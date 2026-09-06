using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
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

/// <summary>One explicitly planned, bounded production task in an existing user application window.
/// This runner neither implements a setting workflow nor verifies its value or restoration.</summary>
internal static class BoundedDesktopTaskDiagnostic
{
    internal static async Task<int> RunAsync(string planPath, string directory)
    {
        var clock = Stopwatch.StartNew();
        TaskPlan plan;
        Evidence evidence;
        try
        {
            plan = ReadPlan(planPath);
            evidence = new(directory);
        }
        catch { return 2; } // Invalid plans cannot create an input permit or expose arbitrary file contents.

        // Reserve up to ten seconds for cancellation, hotkey release and clearing cached pixels.
        int operationDeadlineMs = (int)Math.Min(plan.ActiveMsLimit + 5000, 180000);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, operationDeadlineMs - clock.ElapsedMilliseconds)));
        using var watchCancellation = new CancellationTokenSource();
        var gate = new InputSafetyGate();
        var hotkeys = new EmergencyHotkeyService(gate);
        using var deadlineRegistration = deadline.Token.Register(() => gate.Trip(InputStopReason.Deadline));
        using var overlay = new DesktopOverlayController(Application.Current.Dispatcher);
        var desktop = new WindowsDesktopObserver(overlay.HideForCaptureAsync, preferForegroundWindow: true);
        int stopRequested = 0;
        void Stop(InputStopReason reason)
        {
            gate.Trip(reason);
            Interlocked.Exchange(ref stopRequested, 1);
            deadline.Cancel();
        }
        overlay.Pause += () => Stop(InputStopReason.Paused);
        overlay.Stop += () => Stop(InputStopReason.Stopped);
        overlay.Chat += () => Stop(InputStopReason.Paused);
        WindowsControlObserver? controls = null;
        var controlTrace = new System.Collections.Concurrent.ConcurrentQueue<object>();
        ChatCompletionProvider? provider = null;
        ScopedModel? recordedModel = null;
        RecordedInput? recordedInput = null;
        DesktopTaskCoordinator? coordinator = null;
        ProviderProfile? profile = null;
        HostedWindowIdentity? hostedSettings = null;
        AgentProgress? final = null;
        Task? hotkeyWatch = null;
        Action<AgentProgress>? changed = null;
        string stage = "CHECKING_PROFILE";
        string? failure = null;
        int processId = 0;
        bool hotkeysStarted = false, cancelledBeforeCleanup = false, cleanupComplete = false, pixelsCleared = false;
        bool taskSucceeded = false;
        try
        {
            await evidence.WriteAsync("run.json", new
            {
                schemaVersion = 1, atUtc = DateTimeOffset.UtcNow, plan,
                operationDeadlineMs, totalDeadlineMs = 190000,
                scope = plan.ExpectedProcessName == "ApplicationFrameHost" ? "EXACT_SETTINGS_HOST_AND_BOUND_CONTENT" : "INITIAL_EXACT_HWND_THEN_SAME_PROCESS_ID_ONLY", controlsRequired = true,
                startsApplications = false, closesUserApplications = false, automaticallyRestartsTask = false,
                retriesProviderErrors = false, coordinatorMayReobserveWithinBudget = true,
                automaticallyRestores = false, settingAcceptance = "SEPARATE_ORIGINAL_CHANGE_RESTORE_REVIEW_REQUIRED"
            }, deadline.Token);
            var store = new LocalConfigurationStore(LocalConfigurationStore.DefaultRoot);
            var matches = (await store.ReadAsync(deadline.Token)).Where(p => p.ProviderKind == ProviderKind.DeepSeek &&
                p.Model == "deepseek-v4-flash-vision-exp" && p.BaseUrl.AbsoluteUri.TrimEnd('/') == "https://api.deepseek.com").ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("EXACT_DEEPSEEK_PROFILE_NOT_UNIQUE");
            profile = matches[0];
            if (!DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: true))
                throw new InvalidOperationException("HYBRID_PROBE_REQUIRED");

            stage = "CHECKING_INITIAL_WINDOW";
            nint expectedHwnd = unchecked((nint)(long)ParseHwnd(plan.ExpectedHwndHex));
            if (!IsWindow(expectedHwnd) || GetWindowThreadProcessId(expectedHwnd, out uint targetPid) == 0 || targetPid is 0 or > int.MaxValue)
                throw new InvalidOperationException("INITIAL_WINDOW_UNAVAILABLE");
            processId = (int)targetPid;
            using var targetProcess = Process.GetProcessById(processId);
            if (processId == Environment.ProcessId || targetProcess.HasExited ||
                !string.Equals(targetProcess.ProcessName, plan.ExpectedProcessName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("INITIAL_PROCESS_MISMATCH");
            if (plan.ExpectedProcessName == "ApplicationFrameHost")
            {
                var initialHost = Win32InputDevice.ReadTarget(expectedHwnd, Win32InputDevice.VirtualDesktop()).Foreground with { ProcessName = targetProcess.ProcessName };
                hostedSettings = HostedWindowIdentity.TryCapture(initialHost);
                if (initialHost.ProcessId != processId || hostedSettings?.ContentHwnd is null || hostedSettings.ContentProcessId is not > 0 ||
                    hostedSettings.ContentProcessName is not ("SystemSettings" or "CalculatorApp") || !hostedSettings.IsCurrent())
                    throw new InvalidOperationException("INITIAL_HOSTED_SETTINGS_NOT_VERIFIED");
            }
            void CheckInitialWindow()
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (targetProcess.HasExited || !IsWindow(expectedHwnd) || GetWindowThreadProcessId(expectedHwnd, out uint pid) == 0 || pid != targetPid ||
                    hostedSettings is not null && !hostedSettings.IsCurrent())
                    throw new InvalidOperationException("INITIAL_WINDOW_IDENTITY_CHANGED");
            }
            stage = "REGISTERING_HOTKEYS";
            hotkeysStarted = true;
            var ready = await hotkeys.StartAsync().WaitAsync(deadline.Token);
            if (!ready.Registered) throw new InvalidOperationException("EMERGENCY_HOTKEYS_UNAVAILABLE");
            hotkeyWatch = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(50, watchCancellation.Token);
                        if (hotkeys.MessageCount == 0) continue;
                        Stop(gate.Status.Reason == InputStopReason.Paused ? InputStopReason.Paused : InputStopReason.Stopped);
                        return;
                    }
                }
                catch (OperationCanceledException) { }
            });
            long revision = gate.Status.Revision;
            stage = "ACTIVATING_INITIAL_WINDOW";
            bool activationAttempted = false, activationConfirmed = false;
            bool? activationReturned = null;
            string? activationError = null;
            var activationTimer = Stopwatch.StartNew();
            DesktopEnvironment environment;
            try
            {
                CheckInitialWindow();
                if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                await evidence.WriteAsync("initial-activation-before.json", new
                {
                    atUtc = DateTimeOffset.UtcNow, processId, plan.ExpectedProcessName, plan.ExpectedHwndHex,
                    hostedSettings = HostedEvidence(hostedSettings),
                    alreadyForeground = GetForegroundWindow() == expectedHwnd, maximumWaitMs = 30000,
                    operation = "SetForegroundWindow_ONCE_IF_NEEDED", preparationOnly = true, modelAction = false, inputInjected = false
                }, deadline.Token);
                CheckInitialWindow();
                if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                if (GetForegroundWindow() != expectedHwnd)
                {
                    activationAttempted = true;
                    activationReturned = SetForegroundWindow(expectedHwnd);
                }
                while (GetForegroundWindow() != expectedHwnd)
                {
                    CheckInitialWindow();
                    if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                    if (activationTimer.ElapsedMilliseconds >= 30000) throw new InvalidOperationException("INITIAL_ACTIVATION_WAIT_EXPIRED");
                    await Task.Delay(50, deadline.Token);
                }
                CheckInitialWindow();
                if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                environment = await desktop.GetEnvironmentAsync(deadline.Token);
                if (environment.SessionState != DesktopSessionState.Available || environment.Foreground?.ProcessId != processId ||
                    !string.Equals(environment.Foreground.ProcessName, plan.ExpectedProcessName, StringComparison.OrdinalIgnoreCase) ||
                    ParseHwnd(environment.Foreground.HwndHex) != ParseHwnd(plan.ExpectedHwndHex))
                    throw new InvalidOperationException("INITIAL_FOREGROUND_MISMATCH");
                CheckInitialWindow();
                activationConfirmed = true;
            }
            catch (Exception error) { activationError = SafeError(error); throw; }
            finally
            {
                await evidence.WriteAsync("initial-activation-completed.json", new
                {
                    atUtc = DateTimeOffset.UtcNow, elapsedMs = activationTimer.ElapsedMilliseconds,
                    processId, plan.ExpectedProcessName, plan.ExpectedHwndHex, activationAttempted, activationReturned, activationConfirmed,
                    hostedSettings = HostedEvidence(hostedSettings),
                    activationError, cancelled = deadline.IsCancellationRequested || hotkeys.MessageCount != 0,
                    preparationOnly = true, modelAction = false, inputInjected = false, modelCalls = 0
                }, CancellationToken.None);
            }
            var foreground = environment.Foreground!;
            var display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, foreground.WindowRect)).First();
            var scoped = new ScopedObserver(desktop, processId, plan.ExpectedProcessName, evidence, hostedSettings);
            await evidence.WriteAsync("initial-identity.json", new
            {
                atUtc = DateTimeOffset.UtcNow, foreground, display.Id,
                hostedSettings = HostedEvidence(hostedSettings),
                profileFingerprint = ProviderConfiguration.Fingerprint(profile), profile.Model, profile.BaseUrl
            }, deadline.Token);
            overlay.SetDisplay(display);
            stage = "COUNTDOWN";
            for (int second = 3; second > 0; second--)
            {
                if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
                environment = await scoped.GetEnvironmentAsync(deadline.Token);
                if (ParseHwnd(environment.Foreground!.HwndHex) != ParseHwnd(plan.ExpectedHwndHex))
                    throw new InvalidOperationException("INITIAL_FOREGROUND_MISMATCH");
                overlay.Countdown(second);
                await Task.Delay(1000, deadline.Token);
            }
            if (hotkeys.MessageCount != 0 || gate.Status.Revision != revision) throw new OperationCanceledException();
            environment = await scoped.GetEnvironmentAsync(deadline.Token);
            if (ParseHwnd(environment.Foreground!.HwndHex) != ParseHwnd(plan.ExpectedHwndHex))
                throw new InvalidOperationException("INITIAL_FOREGROUND_MISMATCH");
            controls = new WindowsControlObserver(scoped, entry => { if (controlTrace.Count < 256) controlTrace.Enqueue(entry); });
            provider = new ChatCompletionProvider(profile, store);
            recordedModel = new(provider, scoped, evidence);
            var ttl = TimeSpan.FromMilliseconds(profile.FrameTtlMs);
            coordinator = new(gate, scoped, new RecordedControls(controls, evidence), recordedModel, profile,
                new ScopedPolicy(new DesktopPolicyValidator(ttl, Environment.ProcessId), scoped),
                run => recordedInput = new(new WindowsInputExecutor(desktop, gate, run, ttl), scoped, evidence),
                overlay: overlay, controlsRequired: true, prepareDesktop: new CalculatorWindowLayout(gate).PrepareAsync);
            changed = progress =>
            {
                try { evidence.RecordProgress(progress); }
                catch { Stop(InputStopReason.InputFault); }
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!evidence.Closing) overlay.Update(progress, true);
                }));
            };
            coordinator.Changed += changed;
            stage = "MODEL_LOOP";
            await coordinator.StartAsync(new(plan.Goal, ProviderConfiguration.Fingerprint(profile), display.Id,
                new(plan.ActionLimit, plan.RequestLimit, plan.ActiveMsLimit), revision), deadline.Token);
            await coordinator.Completion.WaitAsync(deadline.Token);
            final = coordinator.Progress;
            taskSucceeded = final?.Task.State == TaskState.Succeeded;
            stage = "LOOP_FINISHED";
        }
        catch (Exception error) { failure = stage + "_" + SafeError(error); }
        finally
        {
            // Preserve the actual terminal state before StopAsync can change it to Cancelled.
            final ??= coordinator?.Progress;
            cancelledBeforeCleanup = deadline.IsCancellationRequested || hotkeys.MessageCount != 0 || Volatile.Read(ref stopRequested) != 0;
            evidence.BeginCleanup();
            gate.Trip(InputStopReason.Stopped);
            deadline.Cancel();
            overlay.HideAll();
            try
            {
                await evidence.WriteAsync("result-before-stop.json", new
                {
                    atUtc = DateTimeOffset.UtcNow, stage, failure, final, taskSucceeded, cancelledBeforeCleanup,
                    apiAttempts = recordedModel?.ApiAttempts ?? 0, executionCalls = recordedInput?.ExecutionCalls ?? 0,
                    settingsVerified = false, restorationVerified = false
                }, CancellationToken.None);
            }
            catch { failure ??= "RESULT_EVIDENCE_FAILED"; }
            if (coordinator?.Progress is { } active)
            {
                try
                {
                    await StopAndDrainAsync(coordinator, active.Task.Id).WaitAsync(TimeSpan.FromSeconds(4));
                }
                catch { failure ??= "COORDINATOR_CLEANUP_INCOMPLETE"; }
            }
            if (coordinator is not null && changed is not null) coordinator.Changed -= changed;
            cleanupComplete = !gate.Status.IsOpen && gate.Status.Lease is null &&
                (coordinator is null || coordinator.Completion.IsCompleted && coordinator.Progress?.Task.CleanupComplete == true);
            controls?.Dispose();
            provider?.Dispose();
            watchCancellation.Cancel();
            if (hotkeyWatch is not null)
                try { await hotkeyWatch.WaitAsync(TimeSpan.FromMilliseconds(250)); } catch { failure ??= "HOTKEY_WATCH_CLEANUP_INCOMPLETE"; }
            try { await hotkeys.DisposeAsync(); } catch { failure ??= "HOTKEY_CLEANUP_FAILED"; }
            try { await desktop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); pixelsCleared = true; }
            catch { failure ??= "PIXEL_CACHE_CLEANUP_INCOMPLETE"; }
        }
        bool hotkeysReleased = !hotkeysStarted || hotkeys.ThreadExited;
        if (evidence.Faulted) failure ??= "DIAGNOSTIC_EVIDENCE_FAILED";
        if (clock.ElapsedMilliseconds > 190000) failure ??= "TOTAL_DEADLINE_EXCEEDED";
        bool diagnosticCompleted = taskSucceeded && !cancelledBeforeCleanup && cleanupComplete && pixelsCleared && hotkeysReleased && failure is null;
        try
        {
            await evidence.WriteAsync("control-resolution-trace.json", controlTrace.ToArray(), CancellationToken.None);
            await evidence.WriteAsync("report.json", new
            {
                schemaVersion = 1, atUtc = DateTimeOffset.UtcNow, plan, processId, stage, failure,
                hostedSettings = HostedEvidence(hostedSettings),
                diagnosticCompleted, taskSucceeded, final, cancelled = cancelledBeforeCleanup, wallElapsedMs = clock.ElapsedMilliseconds,
                apiAttempts = recordedModel?.ApiAttempts ?? 0, usage = coordinator?.Progress?.Task.Usage ?? final?.Task.Usage,
                timings = final?.Timings, recordedProviderUsage = recordedModel?.Usage ?? new ProviderUsage(null, null),
                executionCalls = recordedInput?.ExecutionCalls ?? 0, appliedInputEvents = recordedInput?.AppliedEvents ?? 0,
                gateClosed = !gate.Status.IsOpen, permitReleased = gate.Status.Lease is null,
                cleanupComplete, hotkeysReleased, pixelsCleared, evidenceComplete = !evidence.Faulted,
                profileFingerprint = profile is null ? null : ProviderConfiguration.Fingerprint(profile), model = profile?.Model,
                settingsVerified = false, originalValueVerified = false, restorationVerified = false, pureVisionVerified = false,
                userApplicationClosed = false, automaticRestorationAttempted = false, automaticallyReplayed = false,
                note = "taskSucceeded is the model/coordinator task outcome, not independent setting acceptance or proof of restoration. Review original/change/restore evidence separately."
            }, CancellationToken.None);
        }
        catch { return 1; }
        return diagnosticCompleted ? 0 : 1;
    }

    private sealed record TaskPlan(string Goal, string ExpectedProcessName, string ExpectedHwndHex, int ActionLimit, int RequestLimit, long ActiveMsLimit);
    private static TaskPlan ReadPlan(string path)
    {
        using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 16384) throw new InvalidOperationException("PLAN_SIZE_INVALID");
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        string[] names = ["goal", "expectedProcessName", "expectedHwndHex", "actionLimit", "requestLimit", "activeMsLimit"];
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("PLAN_OBJECT_REQUIRED");
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != names.Length || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length ||
            properties.Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) throw new InvalidOperationException("PLAN_FIELDS_INVALID");
        string goal = root.GetProperty("goal").GetString() ?? "";
        string process = root.GetProperty("expectedProcessName").GetString() ?? "";
        string hwnd = root.GetProperty("expectedHwndHex").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(goal) || goal.Length > 4000 || process is not ("SystemSettings" or "explorer" or "ApplicationFrameHost") || ParseHwnd(hwnd) == 0 ||
            !root.GetProperty("actionLimit").TryGetInt32(out int actions) || actions is < 1 or > 30 ||
            !root.GetProperty("requestLimit").TryGetInt32(out int requests) || requests is < 1 or > 40 ||
            !root.GetProperty("activeMsLimit").TryGetInt64(out long active) || active is < 10 or > 180000)
            throw new InvalidOperationException("PLAN_VALUES_INVALID");
        return new(goal.Trim(), process, hwnd, actions, requests, active);
    }
    private static ulong ParseHwnd(string text)
    {
        if (text.Length is < 1 or > 18) return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value) ? value : 0;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static string SafeError(Exception error)
    {
        string code = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : error is ProviderCallException call ? call.Code : error.Message;
        return code.Length is > 0 and <= 100 && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? code : error.GetType().Name;
    }
    private static async Task StopAndDrainAsync(DesktopTaskCoordinator coordinator, Guid taskId)
    {
        await coordinator.StopAsync(taskId);
        await coordinator.Completion;
    }
    private static object? HostedEvidence(HostedWindowIdentity? identity) => identity is null ? null : new
    {
        hostHwndHex = identity.HostHwnd.ToString("X"), identity.HostProcessId, identity.HostProcessName, identity.HostStartTimeUtcTicks,
        contentHwndHex = identity.ContentHwnd is { } content ? content.ToString("X") : null,
        identity.ContentProcessId, identity.ContentProcessName, identity.ContentStartTimeUtcTicks
    };

    private sealed class ScopedObserver(WindowsDesktopObserver inner, int processId, string processName, Evidence evidence,
        HostedWindowIdentity? hostedSettings) : IDesktopObserver
    {
        private int _observations;
        public int ProcessId => processId;
        public bool Matches(DesktopEnvironment environment) => environment.SessionState == DesktopSessionState.Available &&
            environment.Foreground?.ProcessId == processId && string.Equals(environment.Foreground.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
            (hostedSettings is null || ParseHwnd(environment.Foreground.HwndHex) == unchecked((ulong)(long)hostedSettings.HostHwnd) && hostedSettings.IsCurrent());
        public void CheckFrame(Frame frame)
        {
            if (frame.Foreground.ProcessId != processId || !string.Equals(frame.Foreground.ProcessName, processName, StringComparison.OrdinalIgnoreCase) ||
                !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion) || hostedSettings is not null &&
                (ParseHwnd(frame.Foreground.HwndHex) != unchecked((ulong)(long)hostedSettings.HostHwnd) || !hostedSettings.IsCurrent()))
                throw new InvalidOperationException("FRAME_OUTSIDE_TARGET_PROCESS");
        }
        public async Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct)
        {
            var environment = await inner.GetEnvironmentAsync(ct);
            if (!Matches(environment)) throw new InvalidOperationException("TARGET_PROCESS_NOT_FOREGROUND");
            return environment;
        }
        public async Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? physicalRegion, CancellationToken ct)
        {
            _ = await GetEnvironmentAsync(ct);
            int index = Interlocked.Increment(ref _observations);
            await evidence.WriteAsync($"observation-{index:D3}-before.json", new { atUtc = DateTimeOffset.UtcNow, lease, monitorId, physicalRegion, processId }, ct);
            var frame = await inner.CaptureAsync(lease, monitorId, physicalRegion, ct);
            // Reject a foreground-switch race before persisting or returning any pixels to a model.
            CheckFrame(frame);
            _ = await GetEnvironmentAsync(ct);
            await evidence.WriteBytesAsync($"observation-{index:D3}.png", frame.Image.Bytes.ToArray(), ct);
            await evidence.WriteAsync($"observation-{index:D3}-completed.json", new { atUtc = DateTimeOffset.UtcNow, frame }, ct);
            return frame;
        }
    }

    private sealed class ScopedPolicy(IPolicyValidator inner, ScopedObserver scope) : IPolicyValidator
    {
        public PolicyResult Validate(TaskContext task, Frame frame, Proposal proposal, DesktopEnvironment environment)
        {
            if (!scope.Matches(environment)) return new(PolicyDisposition.Wait, "TARGET_PROCESS_NOT_FOREGROUND", "目标应用已失去前台，本轮诊断停止。", null);
            if (proposal.Decision is ActDecision { Action: HotkeyAction keys } &&
                (keys.Keys.Contains(AgentKey.WIN) || keys.Keys.Contains(AgentKey.ALT) && keys.Keys.Contains(AgentKey.TAB)))
                return new(PolicyDisposition.Wait, "DIAGNOSTIC_APPLICATION_SCOPE", "本轮测试限定在当前应用内，请使用应用自身的导航或搜索。", null);
            return inner.Validate(task, frame, proposal, environment);
        }
    }

    private sealed class RecordedControls(IControlObserver inner, Evidence evidence) : IControlObserver
    {
        private int _count;
        public async Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct)
        {
            var snapshot = await inner.ObserveAsync(frame, ct);
            await evidence.WriteAsync($"controls-{Interlocked.Increment(ref _count):D3}.json", new { atUtc = DateTimeOffset.UtcNow, frameId = frame.Id, snapshot }, ct);
            return snapshot;
        }
        public Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct)
            => inner.ResolveAsync(frame, snapshot, controlId, ct);
    }

    private sealed class ScopedModel(IModelProvider inner, ScopedObserver scope, Evidence evidence) : IModelProvider, ITaskIntentProvider
    {
        private readonly object _sync = new();
        private readonly List<RecordingModelProvider> _recorders = [];
        private long _input, _output;
        private int _pending;
        private bool _inputKnown = true, _outputKnown = true;
        public int ApiAttempts { get { lock (_sync) return _recorders.Sum(r => r.ApiAttempts); } }
        public ProviderUsage Usage
        {
            get
            {
                lock (_sync)
                {
                    bool complete = _pending == 0 && _recorders.Any(r => r.ApiAttempts > 0);
                    return new(complete && _inputKnown ? _input : null, complete && _outputKnown ? _output : null);
                }
            }
        }
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
            => CallAsync(request, ct, readOnlyStage: false);

        public Task<ProviderReply> InterpretAsync(ModelRequest request, CancellationToken ct)
            => CallAsync(request, ct, readOnlyStage: true);

        private async Task<ProviderReply> CallAsync(ModelRequest request, CancellationToken ct, bool readOnlyStage)
        {
            if (readOnlyStage && inner is not ITaskIntentProvider)
                throw new ProviderCallException("TASK_INTERPRETATION_UNSUPPORTED");
            // Diagnostic action restrictions remain local execution policy, not added user intent.
            if (!readOnlyStage)
                request = new ModelRequest(request.Task, request.CurrentFrame, request.RecentResults,
                    request.ProtocolPrompt + "\nBOUNDED TEST SCOPE: Stay within this current application window and its own controls. Use this application's own navigation and search. WIN shortcuts, the Start menu, ALT+TAB, launching or switching to other applications are outside this test and will be blocked. This scope is specific to this diagnostic task.",
                    request.Scope, request.OverviewContext, request.Controls, request.UntrustedModelObservations)
                    { RecoveryLevel = request.RecoveryLevel, SearchContinuation = request.SearchContinuation, Reconsidering = request.Reconsidering };
            scope.CheckFrame(request.CurrentFrame);
            if (request.OverviewContext is { } overview) scope.CheckFrame(overview);
            _ = await scope.GetEnvironmentAsync(ct);
            RecordingModelProvider recorder;
            lock (_sync)
            {
                string directory = evidence.NewModelDirectory(_recorders.Count + 1);
                recorder = new(inner, directory, scope.ProcessId, request.CurrentFrame.Foreground.HwndHex);
                _recorders.Add(recorder);
                _pending++;
            }
            ProviderUsage? usage = null;
            try
            {
                var reply = readOnlyStage ? await recorder.InterpretAsync(request, ct) : await recorder.DecideAsync(request, ct);
                usage = reply.Usage;
                return reply;
            }
            catch (ProviderCallException error)
            {
                usage = error.ResponseMetadata?.Usage;
                if (error.Code.StartsWith("DIAGNOSTIC_EVIDENCE", StringComparison.Ordinal)) evidence.MarkFault();
                throw;
            }
            catch (IOException) { evidence.MarkFault(); throw; }
            finally
            {
                lock (_sync)
                {
                    _pending--;
                    if (recorder.ApiAttempts > 0)
                    {
                        if (usage?.InputTokens is { } input) _input += input; else _inputKnown = false;
                        if (usage?.OutputTokens is { } output) _output += output; else _outputKnown = false;
                    }
                }
            }
        }
        public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordedInput(IInputExecutor inner, ScopedObserver scope, Evidence evidence) : IInputExecutor
    {
        private int _calls;
        private long _applied;
        public int ExecutionCalls => Volatile.Read(ref _calls);
        public long AppliedEvents => Interlocked.Read(ref _applied);
        public async Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
        {
            var before = await scope.GetEnvironmentAsync(ct);
            int index = ExecutionCalls + 1;
            var concreteAction = JsonSerializer.SerializeToElement(action.Action, action.Action.GetType());
            await evidence.WriteAsync($"input-{index:D3}-before.json", new
            {
                atUtc = DateTimeOffset.UtcNow, action.Lease, action.FrameId, action.ProposalId,
                actionType = action.Action.GetType().Name, action = concreteAction, foreground = before.Foreground
            }, ct);
            ActionResult? result = null;
            string? errorCode = null;
            bool? targetStillForeground = null;
            var timer = Stopwatch.StartNew();
            try
            {
                ct.ThrowIfCancellationRequested();
                _ = await scope.GetEnvironmentAsync(ct);
                Interlocked.Increment(ref _calls);
                result = await inner.ExecuteAsync(lease, action, ct);
                Interlocked.Add(ref _applied, result.AppliedEventCount);
                _ = await scope.GetEnvironmentAsync(ct);
                targetStillForeground = true;
                return result;
            }
            catch (Exception error)
            {
                errorCode = SafeError(error);
                targetStillForeground = error is InvalidOperationException && error.Message == "TARGET_PROCESS_NOT_FOREGROUND" ? false : null;
                throw;
            }
            finally
            {
                await evidence.WriteAsync($"input-{index:D3}-completed.json", new
                {
                    atUtc = DateTimeOffset.UtcNow, elapsedMs = timer.ElapsedMilliseconds, action.ProposalId,
                    action = concreteAction, result, errorCode, targetStillForeground,
                    executionCalls = ExecutionCalls, appliedInputEvents = AppliedEvents
                }, CancellationToken.None);
            }
        }
    }

    private sealed class Evidence
    {
        private readonly string _directory;
        private readonly object _progressSync = new();
        private int _faulted, _closing, _progressCount;
        public bool Faulted => Volatile.Read(ref _faulted) != 0;
        public bool Closing => Volatile.Read(ref _closing) != 0;
        public void MarkFault() => Interlocked.Exchange(ref _faulted, 1);
        public Evidence(string directory)
        {
            _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            CheckDirectory();
            if (Directory.Exists(_directory) && Directory.EnumerateFileSystemEntries(_directory).Any())
                throw new InvalidOperationException("EVIDENCE_DIRECTORY_NOT_EMPTY");
            Directory.CreateDirectory(_directory);
            CheckDirectory();
        }
        private void CheckDirectory()
        {
            for (var parent = new DirectoryInfo(_directory); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("EVIDENCE_REPARSE_POINT");
        }
        public string NewModelDirectory(int index)
        {
            CheckDirectory();
            string path = Path.Combine(_directory, $"model-call-{index:D3}");
            if (Directory.Exists(path)) throw new InvalidOperationException("MODEL_EVIDENCE_ALREADY_EXISTS");
            Directory.CreateDirectory(path);
            return path;
        }
        public void BeginCleanup() { lock (_progressSync) Interlocked.Exchange(ref _closing, 1); }
        public void RecordProgress(AgentProgress progress)
        {
            lock (_progressSync)
            {
                try
                {
                    CheckDirectory();
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { atUtc = DateTimeOffset.UtcNow, progress }, new JsonSerializerOptions { WriteIndented = true });
                    WriteAtomic($"progress-{++_progressCount:D4}.json", bytes);
                    if (!Closing) WriteAtomic("result-before-stop.json", bytes);
                }
                catch { Interlocked.Exchange(ref _faulted, 1); throw; }
            }
        }
        private void WriteAtomic(string name, byte[] bytes)
        {
            string path = Path.Combine(_directory, name), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(flushToDisk: true); }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public Task WriteAsync<T>(string name, T value, CancellationToken ct) => WriteBytesAsync(name, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }), ct);
        public async Task WriteBytesAsync(string name, byte[] bytes, CancellationToken ct)
        {
            try { CheckDirectory(); await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(_directory, name), bytes, ct); }
            catch { Interlocked.Exchange(ref _faulted, 1); throw; }
        }
    }
}
