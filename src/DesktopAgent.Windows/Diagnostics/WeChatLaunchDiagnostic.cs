using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

/// <summary>Explicit real-model launch-only check. No WeChat pixels or controls are returned,
/// persisted or uploaded. Observing its foreground process identity ends this run immediately.</summary>
internal static class WeChatLaunchDiagnostic
{
    private const string Goal = "通过开始菜单搜索并打开微信。微信窗口打开后立即结束，不进入聊天、不读取消息、不发送任何内容。";
    private const int ActionLimit = 8, RequestLimit = 12, OperationMs = 52000, TotalMs = 60000;

    internal static async Task<int> RunAsync(string directory, bool highRiskEnabled = false)
    {
        var clock = Stopwatch.StartNew();
        directory = Path.GetFullPath(directory);
        TextReplacementDiagnostic.CheckDirectory(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(OperationMs));
        using var watchCancellation = new CancellationTokenSource();
        var gate = new InputSafetyGate();
        var hotkeys = new EmergencyHotkeyService(gate);
        using var timeoutTrip = deadline.Token.Register(() => gate.Trip(InputStopReason.Deadline));
        using var overlay = new DesktopOverlayController(Application.Current.Dispatcher);
        var scope = new LaunchScope(gate, deadline);
        var desktop = new WindowsDesktopObserver(overlay.HideForCaptureAsync, preferForegroundWindow: true,
            validateEnvironment: scope.Check);
        var observer = new ScopedObserver(desktop, scope);
        overlay.Pause += () => scope.Stop("USER_PAUSED", InputStopReason.Paused);
        overlay.Stop += () => scope.Stop("USER_STOPPED", InputStopReason.Stopped);
        overlay.Chat += () => scope.Stop("USER_RETURNED", InputStopReason.Paused);
        var trace = new ConcurrentQueue<object>();
        var progressTrace = new ConcurrentQueue<object>();
        WindowsControlObserver? controls = null;
        ChatCompletionProvider? provider = null;
        ScopedModel? model = null;
        ScopedInput? input = null;
        DesktopTaskCoordinator? coordinator = null;
        AgentProgress? final = null;
        ProviderProfile? profile = null;
        Task? watch = null;
        string stage = "PROFILE", failure = "";
        int preparationActions = 0;
        bool pixelsCleared = false, cleanupComplete = false;
        var existingWeChat = ReadExistingWeChatProcesses();
        bool initiallyForeground = false;
        bool postStopIdentityCheck = false;
        long postStopIdentityCheckMs = 0;
        var postStopForeground = new List<EnvironmentIdentity>();
        try
        {
            await WriteAsync(directory, "run.json", new
            {
                atUtc = DateTimeOffset.UtcNow, goal = Goal, operationDeadlineMs = OperationMs, totalDeadlineMs = TotalMs,
                actionLimit = ActionLimit, requestLimit = RequestLimit, highRiskEnabled,
                allowedProcesses = new[] { "explorer", "SearchHost", "StartMenuExperienceHost" },
                stopBeforeInspectingProcesses = new[] { "WeChat", "Weixin" },
                preparation = "EXACT_NATIVE_SHELL_FOREGROUND_THEN_ONE_PRODUCTION_WIN_GESTURE_NO_CAPTURE",
                acceptance = "FOREGROUND_PROCESS_IDENTITY_ONLY_NOT_CHAT_CONTENT"
            });
            var store = new LocalConfigurationStore(LocalConfigurationStore.DefaultRoot);
            var matches = (await store.ReadAsync(deadline.Token)).Where(p => p.ProviderKind == ProviderKind.DeepSeek &&
                p.Model == "deepseek-v4-flash-vision-exp" && p.BaseUrl.AbsoluteUri.TrimEnd('/') == "https://api.deepseek.com").ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("EXACT_DEEPSEEK_PROFILE_NOT_UNIQUE");
            profile = matches[0];
            if (!DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: true))
                throw new InvalidOperationException("HYBRID_PROBE_REQUIRED");
            stage = "HOTKEYS";
            var ready = await hotkeys.StartAsync().WaitAsync(deadline.Token);
            if (!ready.Registered) throw new InvalidOperationException("EMERGENCY_HOTKEYS_UNAVAILABLE");
            var initial = await desktop.GetEnvironmentAsync(deadline.Token);
            initiallyForeground = IsWeChat(initial.Foreground);
            scope.NoticeOpened(initial);
            deadline.Token.ThrowIfCancellationRequested();
            watch = Task.Run(async () =>
            {
                try
                {
                    while (!watchCancellation.IsCancellationRequested)
                    {
                        if (hotkeys.MessageCount != 0)
                        { scope.Stop("EMERGENCY_HOTKEY", gate.Status.Reason); return; }
                        // Foreground identity only: never UIA, window title or pixels.
                        scope.NoticeOpened(await desktop.GetEnvironmentAsync(watchCancellation.Token));
                        await Task.Delay(50, watchCancellation.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch { scope.Stop("FOREGROUND_WATCH_FAILED", InputStopReason.InputFault); }
            });
            stage = "PREPARATION";
            var display = initial.Displays.FirstOrDefault(d => d.IsPrimary) ?? initial.Displays.First();
            overlay.SetDisplay(display);
            long revision = gate.Status.Revision;
            for (int second = 3; second > 0; second--)
            {
                if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                overlay.Countdown(second);
                await Task.Delay(1000, deadline.Token);
            }
            nint shell = GetShellWindow();
            if (shell == 0) throw new InvalidOperationException("SHELL_UNAVAILABLE");
            if (Win32InputDevice.GetForegroundWindow() != shell) _ = SetForegroundWindow(shell);
            var shellEnvironment = await observer.GetEnvironmentAsync(deadline.Token);
            if (shellEnvironment.Foreground is not { } foreground ||
                !foreground.HwndHex.Equals(shell.ToString("X"), StringComparison.OrdinalIgnoreCase) ||
                !foreground.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SHELL_ACTIVATION_NOT_CONFIRMED");
            if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
            preparationActions = 1;
            await OpenStartAsync(gate, foreground, revision, deadline.Token);
            await WriteAsync(directory, "preparation.json", new
            { atUtc = DateTimeOffset.UtcNow, preparationActions, keys = new[] { "WIN" }, images = 0, productionGestureRunner = true });
            var startWait = Stopwatch.StartNew();
            DesktopEnvironment environment;
            do
            {
                environment = await observer.GetEnvironmentAsync(deadline.Token);
                if (environment.Foreground?.ProcessName is "SearchHost" or "StartMenuExperienceHost") break;
                if (startWait.ElapsedMilliseconds >= 2500) throw new InvalidOperationException("START_MENU_NOT_FOREGROUND");
                await Task.Delay(50, deadline.Token);
            } while (true);
            display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, environment.Foreground!.WindowRect)).First();
            overlay.SetDisplay(display);
            controls = new WindowsControlObserver(observer, item => { if (trace.Count < 256) trace.Enqueue(item); });
            provider = new ChatCompletionProvider(profile, store);
            model = new ScopedModel(provider, observer, directory);
            var ttl = TimeSpan.FromMilliseconds(profile.FrameTtlMs);
            coordinator = new(gate, observer, new ScopedControls(controls, observer, directory), model, profile,
                new LaunchPolicy(new DesktopPolicyValidator(ttl, Environment.ProcessId), scope),
                run => input = new ScopedInput(new WindowsInputExecutor(desktop, gate, run, ttl), observer, directory),
                overlay: overlay, controlsRequired: true);
            coordinator.Changed += item =>
            {
                if (progressTrace.Count < 200) progressTrace.Enqueue(new { atUtc = DateTimeOffset.UtcNow, progress = item });
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                { if (!deadline.IsCancellationRequested) overlay.Update(item, true); }));
            };
            stage = "MODEL_LAUNCH";
            long remaining = Math.Min(OperationMs - clock.ElapsedMilliseconds, OperationMs);
            if (remaining <= 0) throw new OperationCanceledException();
            await coordinator.StartAsync(new(Goal, ProviderConfiguration.Fingerprint(profile), display.Id,
                new(ActionLimit - preparationActions, RequestLimit, remaining), gate.Status.Revision) { HighRiskEnabled = highRiskEnabled }, deadline.Token);
            await coordinator.Completion.WaitAsync(deadline.Token);
            final = coordinator.Progress;
            // A launch animation may outlive the final SendInput or model finish. Identity polling only.
            var openedWait = Stopwatch.StartNew();
            while (!scope.AppOpened && openedWait.ElapsedMilliseconds < 1500)
            {
                scope.NoticeOpened(await desktop.GetEnvironmentAsync(deadline.Token));
                await Task.Delay(50, deadline.Token);
            }
            stage = "LOOP_FINISHED";
        }
        catch (Exception error) { failure = error is OperationCanceledException ? "CANCELLED" : SafeError(error); }
        finally
        {
            final ??= coordinator?.Progress;
            gate.Trip(InputStopReason.Stopped);
            deadline.Cancel();
            overlay.HideAll();
            watchCancellation.Cancel();
            if (coordinator?.Progress is { } active)
            {
                try
                {
                    await coordinator.StopAsync(active.Task.Id).WaitAsync(TimeSpan.FromSeconds(3));
                    await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch { failure = "COORDINATOR_CLEANUP_INCOMPLETE"; }
            }
            if (watch is not null) try { await watch.WaitAsync(TimeSpan.FromMilliseconds(300)); } catch { }
            // Enter/click can remove the Start foreground before the app receives foreground.
            // The input permit and task must already be fully closed. This tail reads native
            // identities only; it never calls the scoped capture/control/model/input paths.
            if (!scope.AppOpened && scope.StopCode == "OUTSIDE_LAUNCH_SCOPE" && input?.LaunchGestureInjected == true &&
                !gate.Status.IsOpen && gate.Status.Lease is null && coordinator?.Completion.IsCompleted == true &&
                coordinator.Progress?.Task.CleanupComplete == true && hotkeys.MessageCount == 0)
            {
                int allowedMs = (int)Math.Clamp(TotalMs - clock.ElapsedMilliseconds - 2500, 0L, 2000L);
                if (allowedMs > 0)
                {
                    postStopIdentityCheck = true;
                    var identityClock = Stopwatch.StartNew();
                    using var identityDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(allowedMs));
                    try
                    {
                        while (!scope.AppOpened && hotkeys.MessageCount == 0)
                        {
                            var environment = await desktop.GetEnvironmentAsync(identityDeadline.Token).WaitAsync(identityDeadline.Token);
                            var identity = EnvironmentIdentity.From(environment);
                            if (postStopForeground.Count < 8 && (postStopForeground.Count == 0 || postStopForeground[^1] != identity))
                                postStopForeground.Add(identity);
                            scope.NoticeOpened(environment);
                            if (!scope.AppOpened) await Task.Delay(50, identityDeadline.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { /* Identity failure does not reopen or weaken the original scope stop. */ }
                    postStopIdentityCheckMs = identityClock.ElapsedMilliseconds;
                }
            }
            controls?.Dispose();
            provider?.Dispose();
            try { await hotkeys.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); } catch { failure = "HOTKEY_CLEANUP_INCOMPLETE"; }
            try { await desktop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); pixelsCleared = true; }
            catch { failure = "PIXEL_CLEANUP_INCOMPLETE"; }
            cleanupComplete = !gate.Status.IsOpen && gate.Status.Lease is null &&
                (coordinator is null || coordinator.Completion.IsCompleted && coordinator.Progress?.Task.CleanupComplete == true);
        }
        bool completed = scope.AppOpened && preparationActions == 1 && (model?.ApiAttempts ?? 0) > 0 &&
            cleanupComplete && pixelsCleared && hotkeys.ThreadExited && clock.ElapsedMilliseconds <= TotalMs &&
            (failure.Length == 0 || failure == "CANCELLED");
        bool existingProcessForeground = scope.OpenedIdentity is { } opened && existingWeChat.ProcessIds.Contains(opened.ProcessId);
        await WriteAsync(directory, "control-resolution-trace.json", trace.ToArray());
        await WriteAsync(directory, "progress.json", progressTrace.ToArray());
        await WriteAsync(directory, "report.json", new
        {
            atUtc = DateTimeOffset.UtcNow, diagnosticCompleted = completed, appOpened = scope.AppOpened,
            openedIdentity = scope.OpenedIdentity, stage, failure, stopCode = scope.StopCode,
            outsideEnvironment = scope.OutsideEnvironment,
            postStopIdentityCheck, postStopIdentityCheckMs, postStopForeground,
            launchGestureInjected = input?.LaunchGestureInjected ?? false,
            initialWeChatProcessIds = existingWeChat.ProcessIds, processBaselineComplete = existingWeChat.Complete,
            initiallyForeground, existingProcessForeground,
            freshProcessLaunchVerified = completed && existingWeChat.Complete && !existingProcessForeground,
            existingAppActivationVerified = completed && existingProcessForeground && !initiallyForeground,
            final, elapsedMs = clock.ElapsedMilliseconds, apiAttempts = model?.ApiAttempts ?? 0,
            preparationActions, modelExecutionCalls = input?.Calls ?? 0, totalActionCalls = preparationActions + (input?.Calls ?? 0),
            appliedInputEvents = input?.AppliedEvents ?? 0, cleanupComplete, pixelsCleared,
            hotkeysReleased = hotkeys.ThreadExited, gateClosed = !gate.Status.IsOpen, permitReleased = gate.Status.Lease is null,
            model = profile?.Model, baseUrl = profile?.BaseUrl, highRiskEnabled,
            wechatPixelsReturnedOrUploaded = false, wechatControlsReturnedOrPersisted = false, messagesRead = false, messagesSent = false,
            note = "Native identity is checked before and after capture. A foreground race discards pixels; this does not claim that transient pixels can never enter an unreturned native buffer. appOpened verifies launch only."
        });
        return completed ? 0 : 1;
    }

    private static async Task OpenStartAsync(InputSafetyGate gate, ForegroundIdentity foreground, long revision, CancellationToken ct)
    {
        var registry = new TaskLeaseRegistry();
        TaskLeaseSession? session = null;
        LeaseWorker? worker = null;
        InputRun? run = null;
        try
        {
            if (!registry.TryAcquire(Guid.NewGuid(), out session) || !session!.TryStartWorker(session.Current, out worker) ||
                !gate.TryArm(session, session.Current, revision, TimeSpan.FromSeconds(3), out run))
                throw new InvalidOperationException("PREPARATION_LEASE_FAILED");
            var result = await new InputGestureRunner(gate, new Win32InputDevice()).ExecuteAsync(run!,
                new(foreground, foreground.WindowRect, Win32InputDevice.VirtualDesktop()), new HotkeyAction([AgentKey.WIN]), ct);
            if (result.Status != "applied" || !result.CleanupComplete) throw new InvalidOperationException("PREPARATION_WIN_FAILED");
        }
        finally
        {
            gate.Trip(InputStopReason.Stopped);
            if (run is not null)
            {
                var wait = Stopwatch.StartNew();
                while (!gate.TryRelease(run) && wait.ElapsedMilliseconds < 300) await Task.Delay(10);
                if (!run.IsClosed) throw new InvalidOperationException("PREPARATION_PERMIT_NOT_RELEASED");
            }
            worker?.Dispose();
            if (session is not null)
            {
                await session.RequestCompletionAsync().WaitAsync(TimeSpan.FromMilliseconds(300));
                if (!session.TryCompleteCleanup()) throw new InvalidOperationException("PREPARATION_LEASE_NOT_RELEASED");
            }
        }
    }

    private sealed class LaunchScope(InputSafetyGate gate, CancellationTokenSource deadline)
    {
        private ForegroundIdentity? _opened;
        private string? _stop;
        private EnvironmentIdentity? _outside;
        public bool AppOpened => Volatile.Read(ref _opened) is not null;
        public ForegroundIdentity? OpenedIdentity => Volatile.Read(ref _opened);
        public string? StopCode => Volatile.Read(ref _stop);
        public EnvironmentIdentity? OutsideEnvironment => Volatile.Read(ref _outside);
        public static bool IsAllowed(ForegroundIdentity? foreground) => foreground is not null &&
            (foreground.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
             foreground.ProcessName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
             foreground.ProcessName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase));
        public void Stop(string code, InputStopReason reason)
        {
            Interlocked.CompareExchange(ref _stop, code, null);
            gate.Trip(reason == InputStopReason.None ? InputStopReason.Stopped : reason);
            deadline.Cancel();
        }
        public void NoticeOpened(DesktopEnvironment environment)
        {
            if (environment.SessionState != DesktopSessionState.Available || environment.Foreground is not { } foreground) return;
            if (IsWeChat(foreground))
            {
                Interlocked.CompareExchange(ref _opened, foreground, null);
                Stop("WECHAT_FOREGROUND_CONFIRMED", InputStopReason.Stopped);
            }
        }
        public void Check(DesktopEnvironment environment)
        {
            NoticeOpened(environment);
            if (AppOpened) throw new OperationCanceledException("WECHAT_FOREGROUND_CONFIRMED");
            if (environment.SessionState != DesktopSessionState.Available || !IsAllowed(environment.Foreground))
            {
                Interlocked.CompareExchange(ref _outside, EnvironmentIdentity.From(environment), null);
                Stop("OUTSIDE_LAUNCH_SCOPE", InputStopReason.Stopped);
                throw new OperationCanceledException("OUTSIDE_LAUNCH_SCOPE");
            }
            // Screen capture is composited, including a translucent Start menu. Reject any visible
            // WeChat overlap even when it does not own foreground. Only native PID/rect/cloak reads.
            if (VisibleWeChatOverlaps(environment.Foreground!.WindowRect))
            { Stop("PRIVATE_WINDOW_VISIBLE", InputStopReason.Stopped); throw new OperationCanceledException("PRIVATE_WINDOW_VISIBLE"); }
            deadline.Token.ThrowIfCancellationRequested();
        }
        public void CheckFrame(Frame frame)
        {
            if (!IsAllowed(frame.Foreground) || !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion))
                throw new InvalidOperationException("FRAME_OUTSIDE_LAUNCH_SCOPE");
        }
    }

    private sealed class ScopedObserver(WindowsDesktopObserver inner, LaunchScope scope) : IDesktopObserver
    {
        public void CheckFrame(Frame frame) => scope.CheckFrame(frame);
        public async Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct)
        { var value = await inner.GetEnvironmentAsync(ct); scope.Check(value); return value; }
        public async Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? region, CancellationToken ct)
        {
            _ = await GetEnvironmentAsync(ct);
            var frame = await inner.CaptureAsync(lease, monitorId, region, ct);
            scope.CheckFrame(frame);
            _ = await GetEnvironmentAsync(ct);
            return frame;
        }
    }

    private sealed class ScopedControls(IControlObserver inner, ScopedObserver scope, string directory) : IControlObserver
    {
        private int _count;
        public async Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct)
        {
            scope.CheckFrame(frame); _ = await scope.GetEnvironmentAsync(ct);
            var value = await inner.ObserveAsync(frame, ct);
            _ = await scope.GetEnvironmentAsync(ct);
            await WriteAsync(directory, $"controls-{Interlocked.Increment(ref _count):D3}.json", value);
            return value;
        }
        public async Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct)
        {
            scope.CheckFrame(frame); _ = await scope.GetEnvironmentAsync(ct);
            return await inner.ResolveAsync(frame, snapshot, controlId, ct);
        }
    }

    private sealed class ScopedModel(IModelProvider inner, ScopedObserver scope, string directory) : IModelProvider, ITaskIntentProvider
    {
        private int _calls;
        private readonly List<RecordingModelProvider> _recorders = [];
        public int ApiAttempts => _recorders.Sum(r => r.ApiAttempts);
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct) => CallAsync(request, ct, false);
        public Task<ProviderReply> InterpretAsync(ModelRequest request, CancellationToken ct) => CallAsync(request, ct, true);
        private async Task<ProviderReply> CallAsync(ModelRequest request, CancellationToken ct, bool interpretation)
        {
            scope.CheckFrame(request.CurrentFrame);
            if (request.OverviewContext is { } overview) scope.CheckFrame(overview);
            _ = await scope.GetEnvironmentAsync(ct);
            int index = Interlocked.Increment(ref _calls);
            if (index > RequestLimit) throw new InvalidOperationException("REQUEST_LIMIT");
            var recorder = new RecordingModelProvider(inner, Path.Combine(directory, $"model-{index:D3}"),
                request.CurrentFrame.Foreground.ProcessId, request.CurrentFrame.Foreground.HwndHex);
            _recorders.Add(recorder);
            return interpretation ? await recorder.InterpretAsync(request, ct) : await recorder.DecideAsync(request, ct);
        }
        public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class LaunchPolicy(IPolicyValidator inner, LaunchScope scope) : IPolicyValidator
    {
        public PolicyResult Validate(TaskContext task, Frame frame, Proposal proposal, DesktopEnvironment environment)
        {
            scope.Check(environment); scope.CheckFrame(frame);
            if (proposal.Decision is ActDecision { Action: var action } && !AllowedAction(action))
                return new(PolicyDisposition.Reject, "LAUNCH_DIAGNOSTIC_ACTION_SCOPE", "本轮只允许搜索并打开微信。", null);
            return inner.Validate(task, frame, proposal, environment);
        }
        public static bool AllowedAction(AgentAction action) => action switch
        {
            TextAction text => IsSearchText(text.Text),
            ReplaceTextAction text => IsSearchText(text.Text),
            HotkeyAction keys => keys.Keys is [AgentKey.WIN] or [AgentKey.CTRL, AgentKey.A] or
                [AgentKey.ENTER or AgentKey.ESC or AgentKey.TAB or AgentKey.UP or AgentKey.DOWN or AgentKey.LEFT or AgentKey.RIGHT or AgentKey.BACKSPACE],
            ClickAction { Button: MouseButton.Left } or MoveAction or ScrollAction => true,
            _ => false
        };
        private static bool IsSearchText(string value) => value.Trim().Equals("微信", StringComparison.Ordinal) ||
            value.Trim().Equals("WeChat", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("Weixin", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScopedInput(IInputExecutor inner, ScopedObserver scope, string directory) : IInputExecutor
    {
        private int _calls;
        private long _applied;
        private int _launchGestureInjected;
        public int Calls => Volatile.Read(ref _calls);
        public long AppliedEvents => Interlocked.Read(ref _applied);
        public bool LaunchGestureInjected => Volatile.Read(ref _launchGestureInjected) != 0;
        public async Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
        {
            _ = await scope.GetEnvironmentAsync(ct);
            if (!LaunchPolicy.AllowedAction(action.Action)) throw new InvalidOperationException("INPUT_OUTSIDE_LAUNCH_SCOPE");
            if (Calls >= ActionLimit - 1) throw new InvalidOperationException("INPUT_ACTION_LIMIT");
            int index = Interlocked.Increment(ref _calls);
            await WriteAsync(directory, $"input-{index:D3}-before.json", new
            { atUtc = DateTimeOffset.UtcNow, action.ProposalId, action.FrameId, actionType = action.Action.GetType().Name,
                action = JsonSerializer.SerializeToElement(action.Action, action.Action.GetType()) });
            _ = await scope.GetEnvironmentAsync(ct);
            var result = await inner.ExecuteAsync(lease, action, ct);
            Interlocked.Add(ref _applied, result.AppliedEventCount);
            // These native gestures may launch the selected app. This is not proof of launch;
            // proof comes solely from the subsequent foreground identity check.
            if (result.AppliedEventCount > 0 && action.Action is HotkeyAction { Keys: [AgentKey.ENTER] } or ClickAction { Button: MouseButton.Left })
                Volatile.Write(ref _launchGestureInjected, 1);
            await WriteAsync(directory, $"input-{index:D3}-completed.json", result);
            _ = await scope.GetEnvironmentAsync(ct);
            return result;
        }
    }

    private static Task WriteAsync<T>(string directory, string name, T data)
        => HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, name), data, CancellationToken.None);
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static string SafeError(Exception error)
    {
        string code = error is ProviderCallException provider ? provider.Code : error.Message;
        return code.Length is > 0 and <= 100 && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? code : error.GetType().Name;
    }
    private sealed record EnvironmentIdentity(string? ProcessName, int? ProcessId, string? HwndHex, DesktopSessionState SessionState)
    {
        public static EnvironmentIdentity From(DesktopEnvironment environment) =>
            new(environment.Foreground?.ProcessName, environment.Foreground?.ProcessId, environment.Foreground?.HwndHex, environment.SessionState);
    }
    private static bool IsWeChat(ForegroundIdentity? foreground) => foreground is not null &&
        (foreground.ProcessName.Equals("WeChat", StringComparison.OrdinalIgnoreCase) || foreground.ProcessName.Equals("Weixin", StringComparison.OrdinalIgnoreCase));
    private static (int[] ProcessIds, bool Complete) ReadExistingWeChatProcesses()
    {
        var ids = new HashSet<int>();
        bool complete = true;
        foreach (string name in new[] { "WeChat", "Weixin" })
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) ids.Add(process.Id);
            }
            catch { complete = false; }
        }
        return (ids.OrderBy(id => id).ToArray(), complete);
    }
    private static bool VisibleWeChatOverlaps(PhysicalRect region)
    {
        using var dpi = new PhysicalDpiScope();
        bool blocked = false, unreadable = false;
        WindowCallback callback = (hwnd, parameter) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var rectangle)) { unreadable = true; return false; }
            if (rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top ||
                Intersection(region, new(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top)) == 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) { unreadable = true; return false; }
            if (pid == Environment.ProcessId) return true;
            try
            {
                using var process = Process.GetProcessById(checked((int)pid));
                if (process.ProcessName.Equals("WeChat", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("Weixin", StringComparison.OrdinalIgnoreCase))
                { blocked = true; return false; }
            }
            catch { unreadable = true; return false; }
            return true;
        };
        bool enumerated = EnumWindows(callback, 0);
        if (unreadable || !enumerated && !blocked) throw new InvalidOperationException("NATIVE_WINDOW_SCOPE_UNAVAILABLE");
        return blocked;
    }
    private delegate bool WindowCallback(nint hwnd, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
