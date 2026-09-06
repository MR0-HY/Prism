using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Observation;

namespace DesktopAgent.Windows.Diagnostics;

internal sealed record NotepadCloseResult(bool Closed, bool WindowLeftOpen, string? ErrorCode);

/// <summary>Own-file Notepad test preparation and read-only verification. No keyboard, mouse, UIA writes, save, or process kill.</summary>
internal sealed class NotepadTestSession : IDisposable
{
    private readonly Process _process;
    private readonly nint _hwnd;
    private readonly long _startTimeTicks;
    private readonly string _testName;
    private readonly string _directory;
    private readonly BoundedReadWorker _reader = new("Desktop Agent owned Notepad read-only check");
    private readonly SemaphoreSlim _serial = new(1, 1);
    private EditorIdentityDiagnostic? _lastIdentityDiagnostic;
    private bool _disposed;
    public int ProcessId { get; }
    public string HwndHex => _hwnd.ToString("X");
    public string TestFilePath { get; }

    private NotepadTestSession(Process process, nint hwnd, string file, string directory)
    {
        _process = process;
        ProcessId = process.Id;
        _startTimeTicks = process.StartTime.ToUniversalTime().Ticks;
        _hwnd = hwnd;
        TestFilePath = file;
        _testName = Path.GetFileNameWithoutExtension(file);
        _directory = directory;
    }

    public static async Task<NotepadTestSession> StartAsync(string directory, CancellationToken ct)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        Directory.CreateDirectory(directory);
        CheckPath(directory);
        string file = Path.Combine(directory, "DesktopAgent-P09-" + Guid.NewGuid().ToString("N") + ".txt");
        string name = Path.GetFileNameWithoutExtension(file);
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            stream.Flush(flushToDisk: true);
        var baselinePids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("notepad"))
        {
            using (process) baselinePids.Add(process.Id);
        }
        var baselineWindows = VisibleWindows().Where(w => baselinePids.Contains(w.ProcessId)).Select(w => w.Hwnd).ToHashSet();
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, name + "-start.json"), new
        {
            atUtc = DateTimeOffset.UtcNow, testFilePath = file, baselineProcessIds = baselinePids,
            baselineWindowHandles = baselineWindows.Select(h => h.ToString("X")).ToArray(),
            fileBytes = 0, modelCalls = 0, inputInjected = false
        }, ct);
        Process? launcher = null;
        NotepadTestSession? candidate = null;
        bool launched = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            if (!File.Exists(executable)) throw new InvalidOperationException("NOTEPAD_SYSTEM_EXECUTABLE_MISSING");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
            start.ArgumentList.Add(file);
            launcher = Process.Start(start) ?? throw new InvalidOperationException("NOTEPAD_START_FAILED");
            launched = true;
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ready.CancelAfter(TimeSpan.FromSeconds(15));
            while (candidate is null)
            {
                ready.Token.ThrowIfCancellationRequested();
                nint hwnd = GetForegroundWindow();
                if (hwnd != 0 && IsWindowVisible(hwnd) && !IsIconic(hwnd))
                {
                    GetWindowThreadProcessId(hwnd, out uint rawPid);
                    if (rawPid > 0 && rawPid <= int.MaxValue)
                    {
                        Process? process = null;
                        try
                        {
                            process = Process.GetProcessById((int)rawPid);
                            if (string.Equals(process.ProcessName, "notepad", StringComparison.OrdinalIgnoreCase) && TitleContains(hwnd, name))
                            {
                                if (baselinePids.Contains(process.Id) || baselineWindows.Contains(hwnd))
                                    throw new InvalidOperationException("NOTEPAD_REUSED_EXISTING_SESSION");
                                if (!IsUniqueTestWindow(hwnd, process.Id, name))
                                    throw new InvalidOperationException("NOTEPAD_NOT_AN_INDEPENDENT_WINDOW");
                                candidate = new(process, hwnd, file, directory);
                                process = null;
                            }
                        }
                        catch (ArgumentException) { }
                        finally { process?.Dispose(); }
                    }
                }
                if (candidate is null) await Task.Delay(80, ready.Token);
            }
            string initialText = await candidate.ReadOwnedTextAsync(ready.Token);
            if (initialText.Length != 0) throw new InvalidOperationException("NOTEPAD_TEST_EDITOR_NOT_EMPTY");
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, name + "-ready.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, candidate.ProcessId, candidate.HwndHex, candidate.TestFilePath,
                newProcess = true, newWindow = true, uniqueTestFile = true, emptyEditor = true,
                identityDiagnostic = candidate._lastIdentityDiagnostic,
                modelCalls = 0, inputInjected = false
            }, ct);
            ct.ThrowIfCancellationRequested();
            return candidate;
        }
        catch (Exception error)
        {
            candidate?.Dispose();
            string code = error is OperationCanceledException ? "NOTEPAD_PREPARATION_CANCELLED_OR_TIMED_OUT" : SafeError(error);
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, name + "-failure.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, errorCode = code, testFilePath = file,
                windowMayRemainOpen = launched, automaticWindowCleanupAttempted = false,
                identityDiagnostic = candidate?._lastIdentityDiagnostic,
                modelCalls = 0, inputInjected = false, note = "Unproven or reused windows and tabs were left untouched."
            }, CancellationToken.None);
            if (error is OperationCanceledException) throw;
            throw new InvalidOperationException(code);
        }
        finally { launcher?.Dispose(); }
    }

    public async Task<string> ReadOwnedTextAsync(CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (await InspectAsync(requireForeground: true, ct)).Text!;
        }
        finally { _serial.Release(); }
    }

    /// <summary>Reuse the original GUID test file after its tab was manually isolated; never launch a new Notepad instance.</summary>
    public static async Task<NotepadTestSession> OpenPreparedAsync(string originalDirectory, CancellationToken ct, string? expectedInitialText = null)
    {
        originalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalDirectory));
        CheckPath(originalDirectory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        NotepadTestSession? session = null;
        bool activationAttempted = false;
        bool? activationReturned = null;
        string? inspectedText = null;
        bool MatchesInitial(string text) => text.Length == 0 || expectedInitialText is not null && text == expectedInitialText;
        try
        {
            session = await BindOriginalPreparationAsync(originalDirectory, deadline.Token);
            // Confirm the precise isolated document before the one explicitly authorized foreground request.
            var initial = await session.InspectAsync(requireForeground: false, deadline.Token);
            inspectedText = initial.Text;
            if (!MatchesInitial(initial.Text!)) throw new InvalidOperationException("NOTEPAD_PREPARED_DOCUMENT_NOT_EMPTY");
            if (GetForegroundWindow() != session._hwnd)
            {
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(originalDirectory, "prepared-activation-" + Guid.NewGuid().ToString("N") + "-before.json"), new
                {
                    atUtc = DateTimeOffset.UtcNow, session.ProcessId, session.HwndHex, session.TestFilePath,
                    identityDiagnostic = session._lastIdentityDiagnostic, emptyConfirmed = initial.Text!.Length == 0, knownInitialTextMatched = true,
                    operation = "SetForegroundWindow_ONCE", preparationOnly = true, modelAction = false, inputInjected = false
                }, deadline.Token);
                session.EnsureWindowIdentity(requireForeground: false);
                deadline.Token.ThrowIfCancellationRequested();
                activationAttempted = true;
                activationReturned = SetForegroundWindow(session._hwnd);
                var wait = Stopwatch.StartNew();
                while (GetForegroundWindow() != session._hwnd && wait.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(50, deadline.Token);
                if (GetForegroundWindow() != session._hwnd) throw new InvalidOperationException("NOTEPAD_PREPARED_ACTIVATION_FAILED");
            }
            string confirmedText = await session.ReadOwnedTextAsync(deadline.Token);
            if (!MatchesInitial(confirmedText)) throw new InvalidOperationException("NOTEPAD_PREPARED_DOCUMENT_CHANGED");
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(originalDirectory, "prepared-ready-" + Guid.NewGuid().ToString("N") + ".json"), new
            {
                atUtc = DateTimeOffset.UtcNow, session.ProcessId, session.HwndHex, session.TestFilePath,
                identityDiagnostic = session._lastIdentityDiagnostic, emptyConfirmed = confirmedText.Length == 0, knownInitialTextMatched = true, foregroundConfirmed = true,
                reusedOriginalPreparation = true, activationAttempted, activationReturned, modelCalls = 0, inputInjected = false,
                appLaunched = false, screenshotTaken = false, countedAsQualification = false
            }, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return session;
        }
        catch (Exception error)
        {
            session?.Dispose();
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(originalDirectory, "prepared-failure-" + Guid.NewGuid().ToString("N") + ".json"), new
            {
                atUtc = DateTimeOffset.UtcNow, errorCode = error is OperationCanceledException ? "NOTEPAD_PREPARED_CANCELLED_OR_TIMED_OUT" : SafeError(error),
                processId = session?.ProcessId, hwndHex = session?.HwndHex, testFilePath = session?.TestFilePath,
                identityDiagnostic = session?._lastIdentityDiagnostic, activationAttempted, activationReturned,
                inspectedText, inspectedUtf16 = inspectedText?.Select(c => (int)c).ToArray(),
                modelCalls = 0, inputInjected = false, appLaunched = false, screenshotTaken = false, windowLeftUntouchedAfterFailure = true
            }, CancellationToken.None);
            throw;
        }
    }

    private static async Task<NotepadTestSession> BindOriginalPreparationAsync(string originalDirectory, CancellationToken ct)
    {
        var starts = Directory.EnumerateFiles(originalDirectory, "*-start.json", SearchOption.TopDirectoryOnly).ToArray();
        if (starts.Length != 1 || new FileInfo(starts[0]).Length > 64 * 1024 ||
            (File.GetAttributes(starts[0]) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("NOTEPAD_ORIGINAL_START_RECORD_UNAVAILABLE");
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(starts[0], ct));
        var root = document.RootElement;
        string file = Path.GetFullPath(root.GetProperty("testFilePath").GetString()!);
        string name = Path.GetFileNameWithoutExtension(file);
        const string prefix = "DesktopAgent-P09-";
        if (!string.Equals(Path.GetDirectoryName(file), originalDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            !string.Equals(Path.GetFileName(starts[0]), name + "-start.json", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(file) || new FileInfo(file).Length != 0 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("NOTEPAD_ORIGINAL_TEST_FILE_MISMATCH");
        var pids = root.GetProperty("baselineProcessIds");
        var windows = root.GetProperty("baselineWindowHandles");
        if (pids.GetArrayLength() > 1024 || windows.GetArrayLength() > 1024)
            throw new InvalidOperationException("NOTEPAD_ORIGINAL_BASELINE_INVALID");
        var baselinePids = pids.EnumerateArray().Select(p => p.GetInt32()).ToHashSet();
        var baselineWindows = windows.EnumerateArray().Select(w => w.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (baselinePids.Any(pid => pid <= 0)) throw new InvalidOperationException("NOTEPAD_ORIGINAL_BASELINE_INVALID");
        var matches = TestWindows(name);
        if (matches.Count != 1 || IsIconic(matches[0].Hwnd)) throw new InvalidOperationException("NOTEPAD_ORIGINAL_TEST_WINDOW_NOT_UNIQUE");
        var match = matches[0];
        if (baselinePids.Contains(match.ProcessId) || baselineWindows.Contains(match.Hwnd.ToString("X")))
            throw new InvalidOperationException("NOTEPAD_ORIGINAL_WINDOW_WAS_PREEXISTING");
        var process = Process.GetProcessById(match.ProcessId);
        try { return new(process, match.Hwnd, file, originalDirectory); }
        catch { process.Dispose(); throw; }
    }

    /// <summary>Diagnose only the original failed preparation; never launch another app or qualify a model.</summary>
    public static async Task<int> InspectOpenPreparationAsync(string originalDirectory)
    {
        originalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalDirectory));
        CheckPath(originalDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        NotepadTestSession? session = null;
        Process? process = null;
        string? verifiedFile = null, failure = null;
        bool readCompleted = false, empty = false;
        int? textLength = null;
        NotepadCloseResult? close = null;
        try
        {
            var starts = Directory.EnumerateFiles(originalDirectory, "*-start.json", SearchOption.TopDirectoryOnly).ToArray();
            if (starts.Length != 1 || new FileInfo(starts[0]).Length > 64 * 1024 ||
                (File.GetAttributes(starts[0]) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("NOTEPAD_ORIGINAL_START_RECORD_UNAVAILABLE");
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(starts[0], deadline.Token));
            var root = document.RootElement;
            string file = Path.GetFullPath(root.GetProperty("testFilePath").GetString()!);
            string name = Path.GetFileNameWithoutExtension(file);
            const string prefix = "DesktopAgent-P09-";
            if (!string.Equals(Path.GetDirectoryName(file), originalDirectory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
                !string.Equals(Path.GetFileName(starts[0]), name + "-start.json", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(file) || new FileInfo(file).Length != 0 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("NOTEPAD_ORIGINAL_TEST_FILE_MISMATCH");
            verifiedFile = file;
            var pids = root.GetProperty("baselineProcessIds");
            var windows = root.GetProperty("baselineWindowHandles");
            if (pids.GetArrayLength() > 1024 || windows.GetArrayLength() > 1024)
                throw new InvalidOperationException("NOTEPAD_ORIGINAL_BASELINE_INVALID");
            var baselinePids = pids.EnumerateArray().Select(p => p.GetInt32()).ToHashSet();
            var baselineWindows = windows.EnumerateArray().Select(w => w.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (baselinePids.Any(pid => pid <= 0)) throw new InvalidOperationException("NOTEPAD_ORIGINAL_BASELINE_INVALID");
            var matches = TestWindows(name);
            if (matches.Count != 1 || IsIconic(matches[0].Hwnd))
                throw new InvalidOperationException("NOTEPAD_ORIGINAL_TEST_WINDOW_NOT_UNIQUE");
            nint hwnd = matches[0].Hwnd;
            GetWindowThreadProcessId(hwnd, out uint rawPid);
            if (rawPid == 0 || rawPid > int.MaxValue || baselinePids.Contains((int)rawPid) || baselineWindows.Contains(hwnd.ToString("X")))
                throw new InvalidOperationException("NOTEPAD_ORIGINAL_WINDOW_WAS_PREEXISTING");
            process = Process.GetProcessById((int)rawPid);
            if (!string.Equals(process.ProcessName, "notepad", StringComparison.OrdinalIgnoreCase) ||
                !IsUniqueTestWindow(hwnd, process.Id, name))
                throw new InvalidOperationException("NOTEPAD_ORIGINAL_WINDOW_NOT_INDEPENDENT");
            session = new(process, hwnd, file, originalDirectory);
            process = null;
            // This explicit read-only inspection neither captures pixels nor supplies input.
            // Do not steal focus from the user just to diagnose a previously created test tab.
            string text = (await session.InspectAsync(requireForeground: false, deadline.Token)).Text!;
            readCompleted = true; textLength = text.Length; empty = text.Length == 0;
            if (!empty) throw new InvalidOperationException("NOTEPAD_ORIGINAL_TEST_DOCUMENT_NOT_EMPTY");
            close = await session.CloseOwnedWindowAsync(deadline.Token, requireEmpty: true);
        }
        catch (Exception error)
        {
            failure = error is OperationCanceledException ? "NOTEPAD_ORIGINAL_INSPECTION_CANCELLED_OR_TIMED_OUT" : SafeError(error);
        }
        finally { process?.Dispose(); session?.Dispose(); }
        bool passed = failure is null && readCompleted && empty && close?.Closed == true;
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(originalDirectory, "preparation-inspection-" + Guid.NewGuid().ToString("N") + ".json"), new
        {
            atUtc = DateTimeOffset.UtcNow, passed, failure, testFilePath = verifiedFile,
            processId = session?.ProcessId, hwndHex = session?.HwndHex,
            identityDiagnostic = session?._lastIdentityDiagnostic, readCompleted, textLength, empty, close,
            modelCalls = 0, inputInjected = false, appLaunched = false, screenshotTaken = false, countedAsQualification = false,
            note = "Only the original preparation window was inspected. Unproven or nonempty windows were left untouched."
        }, CancellationToken.None);
        return passed ? 0 : 1;
    }

    public async Task<NotepadCloseResult> CloseOwnedWindowAsync(CancellationToken ct = default, bool requireEmpty = false)
    {
        await _serial.WaitAsync(ct);
        NotepadCloseResult result;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsWindow(_hwnd)) result = new(true, false, null);
            else
            {
                var verified = await InspectAsync(requireForeground: false, ct);
                if (requireEmpty && verified.Text!.Length != 0)
                    throw new InvalidOperationException("NOTEPAD_TEST_DOCUMENT_NO_LONGER_EMPTY");
                EnsureWindowIdentity(requireForeground: false);
                ct.ThrowIfCancellationRequested();
                if (!PostMessageW(_hwnd, 0x0010, 0, 0))
                    result = new(false, true, "NOTEPAD_CLOSE_REQUEST_REJECTED");
                else
                {
                    // A normal close may display a save prompt. Never confirm it or close another window.
                    var timeout = Stopwatch.StartNew();
                    while (IsWindow(_hwnd) && timeout.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(50, ct);
                    result = IsWindow(_hwnd) ? new(false, true, "NOTEPAD_CLOSE_REQUIRES_USER_OR_IS_PENDING") : new(true, false, null);
                }
            }
        }
        catch (Exception error)
        {
            result = new(false, IsWindow(_hwnd), error is OperationCanceledException ? "NOTEPAD_CLOSE_CANCELLED" : SafeError(error));
        }
        finally { _serial.Release(); }
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(_directory, _testName + "-close-" + Guid.NewGuid().ToString("N") + ".json"), new
        {
            atUtc = DateTimeOffset.UtcNow, ProcessId, HwndHex, TestFilePath, result,
            saveRequested = false, processKilled = false, inputInjected = false
        }, CancellationToken.None);
        return result;
    }

    private sealed record TabIdentityDiagnostic(bool MatchesTestName, bool ContainsTestName, bool? Selected,
        string? TestNameContainingName, bool NameTruncated);
    private sealed record EditorIdentityDiagnostic(DateTimeOffset CapturedAtUtc, int TabCount, TabIdentityDiagnostic[] Tabs,
        int EditorCount, bool TreeIncomplete, bool DocumentTextRead);
    private sealed record EditorRead(string? Text, string? ErrorCode, EditorIdentityDiagnostic? Identity = null);
    private async Task<EditorRead> InspectAsync(bool requireForeground, CancellationToken ct)
    {
        EnsureWindowIdentity(requireForeground);
        var read = await _reader.ReadAsync(() => ReadEditor(), TimeSpan.FromMilliseconds(1500), ct);
        ct.ThrowIfCancellationRequested();
        EnsureWindowIdentity(requireForeground);
        _lastIdentityDiagnostic = read.Value?.Identity;
        if (read.Status != ReadWorkerStatus.Completed || read.Value is null)
            throw new InvalidOperationException("NOTEPAD_UIA_" + read.Status.ToString().ToUpperInvariant());
        if (read.Value.ErrorCode is not null) throw new InvalidOperationException(read.Value.ErrorCode);
        return read.Value;
    }

    private EditorRead ReadEditor()
    {
        using var dpi = new PhysicalDpiScope();
        var root = AutomationElement.FromHandle(_hwnd);
        if (root.Current.ProcessId != ProcessId) return new(null, "NOTEPAD_UIA_WINDOW_MISMATCH");
        var tabs = new List<AutomationElement>();
        var editors = new List<AutomationElement>();
        var timer = Stopwatch.StartNew();
        int visited = 0;
        bool limited = false;
        var walker = TreeWalker.ControlViewWalker;
        void Walk(AutomationElement parent, int depth)
        {
            if (depth > 24 || visited >= 300 || timer.ElapsedMilliseconds > 850) { limited = true; return; }
            for (var child = walker.GetFirstChild(parent); child is not null; child = walker.GetNextSibling(child))
            {
                if (++visited > 300 || timer.ElapsedMilliseconds > 850) { limited = true; break; }
                var p = child.Current;
                if (p.ProcessId != ProcessId) continue;
                // Include offscreen tab items: hidden old tabs must not be mistaken for an independent test document.
                if (p.ControlType == ControlType.TabItem) tabs.Add(child);
                if (!p.IsOffscreen && !p.IsPassword && p.IsEnabled && p.IsKeyboardFocusable &&
                    (p.ControlType == ControlType.Edit || p.ControlType == ControlType.Document)) editors.Add(child);
                Walk(child, depth + 1);
            }
        }
        Walk(root, 0);
        var tabDiagnostics = tabs.Select(tab =>
        {
            string? name = tab.Current.Name;
            bool contains = name?.Contains(_testName, StringComparison.OrdinalIgnoreCase) == true;
            bool? isSelected = tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selected) && selected is SelectionItemPattern selection
                ? selection.Current.IsSelected : null;
            // Only this run's unpredictable file name can authorize keeping a tab name. Old tab names never leave this reader.
            const int maximumName = 256;
            int kept = contains ? Math.Min(name!.Length, maximumName) : 0;
            if (kept > 0 && kept < name!.Length && char.IsHighSurrogate(name[kept - 1])) kept--;
            return new TabIdentityDiagnostic(IsTestTabName(name), contains, isSelected, contains ? name![..kept] : null,
                contains && kept < name!.Length);
        }).ToArray();
        var identity = new EditorIdentityDiagnostic(DateTimeOffset.UtcNow, tabs.Count, tabDiagnostics, editors.Count, limited, false);
        if (limited) return new(null, "NOTEPAD_UIA_TREE_INCOMPLETE", identity);
        if (tabs.Count > 1 || tabs.Count == 1 && !tabDiagnostics[0].MatchesTestName)
            return new(null, "NOTEPAD_OLD_OR_AMBIGUOUS_TABS_PRESENT", identity);
        if (tabs.Count == 1 && tabDiagnostics[0].Selected != true)
            return new(null, "NOTEPAD_TEST_TAB_NOT_SELECTED", identity);
        if (editors.Count != 1) return new(null, "NOTEPAD_EDITOR_NOT_UNIQUE", identity);
        // Check identity before accessing document content. Old-tab names/content never leave this reader.
        EnsureWindowIdentity(requireForeground: false);
        var editor = editors[0];
        string text;
        if (editor.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern) && textPattern is TextPattern document)
            text = document.DocumentRange.GetText(1002);
        else if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) && valuePattern is ValuePattern value)
            text = value.Current.Value;
        else return new(null, "NOTEPAD_EDITOR_TEXT_UNAVAILABLE", identity);
        identity = identity with { DocumentTextRead = true };
        if (text.Length > 1000) return new(null, "NOTEPAD_TEST_TEXT_TOO_LONG", identity);
        return new(text, null, identity);
    }

    private bool IsTestTabName(string? name)
    {
        if (name is null) return false;
        string value = name.Trim().TrimStart('*').Trim();
        string[] names = [_testName, Path.GetFileName(TestFilePath)];
        string[] suffixes = ["", ". 未修改。", ". 已修改。"];
        return names.Any(test => suffixes.Any(suffix => string.Equals(value, test + suffix, StringComparison.OrdinalIgnoreCase)));
    }

    private void EnsureWindowIdentity(bool requireForeground)
    {
        if (_process.HasExited || _process.StartTime.ToUniversalTime().Ticks != _startTimeTicks ||
            !string.Equals(_process.ProcessName, "notepad", StringComparison.OrdinalIgnoreCase) ||
            !IsWindow(_hwnd) || !IsWindowVisible(_hwnd) || IsIconic(_hwnd) ||
            requireForeground && GetForegroundWindow() != _hwnd || !TitleContains(_hwnd, _testName))
            throw new InvalidOperationException("NOTEPAD_OWNED_WINDOW_CHANGED");
        GetWindowThreadProcessId(_hwnd, out uint pid);
        if (pid != ProcessId || !IsUniqueTestWindow(_hwnd, ProcessId, _testName))
            throw new InvalidOperationException("NOTEPAD_WINDOW_NO_LONGER_INDEPENDENT");
    }

    private static bool IsUniqueTestWindow(nint hwnd, int pid, string name)
    {
        var matches = TestWindows(name);
        return matches.Count == 1 && matches[0].Hwnd == hwnd && matches[0].ProcessId == pid;
    }
    private static List<WindowIdentity> TestWindows(string name)
    {
        var matches = new List<WindowIdentity>();
        foreach (var window in VisibleWindows())
        {
            if (!TitleContains(window.Hwnd, name)) continue;
            try
            {
                using var process = Process.GetProcessById(window.ProcessId);
                if (string.Equals(process.ProcessName, "notepad", StringComparison.OrdinalIgnoreCase)) matches.Add(window);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return matches;
    }

    private sealed record WindowIdentity(nint Hwnd, int ProcessId);
    private static List<WindowIdentity> VisibleWindows()
    {
        var result = new List<WindowIdentity>();
        WindowCallback callback = (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid > 0 && pid <= int.MaxValue) result.Add(new(hwnd, (int)pid));
            }
            return true;
        };
        if (!EnumWindows(callback, 0)) throw new InvalidOperationException("NOTEPAD_WINDOW_INVENTORY_UNAVAILABLE");
        return result;
    }
    private static bool TitleContains(nint hwnd, string name)
    {
        var title = new StringBuilder(512);
        return GetWindowTextW(hwnd, title, title.Capacity) > 0 && title.ToString().Contains(name, StringComparison.OrdinalIgnoreCase);
    }
    private static void CheckPath(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("NOTEPAD_TEST_PATH_REPARSE_POINT");
    }
    private static string SafeError(Exception error) => error is InvalidOperationException && error.Message.Length is > 0 and <= 100 &&
        error.Message.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? error.Message : "NOTEPAD_TEST_CHECK_FAILED";
    public void Dispose() { _disposed = true; _reader.Dispose(); _process.Dispose(); }

    private delegate bool WindowCallback(nint hwnd, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
}
