using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Observation;
using DesktopAgent.Windows.Overlay;
using DesktopAgent.Windows.Safety;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit native-input transport experiment in the original isolated GUID test document.
/// No model, setting workflow, clipboard, UIA write, document save or window close.</summary>
internal static class NotepadNativeTextDiagnostic
{
    private const string OriginalDirectory = @"D:\My-Git-Program\desktop-agent\artifacts\p09h\notepad-preflight";
    private const string PriorReport = @"D:\My-Git-Program\desktop-agent\artifacts\p09h\notepad-checked-first\real\report.json";
    internal static async Task<int> RunAsync(string originalDir, string priorReportPath, string evidenceDir, bool mixedOnly = false)
    {
        var clock = Stopwatch.StartNew();
        string priorText, expectedFile;
        try
        {
            originalDir = Canonical(originalDir); priorReportPath = Path.GetFullPath(priorReportPath); evidenceDir = Canonical(evidenceDir);
            bool rawPreparationRead = SamePath(Path.GetDirectoryName(priorReportPath)!, originalDir) &&
                Path.GetFileName(priorReportPath).StartsWith("prepared-failure-", StringComparison.Ordinal) && Path.GetExtension(priorReportPath) == ".json";
            if (!SamePath(originalDir, OriginalDirectory) || !(SamePath(priorReportPath, PriorReport) || rawPreparationRead)) return 2;
            CheckPath(originalDir); CheckPath(Path.GetDirectoryName(priorReportPath)!); CheckPath(evidenceDir);
            if (Directory.Exists(evidenceDir) && Directory.EnumerateFileSystemEntries(evidenceDir).Any()) return 2;
            using var file = new FileStream(priorReportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 or > 65536 || (File.GetAttributes(priorReportPath) & FileAttributes.ReparsePoint) != 0) return 2;
            using var document = JsonDocument.Parse(file);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) return 2;
            if (rawPreparationRead)
            {
                if (root.GetProperty("errorCode").GetString() != "NOTEPAD_PREPARED_DOCUMENT_NOT_EMPTY" ||
                    root.GetProperty("inputInjected").GetBoolean() || root.GetProperty("modelCalls").GetInt32() != 0 ||
                    root.GetProperty("identityDiagnostic").GetProperty("TabCount").GetInt32() != 1 ||
                    !root.GetProperty("identityDiagnostic").GetProperty("DocumentTextRead").GetBoolean()) return 2;
                var units = root.GetProperty("inspectedUtf16").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                if (units.Length > 1000 || units.Any(v => v < 0 || v > 65535)) return 2;
                priorText = new string(units.Select(v => (char)v).ToArray());
            }
            else
            {
                if (!root.GetProperty("prepared").GetBoolean() || root.GetProperty("offlineOnly").GetBoolean() ||
                    !SamePath(root.GetProperty("preparedFrom").GetString()!, originalDir)) return 2;
                priorText = root.GetProperty("actualText").GetString() ?? throw new InvalidOperationException("PRIOR_ACTUAL_TEXT_UNAVAILABLE");
            }
            expectedFile = Path.GetFullPath(root.GetProperty("testFilePath").GetString()!);
            string name = Path.GetFileNameWithoutExtension(expectedFile);
            const string prefix = "DesktopAgent-P09-";
            if (priorText.Length > 1000 || !SamePath(Path.GetDirectoryName(expectedFile)!, originalDir) ||
                Path.GetExtension(expectedFile) != ".txt" || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return 2;
            Directory.CreateDirectory(evidenceDir); CheckPath(evidenceDir);
        }
        catch { return 2; }

        // The input phase stops at 80 seconds; the remaining ten seconds are reserved for cleanup.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, 80000 - clock.ElapsedMilliseconds)));
        var gate = new InputSafetyGate();
        using var deadlineRegistration = deadline.Token.Register(() => gate.Trip(InputStopReason.Deadline));
        var hotkeys = new EmergencyHotkeyService(gate);
        using var overlay = new DesktopOverlayController(Application.Current.Dispatcher);
        void Stop(InputStopReason reason) { gate.Trip(reason); deadline.Cancel(); }
        overlay.Pause += () => Stop(InputStopReason.Paused);
        overlay.Stop += () => Stop(InputStopReason.Stopped);
        overlay.Chat += () => Stop(InputStopReason.Paused);
        var desktop = new WindowsDesktopObserver(overlay.HideForCaptureAsync, preferForegroundWindow: true);
        using var controls = new WindowsControlObserver(desktop);
        var registry = new TaskLeaseRegistry();
        TaskLeaseSession? leaseSession = null;
        LeaseWorker? worker = null;
        InputRun? run = null;
        NotepadTestSession? notepad = null;
        CancellationTokenSource? active = null;
        var device = new RecordedDevice();
        var cases = new List<CaseResult>();
        var steps = new List<object>();
        string? failure = null, lastActual = null, initialActual = null, finalActual = null;
        string stage = "PREPARING";
        DateTimeOffset? lastReadAt = null;
        bool hotkeysStarted = false, inputCleanup = false, leaseCleanup = false, pixelsCleared = false, cancelled = false;
        int stepIndex = 0;
        CancellationToken Token() => active?.Token ?? deadline.Token;
        Task Write(string name, object value, CancellationToken ct)
        {
            CheckPath(evidenceDir);
            return HybridDiagnosticEvidence.WriteAsync(Path.Combine(evidenceDir, name), value, ct);
        }
        async Task<string> Read(string checkpoint, string? expected = null)
        {
            string actual = await notepad!.ReadOwnedTextAsync(Token());
            lastActual = actual; lastReadAt = DateTimeOffset.UtcNow;
            await Write(checkpoint + "-text.json", new
            {
                atUtc = lastReadAt, actual = TextValue.From(actual), expected = expected is null ? null : TextValue.From(expected),
                matches = expected is null ? (bool?)null : string.Equals(actual, expected, StringComparison.Ordinal),
                notepad.ProcessId, notepad.HwndHex, lease = run?.Lease.Lease
            }, Token());
            if (expected is not null && actual != expected) throw new InvalidOperationException("TEST_TEXT_CHANGED_BEFORE_INPUT");
            return actual;
        }
        async Task<DesktopEnvironment> OwnedEnvironment()
        {
            var environment = await desktop.GetEnvironmentAsync(Token());
            if (environment.SessionState != DesktopSessionState.Available || environment.Foreground?.ProcessId != notepad!.ProcessId ||
                !string.Equals(environment.Foreground.HwndHex, notepad.HwndHex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OWNED_NOTEPAD_NOT_FOREGROUND");
            return environment;
        }
        string? monitorId = null;
        async Task<Frame> Capture(string name)
        {
            _ = await OwnedEnvironment();
            var frame = await desktop.CaptureAsync(run!.Lease.Lease, monitorId!, null, Token());
            if (frame.Foreground.ProcessId != notepad!.ProcessId || !string.Equals(frame.Foreground.HwndHex, notepad.HwndHex, StringComparison.OrdinalIgnoreCase) ||
                !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion)) throw new InvalidOperationException("FRAME_OUTSIDE_TEST_WINDOW");
            _ = await OwnedEnvironment();
            // Verify the isolated GUID tab again before retaining its pixels.
            _ = await Read(name + "-capture-check");
            CheckPath(evidenceDir);
            await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(evidenceDir, name + ".png"), frame.Image.Bytes.ToArray(), Token());
            await Write(name + "-frame.json", new { atUtc = DateTimeOffset.UtcNow, frame }, Token());
            return frame;
        }
        async Task<StepOutcome> Step(string label, AgentAction? requestedAction, string expectedBefore, int textDelayMs)
        {
            Token().ThrowIfCancellationRequested();
            if (!gate.Status.IsOpen || run is null) throw new InvalidOperationException("INPUT_PERMIT_CLOSED");
            string prefix = $"input-{++stepIndex:D2}-{label}";
            await Read(prefix + "-precheck", expectedBefore);
            var frame = await Capture(prefix + "-before");
            var snapshot = await controls.ObserveAsync(frame, Token());
            await Write(prefix + "-controls.json", snapshot, Token());
            var editors = snapshot.Candidates.Where(c => c.Enabled && c.Focusable && c.Role is "Document" or "Edit").ToArray();
            if (snapshot.Status is not (ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial) || editors.Length != 1)
                throw new InvalidOperationException("TEST_EDITOR_CONTROL_NOT_UNIQUE");
            var editor = editors[0];
            AgentAction action;
            if (requestedAction is null)
            {
                var resolved = await controls.ResolveAsync(frame, snapshot, editor.Id, Token());
                if (resolved.Point is not { } point || resolved.ErrorCode is not null) throw new InvalidOperationException("TEST_EDITOR_UNRESOLVED");
                action = new ClickAction(new((point.X - frame.PhysicalRegion.Left) * 1000d / (frame.PhysicalRegion.Width - 1),
                    (point.Y - frame.PhysicalRegion.Top) * 1000d / (frame.PhysicalRegion.Height - 1)), MouseButton.Left, 1);
            }
            else
            {
                if (!editor.Focused) throw new InvalidOperationException("TEST_EDITOR_FOCUS_REQUIRED");
                action = requestedAction;
            }
            var target = new InputTarget(frame.Foreground, frame.PhysicalRegion, Win32InputDevice.VirtualDesktop());
            var keyboard = KeyboardState(notepad!);
            await Write(prefix + "-before-input.json", new
            {
                atUtc = DateTimeOffset.UtcNow, run.Lease.Lease, markerHex = Win32InputDevice.InputMarker.ToString("X"),
                textDelayMs, unicodeOrder = "UTF16_UNIT_DOWN_UP_IN_RUNE_ORDER", target, snapshot, editor, keyboard,
                beforeText = TextValue.From(expectedBefore), actionType = action.GetType().Name,
                action = JsonSerializer.SerializeToElement(action, action.GetType()),
                expectedText = action is TextAction text ? TextValue.From(text.Text) : null,
                source = "EXPLICIT_NATIVE_TRANSPORT_DIAGNOSTIC_NO_MODEL"
            }, Token());
            // Disk evidence precedes a fresh document, focus and foreground check at admission.
            await Read(prefix + "-admission", expectedBefore);
            var current = await OwnedEnvironment();
            string? stale = FrameChecks.Validate(frame, run.Lease.Lease, current, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
            if (stale is not null) throw new InvalidOperationException(stale);
            var freshControls = await controls.ObserveAsync(frame, Token());
            var freshEditors = freshControls.Candidates.Where(c => c.Enabled && c.Focusable && c.Role is "Document" or "Edit").ToArray();
            if (freshEditors.Length != 1 || freshEditors[0].Name != editor.Name || freshEditors[0].Role != editor.Role || freshEditors[0].Bounds != editor.Bounds ||
                requestedAction is not null && !freshEditors[0].Focused) throw new InvalidOperationException("TEST_EDITOR_FOCUS_CHANGED");
            if (requestedAction is null)
            {
                var resolved = await controls.ResolveAsync(frame, freshControls, freshEditors[0].Id, Token());
                if (resolved.Point is not { } point || point != InputCoordinates.ToPhysical(((ClickAction)action).Point, frame.PhysicalRegion))
                    throw new InvalidOperationException("TEST_EDITOR_TARGET_CHANGED");
            }
            await Read(prefix + "-final-precondition", expectedBefore);
            GestureResult? result = null;
            string? actualAfter = null, errorCode = null, afterFrameId = null;
            long executionMs = 0;
            int firstBatch = device.Count;
            var timer = Stopwatch.StartNew();
            try
            {
                Token().ThrowIfCancellationRequested();
                device.SetStep(prefix, run.Lease.Lease);
                result = await new InputGestureRunner(gate, device, textDelayMs).ExecuteAsync(run, target, action, Token());
                executionMs = timer.ElapsedMilliseconds;
                await Task.Delay(150, Token()); // Observation settling only; rune timing is unchanged.
                actualAfter = await Read(prefix + "-after");
                var afterFrame = await Capture(prefix + "-after"); afterFrameId = afterFrame.Id;
                await Read(prefix + "-after-stable", actualAfter);
                if (result.Status != "applied" || !result.CleanupComplete) throw new InvalidOperationException("NATIVE_GESTURE_NOT_APPLIED");
                return new(result, actualAfter, executionMs, afterFrameId);
            }
            catch (Exception error) { errorCode = SafeError(error); throw; }
            finally
            {
                var entry = new
                {
                    atUtc = DateTimeOffset.UtcNow, label, run.Lease.Lease, textDelayMs,
                    beforeText = TextValue.From(expectedBefore), afterText = actualAfter is null ? null : TextValue.From(actualAfter),
                    expectedText = action is TextAction attemptedText ? TextValue.From(attemptedText.Text) : null,
                    executionMs, totalStepMs = timer.ElapsedMilliseconds, result, errorCode, afterFrameId,
                    keyboardAfter = TryKeyboardState(notepad!), batches = device.Since(firstBatch), modelCalls = 0
                };
                steps.Add(entry);
                await Write(prefix + "-completed.json", entry, CancellationToken.None);
            }
        }
        async Task Clear(string label, string knownText, int delay)
        {
            var selected = await Step(label + "-select", new HotkeyAction([AgentKey.CTRL, AgentKey.A]), knownText, delay);
            if (selected.Actual != knownText) throw new InvalidOperationException("TEXT_CHANGED_DURING_SELECT_ALL");
            var erased = await Step(label + "-backspace", new HotkeyAction([AgentKey.BACKSPACE]), knownText, delay);
            if (erased.Actual.Length != 0) throw new InvalidOperationException("TEST_CLEAR_NOT_EMPTY");
        }
        try
        {
            await Write("run.json", new
            {
                atUtc = DateTimeOffset.UtcNow, originalDir, priorReportPath, expectedFile,
                expectedInitialText = TextValue.From(priorText), modelCalls = 0, mixedOnly, maximumTextCases = mixedOnly ? 2 : 6,
                delaysMs = new[] { 4, 40 }, operationDeadlineMs = 80000, totalDeadlineMs = 90000,
                inputMarkerHex = Win32InputDevice.InputMarker.ToString("X"), changesIme = false, changesUnicodeEventOrder = false,
                usesClipboard = false, uiaWrites = false, savesDocument = false, closesWindow = false,
                qualifiesModel = false, retriesTextCases = false
            }, deadline.Token);
            stage = "REGISTERING_HOTKEYS"; hotkeysStarted = true;
            var ready = await hotkeys.StartAsync().WaitAsync(deadline.Token);
            if (!ready.Registered) throw new InvalidOperationException("EMERGENCY_HOTKEYS_UNAVAILABLE");
            stage = "BINDING_ORIGINAL_DOCUMENT";
            notepad = await NotepadTestSession.OpenPreparedAsync(originalDir, deadline.Token, expectedInitialText: priorText);
            if (!SamePath(notepad.TestFilePath, expectedFile)) throw new InvalidOperationException("PRIOR_REPORT_TEST_FILE_MISMATCH");
            device.BindWindow(notepad.HwndHex, Path.GetFileNameWithoutExtension(notepad.TestFilePath));
            initialActual = await Read("initial");
            if (initialActual.Length != 0 && initialActual != priorText) throw new InvalidOperationException("INITIAL_TEXT_NOT_APPROVED");
            var environment = await OwnedEnvironment();
            var display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, environment.Foreground!.WindowRect)).First();
            monitorId = display.Id; overlay.SetDisplay(display);
            stage = "POSITIONING_TEST_WINDOW";
            var previousBounds = environment.Foreground!.WindowRect;
            int width = Math.Min(820, display.WorkArea.Width - 32), height = Math.Min(620, display.WorkArea.Height - 32);
            nint testHwnd = (nint)long.Parse(notepad.HwndHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            using (var dpi = new PhysicalDpiScope())
                if (!SetWindowPos(testHwnd, 0, display.WorkArea.Left + (display.WorkArea.Width - width) / 2,
                    display.WorkArea.Top + (display.WorkArea.Height - height) / 2, width, height, 0x0214))
                    throw new InvalidOperationException("TEST_WINDOW_POSITION_FAILED");
            await Task.Delay(150, deadline.Token);
            await Read("positioned", initialActual);
            environment = await OwnedEnvironment();
            if (!display.WorkArea.Contains(environment.Foreground!.WindowRect)) throw new InvalidOperationException("TEST_WINDOW_NOT_FULLY_VISIBLE");
            await Write("window-preparation.json", new { atUtc = DateTimeOffset.UtcNow, before = previousBounds,
                after = environment.Foreground.WindowRect, preparationOnly = true, modelAction = false,
                operation = "SetWindowPos_NOACTIVATE_NOZORDER_NOOWNERZORDER" }, deadline.Token);
            stage = "COUNTDOWN";
            long revision = gate.Status.Revision;
            for (int seconds = 3; seconds > 0; seconds--)
            {
                if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                overlay.Countdown(seconds); await Task.Delay(1000, deadline.Token);
            }
            if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
            await Read("after-countdown", initialActual);
            if (!registry.TryAcquire(Guid.NewGuid(), out leaseSession) || !leaseSession!.TryStartWorker(leaseSession.Current, out worker) ||
                !gate.TryArm(leaseSession, leaseSession.Current, revision, TimeSpan.FromMilliseconds(Math.Max(1, 80000 - clock.ElapsedMilliseconds)), out run))
                throw new InvalidOperationException("INPUT_LEASE_UNAVAILABLE");
            active = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, run!.Token);
            stage = "FOCUSING_TEST_EDITOR";
            var focused = await Step("focus-editor", null, initialActual, 4);
            if (focused.Actual != initialActual) throw new InvalidOperationException("TEXT_CHANGED_DURING_FOCUS");
            stage = "CLEARING_KNOWN_INITIAL_TEXT";
            await Clear("initial-clear", initialActual, 4);
            (string Name, string Text)[] samples = mixedOnly ? [("mixed", "原生验证 CCJ164 😀")]
                : [("ascii", "CCJ164"), ("cjk", "原生验证"), ("emoji", "😀")];
            foreach (int delay in new[] { 4, 40 })
            {
                foreach (var sample in samples)
                {
                    Token().ThrowIfCancellationRequested();
                    stage = $"CASE_{delay}MS_{sample.Name.ToUpperInvariant()}";
                    string label = $"{delay}ms-{sample.Name}";
                    var outcome = await Step(label, new TextAction(sample.Text), "", delay);
                    var result = new CaseResult(sample.Name, delay, TextValue.From(sample.Text), TextValue.From(outcome.Actual),
                        outcome.Actual == sample.Text, outcome.ExecutionMs, outcome.Result, outcome.AfterFrameId);
                    cases.Add(result);
                    await Write("case-" + label + ".json", result, Token());
                    // A text mismatch is evidence, not a retry. Clear only this exact newly observed test value.
                    stage = "CLEARING_" + label;
                    await Clear(label + "-clear", outcome.Actual, delay);
                }
            }
            stage = "FINAL_EMPTY_CHECK";
            finalActual = await Read("final-empty", "");
            await Capture("final-empty");
            await Read("final-empty-stable", "");
        }
        catch (Exception error) { failure = stage + "_" + SafeError(error); }
        finally
        {
            cancelled = deadline.IsCancellationRequested || hotkeys.MessageCount != 0 || run?.Token.IsCancellationRequested == true;
            gate.Trip(InputStopReason.Stopped); deadline.Cancel(); overlay.HideAll();
            try
            {
                await Write("result-before-cleanup.json", new
                {
                    atUtc = DateTimeOffset.UtcNow, stage, failure, cases, steps, initialText = initialActual is null ? null : TextValue.From(initialActual),
                    lastReadText = lastActual is null ? null : TextValue.From(lastActual), lastReadAt,
                    finalActual = finalActual is null ? null : TextValue.From(finalActual), cancelled, lease = run?.Lease.Lease, modelCalls = 0
                }, CancellationToken.None);
            }
            catch { failure ??= "RESULT_EVIDENCE_FAILED"; }
            try
            {
                if (leaseSession is not null) await leaseSession.RequestCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
                if (run is not null)
                {
                    var until = Stopwatch.StartNew();
                    while (!gate.TryRelease(run) && until.ElapsedMilliseconds < 2000) await Task.Delay(10);
                }
                inputCleanup = run is null || run.IsClosed && run.CancellationDrained && !run.ActionActive && !run.CleanupFailed;
            }
            catch { failure ??= "INPUT_CLEANUP_INCOMPLETE"; }
            worker?.Dispose();
            leaseCleanup = leaseSession is null || leaseSession.TryCompleteCleanup();
            active?.Dispose(); notepad?.Dispose();
            try { await hotkeys.DisposeAsync(); } catch { failure ??= "HOTKEY_CLEANUP_FAILED"; }
            try { await desktop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); pixelsCleared = true; }
            catch { failure ??= "PIXEL_CACHE_CLEANUP_INCOMPLETE"; }
        }
        bool hotkeysReleased = !hotkeysStarted || hotkeys.ThreadExited;
        if (clock.ElapsedMilliseconds > 90000) failure ??= "TOTAL_DEADLINE_EXCEEDED";
        bool completed = failure is null && !cancelled && cases.Count == (mixedOnly ? 2 : 6) && finalActual == "" && inputCleanup && leaseCleanup &&
            hotkeysReleased && pixelsCleared && !gate.Status.IsOpen && gate.Status.Lease is null;
        bool passed = completed && cases.All(c => c.ExactMatch);
        try
        {
            await Write("report.json", new
            {
                atUtc = DateTimeOffset.UtcNow, passed, completed, stage, failure, cancelled, wallElapsedMs = clock.ElapsedMilliseconds,
                originalDir, priorReportPath, expectedFile, expectedInitialText = TextValue.From(priorText),
                initialText = initialActual is null ? null : TextValue.From(initialActual),
                finalActual = finalActual is null ? null : TextValue.From(finalActual), lastReadAt,
                lastReadText = lastActual is null ? null : TextValue.From(lastActual), cases, steps,
                inputMarkerHex = Win32InputDevice.InputMarker.ToString("X"), inputBatches = device.Count,
                modelCalls = 0, modelQualificationPassed = false, settingsVerified = false, inputCleanup, leaseCleanup,
                hotkeysReleased, pixelsCleared, gateClosed = !gate.Status.IsOpen, permitReleased = gate.Status.Lease is null,
                savedDocument = false, closedUserWindow = false, windowRetained = notepad is not null,
                note = "Native transport experiment only. UTF16 arrays preserve lone surrogates that JSON text rendering may replace. SendInput markers identify our emitted batches, not independent receipt by the editor."
            }, CancellationToken.None);
        }
        catch { return 1; }
        return passed ? 0 : 1;
    }

    private sealed record TextValue(string Text, int[] Utf16)
    {
        public static TextValue From(string text) => new(text, text.Select(ch => (int)ch).ToArray());
    }
    private sealed record StepOutcome(GestureResult Result, string Actual, long ExecutionMs, string AfterFrameId);
    private sealed record CaseResult(string Name, int TextDelayMs, TextValue Expected, TextValue Actual, bool ExactMatch,
        long ExecutionMs, GestureResult Result, string AfterFrameId);
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SamePath(string a, string b) => string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);
    private static void CheckPath(string path)
    {
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("EVIDENCE_REPARSE_POINT");
    }
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static string SafeError(Exception error)
    {
        string code = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : error.Message;
        return code.Length is > 0 and <= 100 && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? code : error.GetType().Name;
    }
    private sealed class RecordedDevice : IInputDevice
    {
        private readonly Win32InputDevice _inner = new();
        private readonly List<object> _batches = [];
        private string _step = "NOT_STARTED";
        private string _testName = "";
        private nint _hwnd;
        private Lease _lease;
        public int Count => _batches.Count;
        public void BindWindow(string hwnd, string testName) => (_hwnd, _testName) =
            ((nint)long.Parse(hwnd, NumberStyles.HexNumber, CultureInfo.InvariantCulture), testName);
        public void SetStep(string step, Lease lease) => (_step, _lease) = (step, lease);
        public object[] Since(int index) => _batches.Skip(index).ToArray();
        public bool TargetIsCurrent(InputTarget target)
        {
            if (!_inner.TargetIsCurrent(target) || _hwnd == 0 || _testName.Length == 0) return false;
            var title = new StringBuilder(512);
            return GetWindowTextW(_hwnd, title, title.Capacity) > 0 && title.ToString().Contains(_testName, StringComparison.OrdinalIgnoreCase);
        }
        public bool PointIsOnDisplay(PhysicalPoint point) => _inner.PointIsOnDisplay(point);
        public bool IsKeyDown(int virtualKey) => _inner.IsKeyDown(virtualKey);
        public PhysicalPoint CursorPosition() => _inner.CursorPosition();
        public int Send(ReadOnlySpan<DeviceInput> inputs, PhysicalRect virtualDesktop)
        {
            var atUtc = DateTimeOffset.UtcNow; long timestamp = Stopwatch.GetTimestamp();
            var events = inputs.ToArray(); int? applied = null; string? errorCode = null;
            try { applied = _inner.Send(inputs, virtualDesktop); return applied.Value; }
            catch (Exception error) { errorCode = SafeError(error); throw; }
            finally
            {
                // In-memory only inside the input gate. Disk I/O occurs after the bounded native call returns.
                _batches.Add(new { atUtc, timestamp, step = _step, lease = _lease, markerHex = Win32InputDevice.InputMarker.ToString("X"), events, applied, errorCode });
            }
        }
    }
    private sealed record KeyboardDiagnostic(DateTimeOffset AtUtc, uint ThreadId, uint ProcessId, string KeyboardLayoutHex,
        bool GuiThreadInfoAvailable, string? FocusHwndHex, string? ActiveHwndHex);
    private static KeyboardDiagnostic KeyboardState(NotepadTestSession session)
    {
        nint hwnd = (nint)long.Parse(session.HwndHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        uint thread = GetWindowThreadProcessId(hwnd, out uint pid);
        if (thread == 0 || pid != session.ProcessId) throw new InvalidOperationException("KEYBOARD_TARGET_IDENTITY_CHANGED");
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        bool available = GetGUIThreadInfo(thread, ref info);
        return new(DateTimeOffset.UtcNow, thread, pid, GetKeyboardLayout(thread).ToString("X"), available,
            available ? info.Focus.ToString("X") : null, available ? info.Active.ToString("X") : null);
    }
    private static KeyboardDiagnostic? TryKeyboardState(NotepadTestSession session)
    {
        try { return KeyboardState(session); } catch { return null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
}
