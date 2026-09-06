using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Observation;
using DesktopAgent.Windows.Safety;
using Frame = DesktopAgent.Core.Domain.Frame;

namespace DesktopAgent.Windows.Diagnostics;

internal sealed record TextReplacementFixtureState(int ProcessId, long Hwnd, string Text, int SelectionStart,
    int SelectionLength, bool Focused, long TaggedInputMessages, long TextChanges, DateTimeOffset RecordedAtUtc);

/// <summary>Explicit separate-process fixture. Initial text is test arrangement; only real keyboard input
/// changes it afterwards. It represents a filename Edit, never a file on the user's desktop.</summary>
internal sealed class TextReplacementFixtureWindow : Window
{
    private readonly string _statePath;
    private readonly TextBox _editor;
    private readonly DispatcherTimer _stateTimer;
    private readonly System.Threading.Timer _hardDeadline;
    private long _taggedInputMessages, _textChanges;
    private bool _ready;

    internal TextReplacementFixtureWindow(string statePath)
    {
        _statePath = Path.GetFullPath(statePath);
        TextReplacementDiagnostic.CheckDirectory(Path.GetDirectoryName(_statePath)!);
        if (Path.GetFileName(_statePath) != "fixture-state.json" || File.Exists(_statePath)) throw new InvalidOperationException("FIXTURE_PATH_NOT_EMPTY");
        Title = "Desktop Agent · 原生文本替换测试（独立夹具）";
        Width = 720; Height = 310; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White; ShowInTaskbar = true;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "仅测试此输入框，不创建或修改任何真实文件", FontSize = 20, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "验证全选后替换为 325.txt；Ctrl+Alt+F8 暂停，Ctrl+Alt+F9 停止。", Margin = new Thickness(0, 16, 0, 12), TextWrapping = TextWrapping.Wrap });
        _editor = new TextBox { Text = TextReplacementDiagnostic.DamagedName, FontSize = 22, MinHeight = 42,
            MaxLength = ControlCandidate.MaximumValueLength, AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_editor, TextReplacementDiagnostic.EditorName);
        AutomationProperties.SetAutomationId(_editor, "OwnedFilenameReplacementEdit");
        _editor.TextChanged += (_, _) => { if (_ready) _textChanges++; };
        panel.Children.Add(_editor);
        panel.Children.Add(new TextBlock { Text = "这是程序自有测试窗口，最多 40 秒后自行关闭。", Margin = new Thickness(0, 16, 0, 0), Foreground = Brushes.DimGray });
        Content = panel;
        _stateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => { try { WriteState(); } catch { Close(); } }, Dispatcher);
        _stateTimer.Stop();
        _hardDeadline = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        ContentRendered += (_, _) =>
        {
            _editor.Focus(); Keyboard.Focus(_editor);
            _editor.Select(_editor.Text.Length, 0); // Initial fixture arrangement: existing value, insertion point at its end.
            _ready = true;
            try { WriteState(); _stateTimer.Start(); } catch { Close(); }
        };
        Closed += (_, _) =>
        {
            _stateTimer.Stop(); _hardDeadline.Dispose();
            Application.Current.Shutdown();
        };
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_ready && message is 0x0100 or 0x0101 or 0x0102 && unchecked((nuint)GetMessageExtraInfo()) == Win32InputDevice.InputMarker)
            _taggedInputMessages++;
        return 0;
    }
    private void WriteState()
    {
        if (!_ready) return;
        TextReplacementDiagnostic.CheckDirectory(Path.GetDirectoryName(_statePath)!);
        var state = new TextReplacementFixtureState(Environment.ProcessId, new WindowInteropHelper(this).Handle.ToInt64(),
            _editor.Text, _editor.SelectionStart, _editor.SelectionLength, _editor.IsKeyboardFocused,
            _taggedInputMessages, _textChanges, DateTimeOffset.UtcNow);
        string temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, state); stream.Flush(flushToDisk: true); }
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
}

/// <summary>Finite real SendInput/production-UIA replacement in an owned child window only.
/// Deterministic diagnostic actions are not model acceptance or an Explorer file operation.</summary>
internal static class TextReplacementDiagnostic
{
    internal const string DamagedName = "325.tx325325.txt.txtt.txt";
    internal const string EditorName = "测试文件名";
    private const string DesiredName = "325.txt";

    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        try
        {
            CheckDirectory(directory);
            if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
            Directory.CreateDirectory(directory); CheckDirectory(directory);
        }
        catch { return 2; }
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new InputSafetyGate();
        using var deadlineRegistration = deadline.Token.Register(() => gate.Trip(InputStopReason.Deadline));
        var hotkeys = new EmergencyHotkeyService(gate);
        var desktop = new WindowsDesktopObserver(preferForegroundWindow: true);
        using var controls = new WindowsControlObserver(desktop);
        TaskLeaseSession? session = null; LeaseWorker? worker = null; InputRun? run = null;
        CancellationTokenSource? active = null;
        Process? child = null;
        string statePath = Path.Combine(directory, "fixture-state.json"), stage = "PREPARING";
        string? failure = null;
        TextReplacementFixtureState? truth = null, initialTruth = null, selectedTruth = null, finalTruth = null;
        TextReplacementPlan? plan = null;
        bool fullValueRead = false, fullSelectionRead = false, finalValueRead = false, inputCleanup = true, leaseCleanup = true;
        bool hotkeyCleanup = false, pixelsCleared = false, childClosed = false, cancelled = false;
        var actions = new List<ActionResult>();
        CancellationToken Token() => active?.Token ?? deadline.Token;
        async Task Write(string name, object value, CancellationToken ct)
        {
            CheckDirectory(directory);
            await using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true }, ct);
            await stream.FlushAsync(ct);
        }
        TextReplacementFixtureState ReadTruth()
        {
            Token().ThrowIfCancellationRequested();
            if (child is null || child.HasExited) throw new InvalidOperationException("OWNED_FIXTURE_EXITED");
            CheckDirectory(directory);
            if ((File.GetAttributes(statePath) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("FIXTURE_STATE_REPARSE_POINT");
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is < 1 or > 16384) throw new InvalidOperationException("FIXTURE_STATE_SIZE");
            var state = JsonSerializer.Deserialize<TextReplacementFixtureState>(stream) ?? throw new InvalidOperationException("FIXTURE_STATE_INVALID");
            if (state.ProcessId != child.Id || state.Hwnd == 0 || state.Text.Length > ControlCandidate.MaximumValueLength ||
                state.SelectionStart < 0 || state.SelectionLength < 0 || (long)state.SelectionStart + state.SelectionLength > state.Text.Length ||
                DateTimeOffset.UtcNow - state.RecordedAtUtc > TimeSpan.FromSeconds(2) || state.RecordedAtUtc > DateTimeOffset.UtcNow.AddSeconds(1))
                throw new InvalidOperationException("FIXTURE_STATE_INVALID");
            if (truth is not null && (state.Hwnd != truth.Hwnd || state.ProcessId != truth.ProcessId)) throw new InvalidOperationException("FIXTURE_IDENTITY_CHANGED");
            return truth = state;
        }
        async Task<TextReplacementFixtureState> WaitTruth(Func<TextReplacementFixtureState, bool> predicate)
        {
            var waiting = Stopwatch.StartNew();
            while (true)
            {
                var current = ReadTruth();
                if (predicate(current)) return current;
                if (waiting.ElapsedMilliseconds >= 1500) throw new InvalidOperationException("FIXTURE_TRUTH_NOT_MATCHED");
                await Task.Delay(50, Token());
            }
        }
        async Task<DesktopEnvironment> OwnedEnvironment()
        {
            Token().ThrowIfCancellationRequested();
            if (child is null || child.HasExited || truth is null) throw new InvalidOperationException("OWNED_FIXTURE_UNAVAILABLE");
            var environment = await desktop.GetEnvironmentAsync(Token()).WaitAsync(Token());
            if (environment.SessionState != DesktopSessionState.Available || environment.Foreground is not { } foreground ||
                foreground.ProcessId != child.Id || !foreground.HwndHex.Equals(truth.Hwnd.ToString("X"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OWNED_FIXTURE_NOT_FOREGROUND");
            return environment;
        }
        async Task<(Frame Frame, ControlSnapshot Snapshot)> Observe(string name)
        {
            var environment = await OwnedEnvironment();
            var display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, environment.Foreground!.WindowRect)).First();
            var frame = await desktop.CaptureAsync(run!.Lease.Lease, display.Id, null, Token()).WaitAsync(Token());
            _ = await OwnedEnvironment();
            if (frame.Foreground.ProcessId != child!.Id || !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion))
                throw new InvalidOperationException("CAPTURE_OUTSIDE_OWNED_FIXTURE");
            var snapshot = await controls.ObserveAsync(frame, Token()).WaitAsync(Token());
            _ = await OwnedEnvironment();
            var state = ReadTruth();
            await Write(name + ".json", new { atUtc = DateTimeOffset.UtcNow, frame, snapshot, fixtureTruth = state }, Token());
            CheckDirectory(directory);
            await using var image = new FileStream(Path.Combine(directory, name + ".png"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await image.WriteAsync(frame.Image.Bytes, Token());
            await image.FlushAsync(Token());
            return (frame, snapshot);
        }
        async Task Act(string name, Frame frame, AgentAction action)
        {
            stage = name;
            if (actions.Count >= 2) throw new InvalidOperationException("INPUT_ACTION_LIMIT");
            var task = new TaskContext(run!.Lease.Lease.TaskId, run.Lease.Lease.Epoch, "在程序自有测试框中修正文件名为325.txt", DateTimeOffset.UtcNow,
                TaskState.Running, false, "OWNED_NATIVE_DIAGNOSTIC_NO_PROVIDER", frame.MonitorId, new(2, 0, 30000), new(actions.Count, 0, clock.ElapsedMilliseconds, 0, 0), [], null, null);
            var proposal = new Proposal(2, Guid.NewGuid().ToString("N"), task.Id, task.Epoch, frame.Id, "核对独立测试框", "观察真实输入结果",
                new ActDecision(action, "独立夹具的测试文件名", "完整文件名为325.txt"));
            var validation = new DesktopPolicyValidator(TimeSpan.FromSeconds(10), Environment.ProcessId)
                .Validate(task, frame, proposal, await OwnedEnvironment());
            await Write(name + "-before.json", new { atUtc = DateTimeOffset.UtcNow, frame.Id, validation.Code, validation.Disposition,
                action = JsonSerializer.SerializeToElement(action, action.GetType()), fixtureTruth = ReadTruth() }, Token());
            if (validation.Disposition != PolicyDisposition.Allow || validation.Action is null) throw new InvalidOperationException("DIAGNOSTIC_POLICY_REJECTED");
            _ = await OwnedEnvironment();
            var currentControls = await controls.ObserveAsync(frame, Token()).WaitAsync(Token());
            if (action is TextAction)
            {
                string? selectionError = TextReplacement.ValidateSelection(plan!, frame, currentControls);
                if (selectionError is not null) throw new InvalidOperationException(selectionError);
                var current = ReadTruth();
                if (!current.Focused || current.Text != DamagedName || current.SelectionStart != 0 || current.SelectionLength != DamagedName.Length)
                    throw new InvalidOperationException("FIXTURE_SELECTION_CHANGED_BEFORE_TEXT");
            }
            else
            {
                var editor = currentControls.Candidates.SingleOrDefault(c => c.Role == "Edit" && c.Name == EditorName && c.Focused && c.Enabled && c.Focusable);
                if (editor is null || TextReplacement.Prepare(frame, currentControls,
                        new ReplaceTextAction(DesiredName, DamagedName, currentControls.Id, editor.Id), out _) is not null)
                    throw new InvalidOperationException("FIXTURE_EDIT_CHANGED_BEFORE_SELECTION");
            }
            var result = await new WindowsInputExecutor(desktop, gate, run, TimeSpan.FromSeconds(10)).ExecuteAsync(task.Lease, validation.Action, Token());
            actions.Add(result);
            await Write(name + "-completed.json", new { atUtc = DateTimeOffset.UtcNow, result }, CancellationToken.None);
            Token().ThrowIfCancellationRequested();
            if (result.Status != ActionStatus.Injected || result.AppliedEventCount <= 0) throw new InvalidOperationException("NATIVE_INPUT_NOT_APPLIED");
        }
        try
        {
            await Write("run.json", new { atUtc = DateTimeOffset.UtcNow, maximumMs = 30000, maximumInputActions = 2,
                source = "EXPLICIT_OWNED_FIXTURE_DIAGNOSTIC", damagedName = DamagedName, desiredName = DesiredName,
                modelCalls = 0, apiCalls = 0, userFilesTouched = false, mouseActions = 0,
                pause = "Ctrl+Alt+F8", stop = "Ctrl+Alt+F9", inputPolicy = "Ctrl+A then fresh exact full-selection verification before native Unicode text" }, Token());
            stage = "REGISTERING_HOTKEYS";
            var ready = await hotkeys.StartAsync().WaitAsync(Token());
            await Write("hotkeys.json", ready, Token());
            if (!ready.Registered) throw new InvalidOperationException("HOTKEYS_UNAVAILABLE");
            var registry = new TaskLeaseRegistry();
            if (!registry.TryAcquire(Guid.NewGuid(), out session) || !session!.TryStartWorker(session.Current, out worker) ||
                !gate.TryArm(session, session.Current, gate.Status.Revision, TimeSpan.FromSeconds(30), out run)) throw new InvalidOperationException("LEASE_UNAVAILABLE");
            active = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, run!.Token);
            stage = "WAITING_FOR_OWNED_FIXTURE";
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("APP_PATH_UNAVAILABLE");
            // This owned interactive test window must be visible for real screenshot/keyboard validation.
            // CreateNoWindow suppresses a console; the explicit fixture branch shows only its test window.
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--text-replacement-fixture"); start.ArgumentList.Add(statePath);
            child = Process.Start(start) ?? throw new InvalidOperationException("FIXTURE_START_FAILED");
            using (var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(Token()))
            {
                readyDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                while (!File.Exists(statePath))
                {
                    if (child.HasExited) throw new InvalidOperationException("FIXTURE_EXITED_BEFORE_READY");
                    await Task.Delay(50, readyDeadline.Token);
                }
                initialTruth = ReadTruth();
                while (true)
                {
                    var environment = await desktop.GetEnvironmentAsync(readyDeadline.Token);
                    if (environment.SessionState == DesktopSessionState.Available && environment.Foreground?.ProcessId == child.Id && ReadTruth().Focused) break;
                    await Task.Delay(50, readyDeadline.Token);
                }
            }
            if (initialTruth.Text != DamagedName) throw new InvalidOperationException("FIXTURE_INITIAL_VALUE_WRONG");
            var before = await Observe("01-before");
            var candidates = before.Snapshot.Candidates.Where(c => c.Role == "Edit" && c.Name == EditorName && c.Enabled && c.Focused && c.Focusable).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("OWNED_EDIT_NOT_UNIQUE");
            var editor = candidates[0];
            var requested = new ReplaceTextAction(DesiredName, DamagedName, before.Snapshot.Id, editor.Id);
            string? prepareError = TextReplacement.Prepare(before.Frame, before.Snapshot, requested, out plan);
            fullValueRead = prepareError is null && editor.CurrentValue == DamagedName && editor.ValueTruncated == false;
            await Write("01-prepare.json", new { atUtc = DateTimeOffset.UtcNow, fullValueRead, prepareError, expectedUtf16Length = DamagedName.Length }, Token());
            if (prepareError is not null) throw new InvalidOperationException(prepareError);
            await Act("02-select-all", before.Frame, new HotkeyAction([AgentKey.CTRL, AgentKey.A]));
            selectedTruth = await WaitTruth(s => s.Focused && s.Text == DamagedName && s.SelectionStart == 0 && s.SelectionLength == DamagedName.Length);
            var selected = await Observe("03-selected");
            string? selectionError = TextReplacement.ValidateSelection(plan!, selected.Frame, selected.Snapshot);
            fullSelectionRead = selectionError is null;
            await Write("03-selection-verification.json", new { atUtc = DateTimeOffset.UtcNow, fullSelectionRead, selectionError,
                selectionOffsetUnit = "UTF16_CODE_UNITS", expectedUtf16Length = DamagedName.Length, selectedTruth }, Token());
            if (selectionError is not null) throw new InvalidOperationException(selectionError);
            await Act("04-replace", selected.Frame, new TextAction(DesiredName));
            finalTruth = await WaitTruth(s => s.Text == DesiredName);
            var after = await Observe("05-after");
            string? finalError = TextReplacement.VerifyResult(plan!, after.Frame, after.Snapshot);
            finalValueRead = finalError is null;
            await Write("05-result-verification.json", new { atUtc = DateTimeOffset.UtcNow, finalValueRead, finalError, finalTruth,
                expectedName = DesiredName, duplicatedSuffixAbsent = finalTruth.Text == DesiredName }, Token());
            if (finalError is not null) throw new InvalidOperationException(finalError);
            if (finalTruth.TaggedInputMessages <= initialTruth.TaggedInputMessages || finalTruth.TextChanges <= initialTruth.TextChanges)
                throw new InvalidOperationException("FIXTURE_NATIVE_INPUT_EVIDENCE_MISSING");
            stage = "COMPLETED";
        }
        catch (Exception error)
        {
            cancelled = Token().IsCancellationRequested;
            failure = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : error is InvalidOperationException &&
                error.Message.Length is > 0 and <= 120 && error.Message.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
                ? error.Message : "DIAGNOSTIC_FAILED_" + error.GetType().Name;
        }
        finally
        {
            gate.Trip(InputStopReason.Shutdown);
            if (session is not null) { try { await session.RequestCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { leaseCleanup = false; } }
            if (run is not null)
            {
                var drain = Stopwatch.StartNew();
                while (!run.CancellationDrained && drain.ElapsedMilliseconds < 1000) await Task.Delay(10);
                inputCleanup = gate.TryRelease(run);
            }
            worker?.Dispose();
            if (session is not null) leaseCleanup &= session.TryCompleteCleanup();
            active?.Dispose();
            try { await hotkeys.DisposeAsync(); hotkeyCleanup = hotkeys.ThreadExited; } catch { hotkeyCleanup = false; }
            try { await desktop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); pixelsCleared = true; } catch { pixelsCleared = false; }
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.CloseMainWindow();
                        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch (TimeoutException) { if (!child.HasExited) child.Kill(entireProcessTree: false); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                    }
                    childClosed = child.HasExited;
                }
                catch { childClosed = false; }
                finally { child.Dispose(); }
            }
        }
        bool passed = failure is null && fullValueRead && fullSelectionRead && finalValueRead && actions.Count == 2 &&
            inputCleanup && leaseCleanup && hotkeyCleanup && pixelsCleared && childClosed;
        await Write("report.json", new { atUtc = DateTimeOffset.UtcNow, passed, failure, stage, elapsedMs = clock.ElapsedMilliseconds,
            fullValueRead, fullSelectionRead, finalValueRead, actions, initialTruth, selectedTruth, finalTruth,
            inputCleanup, leaseCleanup, hotkeyCleanup, pixelsCleared, childClosed, cancelled,
            modelCalls = 0, apiCalls = 0, userFilesTouched = false, actualExplorerFileTaskPassed = false,
            utf16NonBmpLiveTested = false, evidenceMode = "REAL_SENDINPUT_AND_READONLY_UIA_IN_OWNED_TEST_PROCESS" }, CancellationToken.None);
        return passed ? 0 : 1;
    }
    internal static void CheckDirectory(string directory)
    {
        for (DirectoryInfo? item = new(Path.GetFullPath(directory)); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("REPARSE_PATH_REJECTED");
    }
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0L, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0L, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}
