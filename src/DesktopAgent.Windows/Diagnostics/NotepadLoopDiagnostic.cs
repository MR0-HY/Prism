using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Configuration;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Observation;
using DesktopAgent.Windows.Overlay;
using DesktopAgent.Windows.Safety;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit G4-H test of a new empty Notepad document through the production model/input loop.</summary>
internal static class NotepadLoopDiagnostic
{
    internal static async Task<int> RunAsync(string directory, bool offlineOnly, string? preparedFrom = null)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
        Directory.CreateDirectory(directory);
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return 2;
        const string alphabet = "ABCDEFGHJKLMNPQRSTUWXYZ";
        string code = new(Enumerable.Range(0, 3).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        string expected = "原生验证 " + code + RandomNumberGenerator.GetInt32(100, 1000).ToString(CultureInfo.InvariantCulture) + " 😀";
        string goal = "在当前独立测试文件的空白正文里输入一次这段完整文本（不包括引号）：“" + expected + "”。根据输入后的真实画面核对全文与目标完全一致，然后结束。";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var gate = new InputSafetyGate();
        var hotkeys = new EmergencyHotkeyService(gate);
        using var overlay = new DesktopOverlayController(Application.Current.Dispatcher);
        void Stop(InputStopReason reason) { gate.Trip(reason); deadline.Cancel(); }
        overlay.Pause += () => Stop(InputStopReason.Paused);
        overlay.Stop += () => Stop(InputStopReason.Stopped);
        overlay.Chat += () => Stop(InputStopReason.Paused);
        await using var desktop = new WindowsDesktopObserver(overlay.HideForCaptureAsync, preferForegroundWindow: true);
        NotepadTestSession? session = null;
        WindowsControlObserver? controls = null;
        ChatCompletionProvider? realProvider = null;
        RecordingModelProvider? recording = null;
        RecordedInput? recordedInput = null;
        DesktopTaskCoordinator? coordinator = null;
        ProviderProfile? profile = null;
        AgentProgress? final = null;
        NotepadCloseResult? close = null;
        string? actual = null, failure = null;
        string stage = "PREPARING";
        bool prepared = false, objectivePassed = false, loopCleanup = false, hotkeysStarted = false, passed = false;
        var store = new LocalConfigurationStore(LocalConfigurationStore.DefaultRoot);
        try
        {
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "run.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, offlineOnly, goal, expectedText = expected, preparedFrom,
                maximumApiAttempts = offlineOnly ? 0 : 4, budget = new TaskBudget(2, 4, 90000), totalDeadlineSeconds = 120,
                inputScope = "OWNED_NEW_NOTEPAD_LEFT_CLICK_AND_EXACT_TARGET_TEXT_ONLY", automaticallyRetriesUnknownRuns = false
            }, deadline.Token);
            if (!offlineOnly)
            {
                stage = "CHECKING_PROFILE";
                profile = (await store.ReadAsync(deadline.Token)).Single(p => p.ProviderKind == ProviderKind.DeepSeek &&
                    p.Model == "deepseek-v4-flash-vision-exp" && p.BaseUrl.AbsoluteUri.TrimEnd('/') == "https://api.deepseek.com");
                if (!DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: true)) throw new InvalidOperationException("HYBRID_PROBE_REQUIRED");
                hotkeysStarted = true;
                var ready = await hotkeys.StartAsync().WaitAsync(deadline.Token);
                if (!ready.Registered) throw new InvalidOperationException("EMERGENCY_HOTKEYS_UNAVAILABLE");
            }
            stage = "STARTING_NEW_NOTEPAD";
            session = preparedFrom is null ? await NotepadTestSession.StartAsync(directory, deadline.Token)
                : await NotepadTestSession.OpenPreparedAsync(preparedFrom, deadline.Token);
            if (await session.ReadOwnedTextAsync(deadline.Token) != "") throw new InvalidOperationException("NOTEPAD_NOT_EMPTY");
            var environment = await desktop.GetEnvironmentAsync(deadline.Token);
            if (!Owned(environment, session)) throw new InvalidOperationException("NOTEPAD_NOT_FOREGROUND");
            var display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, environment.Foreground!.WindowRect)).First();
            stage = "PREPARING_WINDOW_SIZE";
            var before = environment.Foreground!.WindowRect;
            int width = Math.Min(820, display.WorkArea.Width - 32), height = Math.Min(620, display.WorkArea.Height - 32);
            if (width < 400 || height < 300) throw new InvalidOperationException("NOTEPAD_DISPLAY_TOO_SMALL");
            nint hwnd = (nint)long.Parse(session.HwndHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            using (var dpi = new PhysicalDpiScope())
                if (!SetWindowPos(hwnd, 0, display.WorkArea.Left + (display.WorkArea.Width - width) / 2,
                    display.WorkArea.Top + (display.WorkArea.Height - height) / 2, width, height, 0x0214))
                    throw new InvalidOperationException("NOTEPAD_TEST_WINDOW_RESIZE_FAILED");
            await Task.Delay(150, deadline.Token);
            if (await session.ReadOwnedTextAsync(deadline.Token) != "") throw new InvalidOperationException("NOTEPAD_NOT_EMPTY_AFTER_PREPARATION");
            environment = await desktop.GetEnvironmentAsync(deadline.Token);
            if (!Owned(environment, session) || environment.Foreground!.WindowRect.Width > 900 || environment.Foreground.WindowRect.Height > 700 ||
                !display.Bounds.Contains(environment.Foreground.WindowRect)) throw new InvalidOperationException("NOTEPAD_PREPARED_WINDOW_BOUNDS_INVALID");
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "window-preparation.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, session.ProcessId, session.HwndHex, session.TestFilePath,
                before, after = environment.Foreground.WindowRect, preparationOnly = true, modelAction = false,
                operation = "SetWindowPos_NOACTIVATE_NOZORDER_NOOWNERZORDER", emptyConfirmed = true
            }, deadline.Token);
            prepared = true;
            overlay.SetDisplay(display);
            var scoped = new OwnedObserver(desktop, session, directory);
            controls = new WindowsControlObserver(scoped);
            if (offlineOnly)
            {
                stage = "OFFLINE_OBSERVATION";
                var frame = await scoped.CaptureAsync(new(Guid.NewGuid(), 0), display.Id, null, deadline.Token);
                var snapshot = await controls.ObserveAsync(frame, deadline.Token);
                actual = await session.ReadOwnedTextAsync(deadline.Token);
                objectivePassed = actual.Length == 0 && snapshot.Status is (ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial) &&
                    snapshot.Candidates.Any(c => c.Enabled && c.Role is "Edit" or "Document") && !gate.Status.IsOpen && gate.Status.Lease is null;
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "offline-observation.json"), new { frame, snapshot, empty = actual.Length == 0, objectivePassed }, deadline.Token);
            }
            else
            {
                stage = "COUNTDOWN";
                long revision = gate.Status.Revision;
                for (int second = 3; second > 0; second--)
                {
                    if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                    overlay.Countdown(second);
                    await Task.Delay(1000, deadline.Token);
                }
                if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                _ = await session.ReadOwnedTextAsync(deadline.Token);
                realProvider = new ChatCompletionProvider(profile!, store);
                recording = new(realProvider, directory, session.ProcessId, session.HwndHex);
                var model = new OwnedModel(recording, session);
                var ttl = TimeSpan.FromMilliseconds(profile!.FrameTtlMs);
                coordinator = new(gate, scoped, controls, model, profile,
                    new NotepadPolicy(new DesktopPolicyValidator(ttl, Environment.ProcessId), session, expected),
                    run => recordedInput = new(new WindowsInputExecutor(desktop, gate, run, ttl), session, directory, expected),
                    overlay: overlay, controlsRequired: true);
                coordinator.Changed += progress => Application.Current.Dispatcher.BeginInvoke(new Action(() => overlay.Update(progress, true)));
                stage = "MODEL_LOOP";
                await coordinator.StartAsync(new(goal, ProviderConfiguration.Fingerprint(profile), display.Id, new(2, 4, 90000), revision), deadline.Token);
                await coordinator.Completion.WaitAsync(deadline.Token);
                final = coordinator.Progress;
                stage = "VERIFYING_TEXT";
                actual = await session.ReadOwnedTextAsync(deadline.Token);
                objectivePassed = final?.Task.State == TaskState.Succeeded && actual == expected && recordedInput?.InjectedTextCount == 1 &&
                    final.Task.CleanupComplete && !gate.Status.IsOpen && gate.Status.Lease is null;
            }
            // Preserve the result while the document is still present. Closing cannot substitute for verification.
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "result-before-close.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, offlineOnly, prepared, objectivePassed, expectedText = expected, actualText = actual, final,
                apiAttempts = recording?.ApiAttempts ?? 0, injectedTextActions = recordedInput?.InjectedTextCount ?? 0,
                gateClosed = !gate.Status.IsOpen, permitReleased = gate.Status.Lease is null
            }, CancellationToken.None);
        }
        catch (Exception error) { failure = stage + "_" + SafeError(error); }
        finally
        {
            gate.Trip(InputStopReason.Stopped);
            overlay.HideAll();
            if (coordinator?.Progress is { } active)
            {
                try
                {
                    await coordinator.StopAsync(active.Task.Id).WaitAsync(TimeSpan.FromSeconds(4));
                    await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                }
                catch { failure ??= "COORDINATOR_CLEANUP_INCOMPLETE"; }
            }
            loopCleanup = !gate.Status.IsOpen && gate.Status.Lease is null &&
                (coordinator is null || coordinator.Completion.IsCompleted && coordinator.Progress?.Task.CleanupComplete == true);
            // Failure evidence is also saved before requesting a normal close, with no document content guessed.
            try
            {
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "cleanup-before-close.json"), new
                {
                    atUtc = DateTimeOffset.UtcNow, failure, prepared, stage, objectivePassed, loopCleanup,
                    expectedText = expected, actualText = actual, final, apiAttempts = recording?.ApiAttempts ?? 0,
                    gateClosed = !gate.Status.IsOpen, permitReleased = gate.Status.Lease is null
                }, CancellationToken.None);
                if (session is not null && loopCleanup && preparedFrom is not null)
                    close = new(false, true, "NOTEPAD_PREPARED_WINDOW_RETAINED");
                else if (session is not null && loopCleanup) close = await session.CloseOwnedWindowAsync();
                else if (session is not null) close = new(false, true, "INPUT_CLEANUP_NOT_COMPLETE_WINDOW_LEFT_OPEN");
            }
            catch { failure ??= "NOTEPAD_RESULT_OR_CLOSE_RECORD_FAILED"; }
            controls?.Dispose(); realProvider?.Dispose(); session?.Dispose();
            try { await hotkeys.DisposeAsync(); }
            catch { failure ??= "HOTKEY_CLEANUP_FAILED"; }
        }
        bool hotkeysReleased = !hotkeysStarted || hotkeys.ThreadExited;
        passed = objectivePassed && prepared && loopCleanup && hotkeysReleased && failure is null && !deadline.IsCancellationRequested;
        // Only an ordinary pending close/save choice is an explained leftover window.
        // Identity changes and failed UIA checks must not masquerade as verified cleanup.
        passed &= close?.Closed == true || !offlineOnly && close?.ErrorCode == "NOTEPAD_CLOSE_REQUIRES_USER_OR_IS_PENDING" ||
            preparedFrom is not null && close?.ErrorCode == "NOTEPAD_PREPARED_WINDOW_RETAINED";
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "report.json"), new
        {
            atUtc = DateTimeOffset.UtcNow, passed, offlineOnly, prepared, preparedFrom, stage, failure, objectivePassed,
            expectedText = expected, actualText = actual, final, apiAttempts = recording?.ApiAttempts ?? 0,
            profileFingerprint = profile is null ? null : ProviderConfiguration.Fingerprint(profile), model = profile?.Model, baseUrl = profile?.BaseUrl,
            usage = final?.Task.Usage, injectedTextActions = recordedInput?.InjectedTextCount ?? 0,
            inputEverStarted = recordedInput?.ExecutionCalls > 0, gateClosed = !gate.Status.IsOpen, permitReleased = gate.Status.Lease is null,
            loopCleanup, hotkeysReleased, notepadClose = close, testFilePath = session?.TestFilePath,
            windowLeftOpenNote = close?.ErrorCode == "NOTEPAD_PREPARED_WINDOW_RETAINED" ? "已绑定的独立测试窗口保留，供结果检查及后续测试；无活动输入。" :
                close?.WindowLeftOpen == true ? "测试文档仍开着，可能在等待保存选择；未自动点击、保存或结束进程。" : null,
            evidenceMode = offlineOnly ? "NEW_EMPTY_NOTEPAD_REAL_OBSERVATION_NO_MODEL_NO_INPUT" : "REAL_MODEL_REAL_SENDINPUT_NEW_NOTEPAD",
            settingsVerified = false, pureVisionVerified = false
        }, CancellationToken.None);
        return passed ? 0 : 1;
    }

    private static bool Owned(DesktopEnvironment environment, NotepadTestSession session) => environment.SessionState == DesktopSessionState.Available &&
        environment.Foreground?.ProcessId == session.ProcessId && string.Equals(environment.Foreground.HwndHex, session.HwndHex, StringComparison.OrdinalIgnoreCase);
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static string SafeError(Exception error)
    {
        string code = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : error is ProviderCallException call ? call.Code : error.Message;
        return code.Length is > 0 and <= 100 && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? code : error.GetType().Name;
    }

    private sealed class OwnedObserver(WindowsDesktopObserver inner, NotepadTestSession session, string directory) : IDesktopObserver
    {
        private int _count;
        public async Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct)
        {
            var environment = await inner.GetEnvironmentAsync(ct);
            if (!Owned(environment, session)) throw new InvalidOperationException("NOTEPAD_NOT_FOREGROUND");
            return environment;
        }
        public async Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? region, CancellationToken ct)
        {
            _ = await session.ReadOwnedTextAsync(ct);
            _ = await GetEnvironmentAsync(ct);
            int index = _count++;
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"observation-{index:D3}-before.json"), new
            { atUtc = DateTimeOffset.UtcNow, session.ProcessId, session.HwndHex, lease, monitorId, requestedRegion = region }, ct);
            var frame = await inner.CaptureAsync(lease, monitorId, region, ct);
            _ = await session.ReadOwnedTextAsync(ct);
            if (frame.Foreground.ProcessId != session.ProcessId || !string.Equals(frame.Foreground.HwndHex, session.HwndHex, StringComparison.OrdinalIgnoreCase) ||
                !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion)) throw new InvalidOperationException("NOTEPAD_FRAME_OUTSIDE_OWNED_WINDOW");
            await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(directory, $"observation-{index:D3}.png"), frame.Image.Bytes.ToArray(), ct);
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"observation-{index:D3}-completed.json"), new { atUtc = DateTimeOffset.UtcNow, frame }, ct);
            return frame;
        }
    }

    private sealed class OwnedModel(IModelProvider inner, NotepadTestSession session) : IModelProvider
    {
        public async Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
        {
            _ = await session.ReadOwnedTextAsync(ct);
            return await inner.DecideAsync(request, ct);
        }
        public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class NotepadPolicy(IPolicyValidator inner, NotepadTestSession session, string expected) : IPolicyValidator
    {
        public PolicyResult Validate(TaskContext task, Frame frame, Proposal proposal, DesktopEnvironment environment)
        {
            if (!Owned(environment, session)) return new(PolicyDisposition.Wait, "NOTEPAD_NOT_FOREGROUND", "独立记事本已失去前台，测试已停止。", null);
            if (proposal.Decision is ActDecision act)
            {
                bool allowed = act.Action is ClickAction { Button: MouseButton.Left, ClickCount: 1 } ||
                    act.Action is TextAction text && text.Text == expected;
                if (!allowed) return new(PolicyDisposition.Wait, "NOTEPAD_TEST_ACTION_SCOPE", "此测试只允许一次左键点击和输入指定完整测试文本。", null);
            }
            return inner.Validate(task, frame, proposal, environment);
        }
    }

    private sealed class RecordedInput(IInputExecutor inner, NotepadTestSession session, string directory, string expected) : IInputExecutor
    {
        private int _count, _injectedText, _clickCalls, _executionCalls;
        public int Attempts => _count;
        public int ExecutionCalls => _executionCalls;
        public int InjectedTextCount => _injectedText;
        public async Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
        {
            string before = await session.ReadOwnedTextAsync(ct);
            if (action.Action is TextAction text && (text.Text != expected || before.Length != 0 || _injectedText != 0))
                throw new InvalidOperationException("NOTEPAD_TEXT_PRECONDITION_CHANGED");
            if (action.Action is ClickAction && _clickCalls != 0)
                throw new InvalidOperationException("NOTEPAD_SINGLE_CLICK_LIMIT");
            int index = _count++;
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"input-{index:D2}-before.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, action.Lease, action.FrameId, action.ProposalId, beforeText = before,
                actionType = action.Action.GetType().Name, action = JsonSerializer.SerializeToElement(action.Action, action.Action.GetType())
            }, ct);
            ActionResult? result = null;
            string? after = null, errorCode = null;
            try
            {
                _executionCalls++;
                if (action.Action is ClickAction) _clickCalls++;
                result = await inner.ExecuteAsync(lease, action, ct);
                if (action.Action is TextAction && result.Status == ActionStatus.Injected) _injectedText++;
                after = await session.ReadOwnedTextAsync(ct);
                return result;
            }
            catch (Exception error) { errorCode = SafeError(error); throw; }
            finally
            {
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"input-{index:D2}-completed.json"), new
                { atUtc = DateTimeOffset.UtcNow, result, afterText = after, errorCode, injectedTextActions = _injectedText }, CancellationToken.None);
            }
        }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
