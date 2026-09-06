using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
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

/// <summary>Explicit qualification entry; production coordinator drives only fresh independent test windows.</summary>
internal static class AssistedLoopDiagnostic
{
    private const string Goal = "在当前操作验证窗口里，把星号标记的那一项从未开启改为已开启，只点击一次，其他项目保持原样；根据操作后的画面确认结果。";
    private const int RequestsPerCase = 3;

    internal static async Task<int> RunAsync(string fixtureExe, string directory, bool offlineOnly, bool preflightHotkeys = false, int? onlyLayout = null)
    {
        fixtureExe = Path.GetFullPath(fixtureExe); directory = Path.GetFullPath(directory);
        if (Path.GetFileName(fixtureExe) != "DesktopAgent.NativeFixture.exe" || !File.Exists(fixtureExe)) return 2;
        if (onlyLayout is not (null or 0 or 1)) return 2;
        int[] layouts = onlyLayout is { } selectedLayout ? [selectedLayout] : [0, 1];
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
        Directory.CreateDirectory(directory);
        int deadlineSeconds = layouts.Length == 1 ? 110 : 210;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(deadlineSeconds));
        var gate = new InputSafetyGate();
        var hotkeys = new EmergencyHotkeyService(gate);
        using var overlay = new DesktopOverlayController(Application.Current.Dispatcher);
        void StopDiagnostic(InputStopReason reason) { gate.Trip(reason); deadline.Cancel(); }
        overlay.Pause += () => StopDiagnostic(InputStopReason.Paused);
        overlay.Stop += () => StopDiagnostic(InputStopReason.Stopped);
        overlay.Chat += () => StopDiagnostic(InputStopReason.Paused);
        var results = new List<object>();
        string? failure = null; bool passed = true; int apiAttempts = 0;
        ProviderProfile? profile = null;
        var store = new LocalConfigurationStore(LocalConfigurationStore.DefaultRoot);
        try
        {
            if (!offlineOnly)
            {
                profile = (await store.ReadAsync(deadline.Token)).Single(p => p.ProviderKind == ProviderKind.DeepSeek &&
                    p.Model == "deepseek-v4-flash-vision-exp" && p.BaseUrl.AbsoluteUri.TrimEnd('/') == "https://api.deepseek.com");
                if (!DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: true)) throw new InvalidOperationException("HYBRID_PROBE_REQUIRED");
            }
            if (!offlineOnly || preflightHotkeys)
            {
                var ready = await hotkeys.StartAsync().WaitAsync(deadline.Token);
                if (!ready.Registered) throw new InvalidOperationException("EMERGENCY_HOTKEYS_UNAVAILABLE");
            }
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "run.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, offlineOnly, requestedLayouts = layouts, goal = Goal, maximumApiAttempts = offlineOnly ? 0 : layouts.Length * RequestsPerCase,
                perCaseBudget = new TaskBudget(1, RequestsPerCase, 90000), totalDeadlineSeconds = deadlineSeconds,
                profileFingerprint = profile is null ? null : ProviderConfiguration.Fingerprint(profile),
                model = profile?.Model, inputSource = "REAL_MODEL_PRODUCTION_COORDINATOR_NOT_FIXTURE_TRUTH",
                userSettingsChanged = false, automaticallyRetriesUnknownRuns = false
            }, deadline.Token);
            foreach (int layout in layouts)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (hotkeys.MessageCount != 0) throw new OperationCanceledException();
                string caseDirectory = Path.Combine(directory, $"case-{layout:D2}");
                Directory.CreateDirectory(caseDirectory);
                string truthPath = Path.Combine(caseDirectory, "fixture.json");
                var start = new ProcessStartInfo(fixtureExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(fixtureExe)! };
                start.ArgumentList.Add("--assisted-loop-fixture"); start.ArgumentList.Add(truthPath); start.ArgumentList.Add(layout.ToString());
                deadline.Token.ThrowIfCancellationRequested();
                using var child = Process.Start(start) ?? throw new InvalidOperationException("FIXTURE_START_FAILED");
                await using var desktop = new WindowsDesktopObserver(overlay.HideForCaptureAsync, preferForegroundWindow: true);
                using var controls = new WindowsControlObserver(desktop);
                DesktopTaskCoordinator? coordinator = null;
                ChatCompletionProvider? realProvider = null;
                RecordingModelProvider? recordedProvider = null;
                AgentProgress? final = null;
                bool objectivePassed = false, casePassed = false, closed = false;
                JsonElement? truth = null;
                string? fixtureNonce = null;
                string? caseFailure = null;
                string stage = "WAITING_FIXTURE_FILE";
                object? lastEnvironment = null;
                try
                {
                    using var readiness = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    readiness.CancelAfter(TimeSpan.FromSeconds(10));
                    while (!File.Exists(truthPath)) { if (child.HasExited) throw new InvalidOperationException("FIXTURE_EXITED"); await Task.Delay(50, readiness.Token); }
                    var initial = ReadTruth(truthPath);
                    fixtureNonce = initial.GetProperty("nonce").GetString();
                    if (initial.GetProperty("processId").GetInt32() != child.Id || initial.GetProperty("layout").GetInt32() != layout ||
                        initial.GetProperty("totalClicks").GetInt32() != 0) throw new InvalidOperationException("FIXTURE_INITIAL_STATE");
                    string hwnd = initial.GetProperty("hwnd").GetInt64().ToString("X");
                    stage = "WAITING_FIXTURE_FOREGROUND";
                    DesktopEnvironment environment;
                    while (true)
                    {
                        environment = await desktop.GetEnvironmentAsync(readiness.Token);
                        lastEnvironment = new { session = environment.SessionState, processId = environment.Foreground?.ProcessId, processName = environment.Foreground?.ProcessName,
                            hwnd = environment.Foreground?.HwndHex, expectedPid = child.Id, expectedHwnd = hwnd };
                        if (Owned(environment, child.Id, hwnd)) break;
                        await Task.Delay(50, readiness.Token);
                    }
                    var display = environment.Displays.Single(d => d.Bounds.Contains(environment.Foreground!.WindowRect));
                    overlay.SetDisplay(display);
                    var scoped = new FixtureObserver(desktop, child.Id, hwnd);
                    if (offlineOnly)
                    {
                        stage = "OFFLINE_CAPTURE";
                        var frame = await scoped.CaptureAsync(new(Guid.NewGuid(), 0), display.Id, null, readiness.Token);
                        var snapshot = await controls.ObserveAsync(frame, readiness.Token);
                        string expectedName = initial.GetProperty("expectedTargetName").GetString()!;
                        var candidates = snapshot.Candidates.Where(c => c.Name.Contains(expectedName, StringComparison.Ordinal) && c.Enabled && c.Role is "Button" or "CheckBox").ToArray();
                        var candidate = candidates.Single();
                        var resolution = await controls.ResolveAsync(frame, snapshot, candidate.Id, readiness.Token);
                        string? stale = resolution.Point is { } point ? await desktop.CheckPointAsync(frame, frame.Lease,
                            new((point.X - frame.PhysicalRegion.Left) * 1000d / (frame.PhysicalRegion.Width - 1),
                                (point.Y - frame.PhysicalRegion.Top) * 1000d / (frame.PhysicalRegion.Height - 1)), TimeSpan.FromSeconds(90), readiness.Token) : "UNRESOLVED";
                        casePassed = resolution.Point is not null && resolution.ErrorCode is null && stale is null && frame.ViewKind == FrameViewKind.Overview &&
                            frame.PhysicalRegion == Win32DesktopEnvironment.VisibleWindowBounds(frame.Foreground) && !gate.Status.IsOpen;
                        await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(caseDirectory, "observation.png"), frame.Image.Bytes.ToArray(), readiness.Token);
                        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(caseDirectory, "observation.json"), new { frame, snapshot, resolution, stale, casePassed }, readiness.Token);
                    }
                    else
                    {
                        stage = "COUNTDOWN";
                        long startRevision = gate.Status.Revision;
                        for (int second = 3; second > 0; second--)
                        {
                            if (hotkeys.MessageCount != 0) throw new OperationCanceledException();
                            overlay.Countdown(second); await Task.Delay(1000, deadline.Token);
                        }
                        if (hotkeys.MessageCount != 0) throw new OperationCanceledException();
                        deadline.Token.ThrowIfCancellationRequested();
                        realProvider = new ChatCompletionProvider(profile!, store);
                        recordedProvider = new(realProvider, caseDirectory, child.Id, hwnd);
                        var ttl = TimeSpan.FromMilliseconds(profile!.FrameTtlMs);
                        coordinator = new(gate, scoped, controls, recordedProvider, profile,
                            new FixturePolicy(new DesktopPolicyValidator(ttl, Environment.ProcessId), child.Id, hwnd),
                            run => new RecordedInput(new WindowsInputExecutor(desktop, gate, run, ttl), scoped, caseDirectory),
                            overlay: overlay, controlsRequired: true);
                        coordinator.Changed += progress => Application.Current.Dispatcher.BeginInvoke(new Action(() => overlay.Update(progress, true)));
                        stage = "MODEL_LOOP";
                        await coordinator.StartAsync(new(Goal, ProviderConfiguration.Fingerprint(profile), display.Id, new(1, RequestsPerCase, 90000), startRevision), deadline.Token);
                        await coordinator.Completion.WaitAsync(deadline.Token);
                        final = coordinator.Progress;
                        truth = ReadTruth(truthPath);
                        objectivePassed = truth.Value.GetProperty("targetClicks").GetInt32() == 1 && truth.Value.GetProperty("targetChecked").GetBoolean() &&
                            truth.Value.GetProperty("nonTargetClicks").GetInt32() == 0 && truth.Value.GetProperty("totalClicks").GetInt32() == 1;
                        casePassed = objectivePassed && final?.Task.State == TaskState.Succeeded && final.Task.CleanupComplete &&
                            final.Task.Usage.ActionsAttempted == 1 && final.Task.RecentResults.Count(r => r.Status == ActionStatus.Injected) == 1 &&
                            !gate.Status.IsOpen && gate.Status.Lease is null;
                    }
                }
                catch (Exception ex) { caseFailure = stage + "_" + SafeError(ex); }
                finally
                {
                    gate.Trip(InputStopReason.Stopped);
                    if (coordinator?.Progress is { } active)
                    {
                        try { await coordinator.StopAsync(active.Task.Id).WaitAsync(TimeSpan.FromSeconds(4)); }
                        catch { caseFailure ??= "COORDINATOR_CLEANUP"; }
                    }
                    if (recordedProvider is not null) apiAttempts += recordedProvider.ApiAttempts;
                    realProvider?.Dispose(); overlay.HideAll();
                    try
                    {
                        if (!child.HasExited)
                        {
                            child.CloseMainWindow();
                            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                            catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                        }
                        closed = child.HasExited;
                        if (File.Exists(truthPath)) truth = ReadTruth(truthPath);
                    }
                    catch { caseFailure ??= "OWNED_FIXTURE_CLEANUP"; }
                }
                if (!offlineOnly && truth is { } settledTruth)
                {
                    objectivePassed = settledTruth.GetProperty("processId").GetInt32() == child.Id && settledTruth.GetProperty("layout").GetInt32() == layout &&
                        settledTruth.GetProperty("nonce").GetString() == fixtureNonce && settledTruth.GetProperty("closed").GetBoolean() &&
                        settledTruth.GetProperty("targetClicks").GetInt32() == 1 && settledTruth.GetProperty("targetChecked").GetBoolean() &&
                        settledTruth.GetProperty("nonTargetClicks").GetInt32() == 0 && settledTruth.GetProperty("totalClicks").GetInt32() == 1;
                    casePassed &= objectivePassed;
                }
                casePassed &= closed && caseFailure is null && !deadline.IsCancellationRequested && !gate.Status.IsOpen && gate.Status.Lease is null;
                var result = new { layout, passed = casePassed, objectivePassed, final, truth, failure = caseFailure, stage, lastEnvironment, fixtureClosed = closed,
                    gateClosed = !gate.Status.IsOpen, permitReleased = gate.Status.Lease is null, apiAttempts = recordedProvider?.ApiAttempts ?? 0 };
                results.Add(result);
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(caseDirectory, "result.json"), result, CancellationToken.None);
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "progress.json"), new { results, apiAttempts }, CancellationToken.None);
                if (!casePassed) { passed = false; break; }
            }
        }
        catch (Exception ex) { failure = SafeError(ex); passed = false; }
        finally
        {
            gate.Trip(InputStopReason.Shutdown); overlay.HideAll();
            try { await hotkeys.DisposeAsync(); }
            catch { failure ??= "HOTKEY_CLEANUP"; passed = false; }
        }
        passed &= results.Count == layouts.Length;
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "report.json"), new
        {
            atUtc = DateTimeOffset.UtcNow, passed, offlineOnly, requestedLayouts = layouts, failure, apiAttempts, results, hotkeysReleased = offlineOnly || hotkeys.ThreadExited,
            evidenceMode = offlineOnly ? "INDEPENDENT_NATIVE_WINDOW_OBSERVATION_NO_MODEL_NO_INPUT" : "REAL_MODEL_REAL_SENDINPUT_INDEPENDENT_FIXTURE",
            notepadVerified = false, settingsVerified = false, pureVisionVerified = false
        }, CancellationToken.None);
        return passed ? 0 : 1;
    }

    private static JsonElement ReadTruth(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path)); return document.RootElement.Clone();
    }
    private static bool Owned(DesktopEnvironment env, int pid, string hwnd) => env.SessionState == DesktopSessionState.Available &&
        env.Foreground?.ProcessId == pid && string.Equals(env.Foreground.HwndHex, hwnd, StringComparison.OrdinalIgnoreCase);
    private static string SafeError(Exception ex) => ex is OperationCanceledException ? "CANCELLED_OR_DEADLINE" :
        ex is ProviderCallException call ? call.Code : ex is InvalidOperationException && ex.Message.All(c => char.IsAsciiLetterUpper(c) || c == '_') ? ex.Message : ex.GetType().Name;

    private sealed class FixtureObserver(WindowsDesktopObserver inner, int pid, string hwnd) : IDesktopObserver
    {
        public async Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct)
        {
            var env = await inner.GetEnvironmentAsync(ct);
            if (!Owned(env, pid, hwnd)) throw new InvalidOperationException("FIXTURE_NOT_FOREGROUND");
            return env;
        }
        public async Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? region, CancellationToken ct)
        {
            _ = await GetEnvironmentAsync(ct);
            var frame = await inner.CaptureAsync(lease, monitorId, region, ct);
            if (frame.Foreground.ProcessId != pid || !string.Equals(frame.Foreground.HwndHex, hwnd, StringComparison.OrdinalIgnoreCase) ||
                !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion)) throw new InvalidOperationException("FIXTURE_FRAME_SCOPE");
            return frame;
        }
    }
    private sealed class FixturePolicy(IPolicyValidator inner, int pid, string hwnd) : IPolicyValidator
    {
        public PolicyResult Validate(TaskContext task, Frame frame, Proposal proposal, DesktopEnvironment env)
        {
            if (!Owned(env, pid, hwnd)) return new(PolicyDisposition.Wait, "FIXTURE_NOT_FOREGROUND", "测试窗口已失去前台，已停止。", null);
            if (proposal.Decision is ActDecision act && act.Action is not ClickAction { Button: MouseButton.Left, ClickCount: 1 })
                return new(PolicyDisposition.Wait, "FIXTURE_ACTION_SCOPE", "此有限测试只允许一次左键点击。", null);
            return inner.Validate(task, frame, proposal, env);
        }
    }
    private sealed class RecordedInput(IInputExecutor inner, FixtureObserver observer, string directory) : IInputExecutor
    {
        private int _count;
        public async Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
        {
            _ = await observer.GetEnvironmentAsync(ct);
            int index = _count++;
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"input-{index:D2}-before.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, action.Lease, action.FrameId, action.ProposalId,
                actionType = action.Action.GetType().Name, action = JsonSerializer.SerializeToElement(action.Action, action.Action.GetType())
            }, ct);
            var result = await inner.ExecuteAsync(lease, action, ct);
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"input-{index:D2}-completed.json"), result, CancellationToken.None);
            return result;
        }
    }
}
