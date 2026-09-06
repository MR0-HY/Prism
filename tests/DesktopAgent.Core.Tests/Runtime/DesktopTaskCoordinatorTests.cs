using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Core.Providers;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class DesktopTaskCoordinatorTests
{
    private sealed class Desktop : IDesktopObserver
    {
        private int _sequence;
        private Frame? _last;
        public static readonly PhysicalRect Bounds = new(0, 0, 1000, 800);
        public static readonly ForegroundIdentity Foreground = new("1", 100, "Fixture", Bounds);
        public ImmutableArray<PhysicalRect> OwnWindowRects = [];
        public ForegroundIdentity CurrentForeground = Foreground;
        public long DisplayGeneration = 1;
        public string? CapturedMonitorOverride;
        public Func<int, CancellationToken, Task>? BeforeCapture;
        public Func<int, byte[]>? ImageBytesAt;
        public int Captures => Volatile.Read(ref _sequence);
        public Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct) => Task.FromResult(new DesktopEnvironment(DisplayGeneration,
            [new("display", Bounds, Bounds, 120, 120, true)], CurrentForeground, DesktopSessionState.Available));
        public async Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? physicalRegion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int sequence = Interlocked.Increment(ref _sequence);
            if (BeforeCapture is not null) await BeforeCapture(sequence, ct);
            var region = physicalRegion ?? Bounds;
            var frame = new Frame("frame-" + sequence, lease, DateTimeOffset.UtcNow, DisplayGeneration, CapturedMonitorOverride ?? monitorId, region,
                new(region.Width, region.Height, "image/png", ImageBytesAt?.Invoke(sequence) ?? [1, 2, 3]), physicalRegion is null ? FrameViewKind.Overview : FrameViewKind.Crop,
                CurrentForeground, OwnWindowRects, physicalRegion is null ? null : _last!.Id);
            _last = frame;
            return frame;
        }
    }
    private sealed class Overlay(Action<PhysicalRect>? moved = null) : IOverlayController
    {
        public readonly ConcurrentQueue<PhysicalRect> Exclusions = new();
        public Task SetStateAsync(OverlayState state, CancellationToken ct) => Task.CompletedTask;
        public Task<IAsyncDisposable> HideForCaptureAsync(Lease lease, CancellationToken ct) => throw new NotSupportedException();
        public Task RelocateAsync(Lease lease, PhysicalRect excludedRegion, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Exclusions.Enqueue(excludedRegion); moved?.Invoke(excludedRegion); return Task.CompletedTask; }
    }
    private sealed class Controls : IControlObserver
    {
        public int Resolutions, Observations;
        public ControlSnapshotStatus Status = ControlSnapshotStatus.Available;
        public ControlResolution Resolution = new(new(500, 400), null);
        public ImmutableArray<ControlCandidate> Candidates = [new("c1", null, "普通外观选项", "Button", new(480, 380, 40, 40), true, false, true, null, null)];
        public Func<int, ImmutableArray<ControlCandidate>>? CandidatesAt;
        public Func<int, ControlSnapshotStatus>? StatusAt;
        public Func<int, CancellationToken, Task>? BeforeObserve;
        public async Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct)
        {
            int count = ++Observations;
            if (BeforeObserve is not null) await BeforeObserve(count, ct);
            return new ControlSnapshot(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id,
                DateTimeOffset.UtcNow, StatusAt?.Invoke(count) ?? Status, CandidatesAt?.Invoke(count) ?? Candidates);
        }
        public Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct)
        { Resolutions++; return Task.FromResult(Resolution); }
    }
    private class Provider(Func<ModelRequest, int, CancellationToken, Task<ProviderReply>> handler) : IModelProvider
    {
        public readonly ConcurrentQueue<ModelRequest> Requests = new();
        private int _calls;
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
        { Requests.Enqueue(request); return handler(request, Interlocked.Increment(ref _calls), ct); }
        public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class IntentProvider(Func<ModelRequest, int, CancellationToken, Task<ProviderReply>> handler,
        Func<ModelRequest, CancellationToken, Task<ProviderReply>> interpret) : Provider(handler), ITaskIntentProvider
    {
        public readonly ConcurrentQueue<ModelRequest> IntentRequests = new();
        public Task<ProviderReply> InterpretAsync(ModelRequest request, CancellationToken ct)
        { IntentRequests.Enqueue(request); return interpret(request, ct); }
    }
    private sealed class Input : IInputExecutor
    {
        public readonly ConcurrentQueue<ValidatedAction> Actions = new();
        public ActionStatus Status = ActionStatus.Injected;
        public int AppliedEvents = 1;
        public Action? AfterExecute;
        public bool WaitForCancel;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Actions.Enqueue(action);
            Entered.TrySetResult();
            if (WaitForCancel) { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Status = ActionStatus.Cancelled; } }
            AfterExecute?.Invoke();
            return new ActionResult(action.ProposalId, lease.TaskId, lease.Epoch, Status,
                Status == ActionStatus.Uncertain ? "PARTIAL_INPUT" : null, DateTimeOffset.UtcNow, 1, AppliedEvents, 1, "test input", null);
        }
    }
    private sealed class Harness : IDisposable
    {
        public readonly InputSafetyGate Gate = new();
        public readonly ProviderProfile Profile;
        public readonly Provider Provider;
        public readonly Input Input = new();
        public readonly DesktopTaskCoordinator Coordinator;
        public Harness(Func<ModelRequest, int, CancellationToken, Task<ProviderReply>> handler, bool verified = true,
            Desktop? desktop = null, IOverlayController? overlay = null, IControlObserver? controls = null,
            bool controlsRequired = false, ProviderProfile? profile = null,
            Func<ModelRequest, CancellationToken, Task<ProviderReply>>? interpret = null)
        {
            Gate.SetHotkeysReady(true); // Test fixture only. Production calls the real registered hotkey owner.
            Profile = ProviderConfiguration.DefaultDeepSeek();
            if (verified)
            {
                string fingerprint = ProviderConfiguration.Fingerprint(Profile);
                Profile = Profile with { ProbeFingerprint = fingerprint, Capabilities = new[] { "vision", "grounding", "schema" }
                    .Select(n => new CapabilityRecord(n, CapabilityStatus.ProbePassed, null, DateTimeOffset.UtcNow, fingerprint, "TEST_ONLY")).ToImmutableArray() };
            }
            if (profile is not null) Profile = profile;
            Provider = interpret is null ? new Provider(handler) : new IntentProvider(handler, interpret);
            Coordinator = new(Gate, desktop ?? new Desktop(), controls, Provider, Profile, new DesktopPolicyValidator(TimeSpan.FromSeconds(90)), _ => Input, overlay: overlay, controlsRequired: controlsRequired);
        }
        public Task<TaskContext> Start(TaskBudget? budget = null, string goal = "把普通显示选项设为深色") => Coordinator.StartAsync(new(goal, ProviderConfiguration.Fingerprint(Profile), "display", budget ?? TaskBudget.Default), default);
        public Task Done() => Coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        public void Dispose() => Gate.Trip(InputStopReason.Shutdown);
    }
    private static ProviderReply Reply(ModelRequest request, object decision, string? id = null,
        string current = "核对当前画面", string next = "下一步") => new(JsonSerializer.Serialize(new
    {
        schemaVersion = 2, proposalId = id ?? Guid.NewGuid().ToString("N"), taskId = request.Task.Lease.TaskId, epoch = request.Task.Lease.Epoch,
        frameId = request.CurrentFrame.Id, current, next, decision
    }), new(10, 4));
    private static object Click => new { kind = "act", action = new { type = "click", x = 500, y = 500, button = "left", clickCount = 1 }, target = "普通外观选项", expected = "选项改变" };
    private static object Finish(ModelRequest r) => new { kind = "finish", outcome = "succeeded", summary = "已核对新状态", evidence = new[]
    { new { frameId = r.CurrentFrame.Id, appName = "Fixture", observedText = "深色", interpretation = "目标状态已显示" } } };

    private static Task<ProviderReply> Interpret(ModelRequest request, CancellationToken ct) => Task.FromResult(new ProviderReply(
        JsonSerializer.Serialize(new { goal = "打开计算器，计算17乘以23并核对结果", reply = "我先选17和23，打开计算器相乘并核对结果。" }), new(20, 8)));

    [Fact]
    public async Task InterpretationKeepsOriginalGoalAndChosenDefaultsWithFreshActionFrame()
    {
        var desktop = new Desktop();
        using var h = new Harness((r, n, _) =>
        {
            Assert.Equal("打开计算器抽取两个随机数帮我相乘", r.Task.Goal);
            Assert.Contains("17", r.Task.Interpretation!.Goal);
            Assert.Contains("23", r.Task.Interpretation.Reply);
            Assert.True(desktop.Captures >= 2);
            return Task.FromResult(Reply(r, n == 1 ? Click : Finish(r)));
        }, desktop: desktop, interpret: Interpret);
        await h.Start(goal: "打开计算器抽取两个随机数帮我相乘"); await h.Done();
        var intentRequest = Assert.Single(((IntentProvider)h.Provider).IntentRequests);
        Assert.NotEqual(intentRequest.CurrentFrame.Id, h.Input.Actions.Single().FrameId);
        Assert.Single(h.Input.Actions);
        Assert.Equal(3, h.Coordinator.Progress!.Task.Usage.ApiAttempts);
        Assert.Equal(40, h.Coordinator.Progress.Task.Usage.InputTokens);
        Assert.Empty(h.Coordinator.Progress.Task.Clarifications);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State);
    }

    [Fact]
    public async Task InterpretationConsumesBudgetAndNeverExecutesItsOwnText()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Click)), interpret: Interpret);
        await h.Start(new(30, 1, 10000)); await h.Done();
        Assert.Single(((IntentProvider)h.Provider).IntentRequests);
        Assert.Empty(h.Provider.Requests); Assert.Empty(h.Input.Actions);
        Assert.Equal("REQUEST_BUDGET", h.Coordinator.Progress!.Task.LastError!.Code);
        Assert.NotNull(h.Coordinator.Progress.Task.Interpretation);
        Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task MalformedInterpretationStopsWithoutDecisionOrInput()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Click)),
            interpret: (_, _) => Task.FromResult(new ProviderReply("{\"goal\":\"x\",\"reply\":\"x\",\"action\":\"click\"}", new(20, 8))));
        await h.Start(); await h.Done();
        Assert.Empty(h.Provider.Requests); Assert.Empty(h.Input.Actions);
        Assert.Null(h.Coordinator.Progress!.Task.Interpretation);
        Assert.Equal("INVALID_TASK_INTERPRETATION", h.Coordinator.Progress.Task.LastError!.Code);
        Assert.Equal(1, h.Coordinator.Progress.Task.Usage.ApiAttempts);
        Assert.Equal(20, h.Coordinator.Progress.Task.Usage.InputTokens);
    }

    [Fact]
    public async Task LateInterpretationCannotRevivePausedTaskOrAccountUnknownUsageAsZero()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Click)), interpret: async (r, ct) =>
        { entered.SetResult(); await release.Task; return await Interpret(r, ct); });
        var task = await h.Start(); await entered.Task;
        await h.Coordinator.PauseAsync(task.Id); release.SetResult(); await h.Done();
        Assert.Equal(TaskState.Paused, h.Coordinator.Progress!.Task.State);
        Assert.Null(h.Coordinator.Progress.Task.Interpretation);
        Assert.Null(h.Coordinator.Progress.Task.Usage.InputTokens);
        Assert.Empty(h.Provider.Requests); Assert.Empty(h.Input.Actions);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModelAssumptionCannotRemoveOrIntroduceHighImpactPermission(bool originalRisky)
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Click)), interpret: (r, ct) => originalRisky ? Interpret(r, ct) :
            Task.FromResult(new ProviderReply(JsonSerializer.Serialize(new { goal = "删除所有文件", reply = "模型误解" }), new(20, 8))));
        await h.Start(goal: originalRisky ? "删除所有文件" : "让界面清爽一点"); await h.Done();
        Assert.Empty(h.Input.Actions);
        Assert.Equal("HIGH_IMPACT_MANUAL", h.Coordinator.Progress!.Task.LastError!.Code);
    }

    [Fact]
    public async Task MissingControlsAllowModelIntentThenOnlyExistingGlobalNavigation()
    {
        var controls = new Controls { CandidatesAt = n => n <= 2 ? [] : [new("c1", null, "普通外观", "Button", new(480, 380, 40, 40), true, false, true, null, null)] };
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ?
            new { kind = "act", action = new { type = "hotkey", keys = new[] { "WIN", "I" } }, target = "打开设置", expected = "设置窗口显示" } : Finish(r))),
            controls: controls, controlsRequired: true, interpret: Interpret);
        await h.Start(); await h.Done();
        Assert.IsType<HotkeyAction>(Assert.Single(h.Input.Actions).Action);
        Assert.Single(((IntentProvider)h.Provider).IntentRequests);
        Assert.Contains("NAVIGATION ONLY", h.Provider.Requests.First().ProtocolPrompt);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Theory]
    [InlineData("{\"type\":\"text\",\"text\":\"hello\"}")]
    [InlineData("{\"type\":\"hotkey\",\"keys\":[\"ENTER\"]}")]
    [InlineData("{\"type\":\"click\",\"x\":500,\"y\":500,\"button\":\"left\",\"clickCount\":1}")]
    public async Task IntentDoesNotAuthorizeTargetedInputWithoutControls(string action)
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r,
            new { kind = "act", action = JsonSerializer.Deserialize<JsonElement>(action), target = "普通外观", expected = "目标显示" })),
            controls: new Controls { Candidates = [] }, controlsRequired: true, interpret: Interpret);
        await h.Start(); await h.Done();
        Assert.Empty(h.Input.Actions);
        Assert.Equal("ASSISTED_CONTROLS_REQUIRED", h.Coordinator.Progress!.Task.LastError!.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnnecessaryQuestionGetsOneFreshModelReconsiderationThenContinuesOrAsks(bool solves)
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 2 && solves ? Finish(r) :
            new { kind = "ask_user", reason = "缺少普通选项", question = "你想选哪两个数？" })), interpret: Interpret);
        await h.Start(); await h.Done();
        Assert.Equal(2, h.Provider.Requests.Count);
        var calls = h.Provider.Requests.ToArray();
        Assert.False(calls[0].Reconsidering); Assert.True(calls[1].Reconsidering);
        Assert.NotEqual(calls[0].CurrentFrame.Id, calls[1].CurrentFrame.Id);
        Assert.Equal(calls[0].Task.Interpretation, calls[1].Task.Interpretation);
        Assert.Equal(3, h.Coordinator.Progress!.Task.Usage.ApiAttempts);
        Assert.Equal(solves ? TaskState.Succeeded : TaskState.WaitingUser, h.Coordinator.Progress.Task.State);
        Assert.Equal(!solves, h.Coordinator.Progress.Task.PendingQuestion is not null);
        Assert.Empty(h.Input.Actions);
    }

    [Fact]
    public async Task CorrectingGoalClearsInterpretationButOrdinaryAnswerPreservesIt()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r,
            new { kind = "ask_user", reason = "登录需要用户", question = "请完成账号登录。" })), interpret: Interpret);
        await h.Start(); await h.Done();
        var task = h.Coordinator.Progress!.Task;
        await h.Coordinator.AnswerAsync(task.Lease, task.PendingQuestion!.Id, "已完成");
        Assert.Equal(task.Interpretation, h.Coordinator.Progress!.Task.Interpretation);
        await h.Coordinator.CorrectAsync(task.Id, "打开新的普通应用");
        Assert.Null(h.Coordinator.Progress.Task.Interpretation);
        Assert.Empty(h.Coordinator.Progress.Task.Clarifications);
    }

    private static ProviderProfile HybridProfile()
    {
        var profile = ProviderConfiguration.DefaultDeepSeek();
        string fingerprint = ProviderConfiguration.Fingerprint(profile);
        return profile with { Capabilities = new[] { "hybrid_vision", "hybrid_grounding", "hybrid_schema" }
            .Select(n => new CapabilityRecord(n, CapabilityStatus.ProbePassed, null, DateTimeOffset.UtcNow, fingerprint, "TEST_ONLY")).ToImmutableArray() };
    }

    [Theory]
    [InlineData("right", 1, true)]
    [InlineData("left", 1, false)]
    [InlineData("right", 2, false)]
    public async Task NativeDesktopBackgroundOnlyUsesResolvedSingleRightClick(string button, int clickCount, bool allowed)
    {
        var desktop = new Desktop { CurrentForeground = Desktop.Foreground with { ProcessName = "explorer", WindowClass = "Progman" } };
        var controls = new Controls { Candidates = [new("background", null, "桌面空白区域", "DesktopBackground", new(480, 380, 40, 40), true, false, false, null, null)] };
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ?
            new { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "background", button, clickCount, target = "桌面空白处", expected = "桌面菜单显示" } : Finish(r))),
            desktop: desktop, controls: controls, controlsRequired: true);
        await h.Start(goal: "桌面上右键创建一个文件夹"); await h.Done();
        if (allowed)
        {
            var click = Assert.IsType<ClickAction>(Assert.Single(h.Input.Actions).Action);
            Assert.Equal(MouseButton.Right, click.Button); Assert.Equal(1, click.ClickCount);
            Assert.Equal(1, controls.Resolutions);
        }
        else
        {
            Assert.Empty(h.Input.Actions); Assert.Equal(0, controls.Resolutions);
            Assert.Equal("DESKTOP_BACKGROUND_RIGHT_CLICK_ONLY", h.Coordinator.Progress!.Task.LastError!.Code);
        }
    }

    [Theory]
    [InlineData("D")]
    [InlineData("M")]
    public async Task ShowDesktopAndMinimizeRemainAllowedWithoutFocusedControl(string key)
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ?
            new { kind = "act", action = new { type = "hotkey", keys = new[] { "WIN", key } }, target = "显示桌面", expected = "所有窗口最小化" } : Finish(r))),
            controls: new Controls(), controlsRequired: true);
        await h.Start(goal: "最小化所有窗口"); await h.Done();
        Assert.Single(h.Input.Actions);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Fact]
    public async Task RequestedWindowCloseUsesForegroundIdentityInsteadOfEditFocus()
    {
        var desktop = new Desktop { CurrentForeground = Desktop.Foreground with { ProcessName = "SystemSettings", WindowClass = "ApplicationFrameWindow" } };
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ?
            new { kind = "act", action = new { type = "hotkey", keys = new[] { "ALT", "F4" } }, target = "关闭设置窗口", expected = "设置窗口关闭" } : Finish(r))),
            desktop: desktop, controls: new Controls(), controlsRequired: true);
        await h.Start(goal: "把设置关掉"); await h.Done();
        Assert.Single(h.Input.Actions);
    }

    [Fact]
    public async Task ForegroundClassChangedAfterReplyCannotCloseDesktopAsOldApplication()
    {
        var desktop = new Desktop { CurrentForeground = Desktop.Foreground with { ProcessName = "explorer", WindowClass = "CabinetWClass" } };
        using var h = new Harness((r, _, _) =>
        {
            desktop.CurrentForeground = desktop.CurrentForeground with { WindowClass = "Progman" };
            return Task.FromResult(Reply(r, new { kind = "act", action = new { type = "hotkey", keys = new[] { "ALT", "F4" } }, target = "关闭窗口", expected = "窗口关闭" }));
        }, desktop: desktop, controls: new Controls(), controlsRequired: true);
        await h.Start(goal: "关闭资源管理器窗口"); await h.Done();
        Assert.Empty(h.Input.Actions);
    }

    [Fact]
    public async Task VisualCycleStopsBeforeReinjectingAcrossDifferentProposalAndFrameIds()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r,
            n == 2 ? new { kind = "act", action = new { type = "click", x = 600, y = 500, button = "left", clickCount = 1 }, target = "普通外观选项", expected = "选项改变" } : Click)));
        await h.Start(); await h.Done();
        Assert.Equal(5, h.Provider.Requests.Count);
        Assert.Equal(2, h.Input.Actions.Count);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        Assert.Equal("VISUAL_ACTION_LOOP", h.Coordinator.Progress.Task.LastError!.Code);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
        Assert.False(h.Gate.Status.IsOpen);
        Assert.Equal(5, h.Provider.Requests.Select(r => r.CurrentFrame.Id).Distinct().Count());
        Assert.Equal(new[] { 0, 0, 0, 1, 2 }, h.Provider.Requests.Select(r => r.RecoveryLevel));
        Assert.NotNull(h.Coordinator.Progress.Task.PendingQuestion);
    }

    [Fact]
    public async Task RecoveryCanChooseDifferentControlAndFinishWithoutResettingTask()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n switch
        {
            <= 2 => Click,
            3 => new { kind = "act", action = new { type = "click", x = 800, y = 500, button = "left", clickCount = 1 }, target = "另一个选项", expected = "选项改变" },
            _ => Finish(r)
        })));
        var task = await h.Start(); await h.Done();
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.Equal(task.Id, h.Coordinator.Progress.Task.Id);
        Assert.Equal(2, h.Input.Actions.Count);
        Assert.Equal(new[] { 0, 0, 1, 0 }, h.Provider.Requests.Select(r => r.RecoveryLevel));
    }

    [Fact]
    public async Task RecoveryNeverExceedsOriginalRequestBudget()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Click)));
        await h.Start(new(10, 2, 10000)); await h.Done();
        Assert.Equal(2, h.Provider.Requests.Count);
        Assert.Single(h.Input.Actions);
        Assert.NotNull(h.Coordinator.Progress!.Task.PendingQuestion);
        Assert.False(h.Gate.Status.IsOpen);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SearchContinuationUsesFreshFrameAndSeparateRequestBudget(int budget)
    {
        using var h = new Harness((r, n, _) => Task.FromResult(n == 4
            ? new ProviderReply("", new(null, null)) { SearchContinuation = "search-reference-fixture" }
            : Reply(r, n == 5 ? Finish(r) : Click)));
        await h.Start(new(10, budget, 10000)); await h.Done();
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(budget, requests.Length); Assert.Single(h.Input.Actions);
        Assert.Null(h.Coordinator.Progress!.Task.Usage.InputTokens);
        if (budget == 5)
        {
            Assert.Equal("search-reference-fixture", requests[4].SearchContinuation);
            Assert.NotEqual(requests[3].CurrentFrame.Id, requests[4].CurrentFrame.Id);
            Assert.Equal(0, requests[4].RecoveryLevel);
            Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State);
        }
        else Assert.NotNull(h.Coordinator.Progress.Task.PendingQuestion);
    }

    [Fact]
    public async Task StopDuringRecoveryRejectsLateAnswer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness(async (r, n, _) =>
        {
            if (n == 3) { entered.SetResult(); await release.Task; return Reply(r, Finish(r)); }
            return Reply(r, Click);
        });
        var task = await h.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = h.Coordinator.StopAsync(task.Id);
        release.SetResult(); await stopped; await h.Done();
        Assert.Single(h.Input.Actions);
        Assert.Equal(TaskState.Cancelled, h.Coordinator.Progress!.Task.State);
        Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task ClearControlCannotSatisfyParenthesisTarget()
    {
        var controls = new Controls { Candidates = [new("c1", null, "C", "Button", new(480, 380, 40, 40), true, false, true, null, null)] };
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, new
        { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "c1", button = "left", clickCount = 1, target = "左括号", expected = "输入左括号" })),
            controls: controls, controlsRequired: true, profile: HybridProfile());
        await h.Start(); await h.Done();
        Assert.Empty(h.Input.Actions); Assert.Equal(0, controls.Resolutions);
        Assert.Equal("CONTROL_MEANING_CONFLICT", h.Coordinator.Progress!.Task.LastError!.Code);
        Assert.NotNull(h.Coordinator.Progress.Task.PendingQuestion);
        Assert.Equal(new[] { 0, 1, 2 }, h.Provider.Requests.Select(r => r.RecoveryLevel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelFailureOrPolicyWaitRetainsAReplyableQuestion(bool policyWait)
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, policyWait
            ? new { kind = "act", action = new { type = "click", x = 500, y = 500, button = "left", clickCount = 1 }, target = "删除文件", expected = "文件消失" }
            : new { kind = "fail", code = "CANNOT_IDENTIFY", reason = "无法识别目标" })));
        await h.Start(); await h.Done();
        Assert.Empty(h.Input.Actions);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        Assert.NotNull(h.Coordinator.Progress.Task.PendingQuestion);
        Assert.False(h.Gate.Status.IsOpen);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedPixelsOrForegroundPermitTheSameAction(bool changeForeground)
    {
        var desktop = new Desktop { ImageBytesAt = n => changeForeground ? [1, 2, 3] : [(byte)n] };
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n <= 2 ? Click : Finish(r))), desktop: desktop);
        if (changeForeground) h.Input.AfterExecute = () => desktop.CurrentForeground = new("2", 101, "Fixture", Desktop.Bounds);
        await h.Start(); await h.Done();
        Assert.Equal(2, h.Input.Actions.Count);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Theory]
    [InlineData(ActionStatus.Rejected, 0)]
    [InlineData(ActionStatus.Injected, 0)]
    public async Task NoAppliedInputDoesNotRegisterAVisualCycle(ActionStatus status, int appliedEvents)
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n <= 2 ? Click : Finish(r))));
        h.Input.Status = status; h.Input.AppliedEvents = appliedEvents;
        await h.Start(); await h.Done();
        Assert.Equal(2, h.Input.Actions.Count);
        // Two input rejections still invoke the existing rejection guard.
        Assert.Equal(status == ActionStatus.Rejected ? TaskState.WaitingUser : TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.NotEqual("VISUAL_ACTION_LOOP", h.Coordinator.Progress.Task.LastError?.Code);
    }

    [Fact]
    public async Task NextRequestsCarryReportedOriginalAndStagesSeparatelyFromAttemptExpectations()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n <= 2 ? Click : Finish(r),
            current: n switch { 1 => "原值浅色，尚未修改", 2 => "当前深色已观察；原值浅色", _ => "当前已恢复浅色" },
            next: n switch { 1 => "切换深色", 2 => "恢复浅色", _ => "完成" })),
            desktop: new Desktop { ImageBytesAt = n => [(byte)n] });
        await h.Start(); await h.Done();
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(3, requests.Length); Assert.Equal(2, h.Input.Actions.Count);
        Assert.Empty(requests[0].UntrustedModelObservations);
        var original = Assert.Single(requests[1].UntrustedModelObservations);
        Assert.Equal("原值浅色，尚未修改", original.Current); Assert.Equal("切换深色", original.Next);
        Assert.Equal(requests[0].CurrentFrame.Id, original.FrameId); Assert.Equal(requests[0].Task.Lease, original.Lease);
        Assert.Equal(new[] { "原值浅色，尚未修改", "当前深色已观察；原值浅色" }, requests[2].UntrustedModelObservations.Select(o => o.Current));
        Assert.Equal("恢复浅色", requests[2].UntrustedModelObservations[1].Next);
        Assert.All(requests[2].UntrustedModelObservations, o =>
        { Assert.False(o.Verified); Assert.False(o.InputAuthority); Assert.DoesNotContain("选项改变", o.Current + o.Next); });
        Assert.Contains("预期效果（未验证）：选项改变", requests[2].RecentResults[0].PublicSummary);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.Equal("当前已恢复浅色", h.Coordinator.Progress.Task.UntrustedModelObservations[^1].Current);
    }

    [Fact]
    public async Task ObservationMemoryKeepsInitialBaselineReportsAndRecentStagesWithinTwelveEntries()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r,
            n < 20 ? new { kind = "select_monitor", monitorId = "display" } : Finish(r),
            current: "observed-stage-" + n, next: "planned-stage-" + (n + 1))));
        await h.Start(); await h.Done();
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(20, requests.Length); Assert.Empty(h.Input.Actions);
        Assert.All(requests, r => Assert.InRange(r.UntrustedModelObservations.Length, 0, UntrustedModelObservation.MaximumEntries));
        var last = requests[^1].UntrustedModelObservations;
        Assert.Equal(Enumerable.Range(1, 6).Concat(Enumerable.Range(14, 6)).Select(n => "observed-stage-" + n), last.Select(o => o.Current));
        Assert.Equal(requests.Take(6).Concat(requests.Skip(13).Take(6)).Select(r => r.CurrentFrame.Id), last.Select(o => o.FrameId));
        Assert.Equal(UntrustedModelObservation.MaximumEntries, h.Coordinator.Progress!.Task.UntrustedModelObservations.Length);
        Assert.Empty(requests[^1].RecentResults); // No attempted action or expectation was converted into an observation.
    }

    [Theory]
    [InlineData("task")]
    [InlineData("epoch")]
    [InlineData("historical-frame")]
    [InlineData("malformed")]
    public async Task InvalidOrHistoricalScopeCannotAppendMemoryOrAuthorizeInput(string invalid)
    {
        ModelRequest? first = null;
        using var h = new Harness((r, n, _) =>
        {
            if (n == 1)
            { first = r; return Task.FromResult(Reply(r, new { kind = "select_monitor", monitorId = "display" }, current: "原值浅色")); }
            if (n != 2) return Task.FromResult(Reply(r, Finish(r)));
            string content = invalid == "malformed" ? "{" : JsonSerializer.Serialize(new
            {
                schemaVersion = 2, proposalId = Guid.NewGuid().ToString("N"),
                taskId = invalid == "task" ? Guid.NewGuid() : r.Task.Lease.TaskId,
                epoch = invalid == "epoch" ? r.Task.Lease.Epoch + 1 : r.Task.Lease.Epoch,
                frameId = invalid == "historical-frame" ? first!.CurrentFrame.Id : r.CurrentFrame.Id,
                current = "invalid-baseline", next = "invalid-stage", decision = Click
            });
            return Task.FromResult(new ProviderReply(content, new(10, 4)));
        });
        await h.Start(); await h.Done();
        Assert.Equal(3, h.Provider.Requests.Count); Assert.Empty(h.Input.Actions);
        var carried = Assert.Single(h.Provider.Requests.Last().UntrustedModelObservations);
        Assert.Equal("原值浅色", carried.Current);
        Assert.DoesNotContain(h.Coordinator.Progress!.Task.UntrustedModelObservations, o => o.Current == "invalid-baseline");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseKeepsOnlyEarlierReportsWhileCorrectedGoalsAndNewTasksStartEmpty(bool correctGoal)
    {
        var entered = new TaskCompletionSource<ModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness((r, n, _) =>
        {
            if (n == 1) return Task.FromResult(Reply(r, new { kind = "select_monitor", monitorId = "display" }, current: "原值浅色"));
            if (n == 2) { entered.TrySetResult(r); return late.Task; }
            return Task.FromResult(Reply(r, Finish(r)));
        });
        try
        {
            var task = await h.Start();
            var pending = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await h.Coordinator.PauseAsync(task.Id);
            late.TrySetResult(Reply(pending, Click, current: "late-baseline-must-not-survive"));
            await h.Done();
            Assert.Equal("原值浅色", Assert.Single(h.Coordinator.Progress!.Task.UntrustedModelObservations).Current);
            if (correctGoal)
            {
                await h.Coordinator.CorrectAsync(task.Id, "查看另一个普通选项");
                Assert.Empty(h.Coordinator.Progress.Task.UntrustedModelObservations);
            }
            await h.Coordinator.ResumeAsync(task.Id, default); await h.Done();
            var resumed = h.Provider.Requests.Last();
            Assert.True(resumed.Task.Lease.Epoch > pending.Task.Lease.Epoch);
            Assert.NotEqual(resumed.CurrentFrame.Id, pending.CurrentFrame.Id);
            if (correctGoal) Assert.Empty(resumed.UntrustedModelObservations);
            else Assert.True(Assert.Single(resumed.UntrustedModelObservations).Lease.Epoch < resumed.Task.Lease.Epoch);
            Assert.Empty(h.Input.Actions);
            var newTask = await h.Start(); await h.Done();
            Assert.NotEqual(task.Id, newTask.Id); Assert.Empty(h.Provider.Requests.Last().UntrustedModelObservations);
        }
        finally { late.TrySetCanceled(); }
    }

    [Theory]
    [InlineData(3L, 2L, 16L, 8L)]
    [InlineData(3L, null, 16L, null)]
    [InlineData(null, 2L, null, 8L)]
    [InlineData(null, null, null, null)]
    public async Task SafeUsageFromFailedResponseAccumulatesWithoutAuthorizingInput(long? input, long? output, long? totalInput, long? totalOutput)
    {
        var metadata = new ProviderResponseMetadata(200, "string", 0, true, "stop", new(input, output), true, 50, false);
        using var h = new Harness((r, n, _) => n == 1
            ? Task.FromResult(Reply(r, new { kind = "wait", milliseconds = 100, reason = "观察界面" }))
            : Task.FromException<ProviderReply>(new ProviderCallException("EMPTY_CONTENT", metadata)));
        await h.Start(); await h.Done();
        var task = h.Coordinator.Progress!.Task;
        Assert.Equal(3, task.Usage.ApiAttempts); Assert.Equal(totalInput, task.Usage.InputTokens); Assert.Equal(totalOutput, task.Usage.OutputTokens);
        Assert.Equal(TaskState.WaitingUser, task.State); Assert.Equal("EMPTY_CONTENT", task.LastError!.Code);
        Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task FailedResponseWithoutMetadataKeepsUsageUnknown()
    {
        using var h = new Harness((_, _, _) => Task.FromException<ProviderReply>(new ProviderCallException("NETWORK_ERROR")));
        await h.Start(); await h.Done();
        var task = h.Coordinator.Progress!.Task;
        Assert.Null(task.Usage.InputTokens); Assert.Null(task.Usage.OutputTokens);
        Assert.Equal(TaskState.WaitingUser, task.State); Assert.Empty(h.Input.Actions);
    }

    [Fact]
    public async Task LateErrorMetadataAfterPauseCannotUpdateUsageOrAuthorizeInput()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness((_, _, _) => { entered.TrySetResult(); return late.Task; });
        try
        {
            var task = await h.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await h.Coordinator.PauseAsync(task.Id);
            late.TrySetException(new ProviderCallException("EMPTY_CONTENT",
                new(200, "string", 0, true, "stop", new(900, 800), true, 600, false)));
            await h.Done();
            Assert.Equal(TaskState.Paused, h.Coordinator.Progress!.Task.State);
            Assert.Null(h.Coordinator.Progress.Task.Usage.InputTokens); Assert.Null(h.Coordinator.Progress.Task.Usage.OutputTokens);
            Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
        }
        finally { late.TrySetCanceled(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextObservationRemembersAttemptedTargetWithoutClaimingExpectedEffectWasObserved(bool useControls)
    {
        const string target = "海风主题按钮", expected = "海风主题状态变为已开启";
        var controls = new Controls();
        controls.Candidates = [controls.Candidates[0] with { Name = "海风主题" }];
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ? useControls ?
            new { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "c1", button = "left", clickCount = 1, target, expected } :
            new { kind = "act", action = new { type = "click", x = 500, y = 500, button = "left", clickCount = 1 }, target, expected }
            : Finish(r))), controls: useControls ? controls : null, controlsRequired: useControls);
        await h.Start(); await h.Done();
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(2, requests.Length); Assert.NotEqual(requests[0].CurrentFrame.Id, requests[1].CurrentFrame.Id);
        var result = Assert.Single(requests[1].RecentResults);
        Assert.Contains("尝试目标（模型提议）：" + target, result.PublicSummary);
        Assert.Contains("预期效果（未验证）：" + expected, result.PublicSummary);
        Assert.Contains("须用当前新画面核对", result.PublicSummary);
        Assert.Contains("预期不代表已观察结果", result.PublicSummary);
        Assert.Equal(ActionStatus.Injected, result.Status); Assert.Null(result.PostFrameId);
        Assert.Equal(1, result.AppliedEventCount); Assert.Null(result.Code);
        Assert.Single(h.Input.Actions);
    }

    [Theory]
    [InlineData(ActionStatus.Rejected)]
    [InlineData(ActionStatus.Uncertain)]
    [InlineData(ActionStatus.Cancelled)]
    public async Task FailedInputKeepsAttemptContextAndItsOriginalFailureStatus(ActionStatus status)
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ? Click :
            new { kind = "ask_user", reason = "需要核对实际状态", question = "请核对当前选项。" })));
        h.Input.Status = status;
        await h.Start(); await h.Done();
        var result = Assert.Single(h.Coordinator.Progress!.Task.RecentResults);
        Assert.Equal(status, result.Status);
        Assert.Contains("尝试目标（模型提议）：普通外观选项", result.PublicSummary);
        Assert.Contains("预期效果（未验证）：选项改变", result.PublicSummary);
        Assert.Contains("预期不代表已观察结果", result.PublicSummary);
        Assert.Null(result.PostFrameId); Assert.Equal(status == ActionStatus.Uncertain ? "PARTIAL_INPUT" : null, result.Code);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress.Task.State);
        Assert.Single(h.Input.Actions);
        if (status == ActionStatus.Rejected)
        {
            var next = h.Provider.Requests.Last();
            Assert.Equal(result, Assert.Single(next.RecentResults));
            Assert.Equal(2, h.Provider.Requests.Count);
        }
        else Assert.Single(h.Provider.Requests);
    }

    [Fact]
    public async Task RejectedControlResolutionKeepsProposedTargetButDoesNotClaimAnyInput()
    {
        var controls = new Controls { Resolution = new(null, "CONTROL_CHANGED") };
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ?
            new { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "c1", button = "left", clickCount = 1, target = "普通外观选项", expected = "选项开启" } :
            new { kind = "ask_user", reason = "控件发生变化", question = "请核对当前选项。" })), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        var next = h.Provider.Requests.Last();
        var result = Assert.Single(next.RecentResults);
        Assert.Equal(ActionStatus.Rejected, result.Status); Assert.Equal("CONTROL_CHANGED", result.Code);
        Assert.Equal(0, result.AppliedEventCount); Assert.Equal(0, result.ExpectedEventCount); Assert.Null(result.PostFrameId);
        Assert.Contains("未执行输入", result.PublicSummary);
        Assert.Contains("尝试目标（模型提议）：普通外观选项", result.PublicSummary);
        Assert.Contains("预期效果（未验证）：选项开启", result.PublicSummary);
        Assert.DoesNotContain("选择新目标", result.PublicSummary);
        Assert.Empty(h.Input.Actions); Assert.Equal(1, controls.Resolutions);
    }

    [Fact]
    public async Task HybridEvidenceDoesNotAuthorizeLegacyCoordinatesOrRewriteOriginalProbe()
    {
        var profile = HybridProfile();
        Assert.Null(profile.ProbeFingerprint);
        Assert.False(DesktopTaskCoordinator.IsProfileVerified(profile));
        Assert.True(DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: true));
        using var legacy = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))), profile: profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => legacy.Start());
        Assert.Empty(legacy.Provider.Requests); Assert.Empty(legacy.Input.Actions); Assert.False(legacy.Gate.Status.IsOpen);
        using var assisted = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))), controls: new Controls(), controlsRequired: true, profile: profile);
        await assisted.Start(); await assisted.Done();
        Assert.Single(assisted.Provider.Requests); Assert.Equal(TaskState.Succeeded, assisted.Coordinator.Progress!.Task.State);
        Assert.Null(assisted.Profile.ProbeFingerprint);
    }

    [Theory]
    [InlineData("hybrid_vision")]
    [InlineData("hybrid_grounding")]
    [InlineData("hybrid_schema")]
    public async Task MissingHybridCapabilityPreventsStart(string missing)
    {
        var profile = HybridProfile();
        profile = profile with { Capabilities = profile.Capabilities.Where(c => c.Name != missing).ToImmutableArray() };
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))), controls: new Controls(), controlsRequired: true, profile: profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Start());
        Assert.Empty(h.Provider.Requests); Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("fingerprint")]
    [InlineData("date")]
    [InlineData("profile_changed")]
    public void HybridEvidenceMustBePassedDatedAndBoundToCurrentConfiguration(string invalid)
    {
        var profile = HybridProfile();
        var evidence = profile.Capabilities[0];
        profile = invalid switch
        {
            "status" => profile with { Capabilities = profile.Capabilities.SetItem(0, evidence with { Status = CapabilityStatus.Documented, Source = "ProbePassed", Notes = "passed" }) },
            "fingerprint" => profile with { Capabilities = profile.Capabilities.SetItem(0, evidence with { ProfileFingerprint = "different" }) },
            "date" => profile with { Capabilities = profile.Capabilities.SetItem(0, evidence with { TestedAt = null }) },
            _ => profile with { RequestTimeoutMs = profile.RequestTimeoutMs + 1000 }
        };
        Assert.False(DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: true));
    }

    [Fact]
    public void CompleteLegacyEvidenceStillQualifiesForEitherMode()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))));
        Assert.True(DesktopTaskCoordinator.IsProfileVerified(h.Profile));
        Assert.True(DesktopTaskCoordinator.IsProfileVerified(h.Profile, controlsRequired: true));
        Assert.False(DesktopTaskCoordinator.IsProfileVerified(h.Profile with { ProbeFingerprint = null }));
        Assert.False(DesktopTaskCoordinator.IsProfileVerified(h.Profile with { ProbeFingerprint = null }, controlsRequired: true));
    }

    [Theory]
    [InlineData(ControlSnapshotStatus.Unavailable)]
    [InlineData(ControlSnapshotStatus.TimedOut)]
    [InlineData(ControlSnapshotStatus.Disabled)]
    [InlineData(ControlSnapshotStatus.Available)]
    [InlineData(ControlSnapshotStatus.Partial)]
    public async Task AssistedModeWithoutCandidatesStopsBeforeChargingOrInput(ControlSnapshotStatus status)
    {
        var controls = new Controls { Status = status, Candidates = [] };
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Empty(h.Provider.Requests); Assert.Empty(h.Input.Actions);
        Assert.Equal(status == ControlSnapshotStatus.Partial ? 2 : 1, controls.Observations);
        Assert.Equal(0, h.Coordinator.Progress!.Task.Usage.ApiAttempts);
        Assert.Equal("ASSISTED_CONTROLS_REQUIRED", h.Coordinator.Progress.Task.LastError!.Code);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress.Task.State); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task AssistedModeWithoutObserverNeverCallsModel()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))), controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Empty(h.Provider.Requests); Assert.Empty(h.Input.Actions);
        Assert.Equal("ASSISTED_CONTROLS_REQUIRED", h.Coordinator.Progress!.Task.LastError!.Code);
    }

    [Theory]
    [InlineData("{\"type\":\"move\",\"x\":500,\"y\":500}")]
    [InlineData("{\"type\":\"click\",\"x\":500,\"y\":500,\"button\":\"left\",\"clickCount\":1}")]
    [InlineData("{\"type\":\"scroll\",\"x\":500,\"y\":500,\"delta\":-1}")]
    [InlineData("{\"type\":\"drag\",\"fromX\":400,\"fromY\":400,\"toX\":600,\"toY\":600,\"durationMs\":500}")]
    public async Task AssistedModeRejectsEveryModelCoordinateAction(string json)
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r,
            new { kind = "act", action = JsonSerializer.Deserialize<JsonElement>(json), target = "普通外观选项", expected = "选项改变" })), controls: new Controls(), controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Single(h.Provider.Requests); Assert.Empty(h.Input.Actions);
        Assert.Equal("ASSISTED_COORDINATES_DISABLED", h.Coordinator.Progress!.Task.LastError!.Code);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress.Task.State);
    }

    [Theory]
    [InlineData(ControlSnapshotStatus.Available)]
    [InlineData(ControlSnapshotStatus.Partial)]
    public async Task AssistedControlResolutionStillProducesPhysicalClickAndFreshVerification(ControlSnapshotStatus status)
    {
        var controls = new Controls { Status = status };
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ?
            new { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "c1", button = "left", clickCount = 1, target = "普通外观选项", expected = "选项改变" } : Finish(r))), controls: controls, controlsRequired: true, profile: HybridProfile());
        await h.Start(); await h.Done();
        Assert.Equal(1, controls.Resolutions);
        var action = Assert.Single(h.Input.Actions);
        var click = Assert.IsType<ClickAction>(action.Action);
        Assert.Equal(new PhysicalPoint(500, 400), InputCoordinates.ToPhysical(click.Point, Desktop.Bounds));
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(2, requests.Length); Assert.NotEqual(requests[0].CurrentFrame.Id, requests[1].CurrentFrame.Id);
        Assert.Equal(DesktopProtocolPrompt.V2Assisted, requests[0].ProtocolPrompt);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssistedUnknownOrDisabledControlStopsWithoutResolution(bool disabled)
    {
        var controls = new Controls();
        if (disabled) controls.Candidates = [controls.Candidates[0] with { Enabled = false }];
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r,
            new { kind = "act_control", snapshotId = r.Controls!.Id, controlId = disabled ? "c1" : "unknown", button = "left", clickCount = 1, target = "普通外观选项", expected = "选项改变" })), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Single(h.Provider.Requests); Assert.Empty(h.Input.Actions); Assert.Equal(0, controls.Resolutions);
        Assert.Equal("ASSISTED_CONTROL_UNAVAILABLE", h.Coordinator.Progress!.Task.LastError!.Code);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    public async Task AssistedKeyboardRequiresEnabledFocusedAndFocusable(bool text, bool enabled, bool focused, bool focusable)
    {
        var controls = new Controls();
        controls.Candidates = [controls.Candidates[0] with { Enabled = enabled, Focused = focused, Focusable = focusable, Role = focusable ? "Button" : "Window" }];
        object action = text ? new { type = "text", text = "深色" } : new { type = "hotkey", keys = new[] { "CTRL", "A" } };
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r,
            new { kind = "act", action, target = "普通文本框", expected = "文字改变" })), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Single(h.Provider.Requests); Assert.Empty(h.Input.Actions);
        Assert.Equal("ASSISTED_FOCUS_REQUIRED", h.Coordinator.Progress!.Task.LastError!.Code);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AssistedKeyboardRechecksFocusAfterModelReturns(bool focusRemains, bool focusableRemains)
    {
        var controls = new Controls();
        var focused = controls.Candidates[0] with { Focused = true };
        controls.CandidatesAt = n => [focused with { Focused = n == 1 || focusRemains, Focusable = n == 1 || focusableRemains }];
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ?
            new { kind = "act", action = new { type = "text", text = "深色" }, target = "普通文本框", expected = "文字改变" } : Finish(r))), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.True(controls.Observations >= 2);
        if (focusRemains && focusableRemains) { Assert.Single(h.Input.Actions); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State); }
        else { Assert.Empty(h.Input.Actions); Assert.Single(h.Provider.Requests); Assert.Equal("ASSISTED_FOCUS_CHANGED", h.Coordinator.Progress!.Task.LastError!.Code); }
    }

    [Theory]
    [InlineData("WIN")]
    [InlineData("WIN,I")]
    [InlineData("WIN,E")]
    [InlineData("ALT,TAB")]
    public async Task AssistedFiniteGlobalNavigationDoesNotRequireFocusedControl(string keys)
    {
        var controls = new Controls();
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ?
            new { kind = "act", action = new { type = "hotkey", keys = keys.Split(',') }, target = "打开普通应用", expected = "目标窗口显示" } : Finish(r))), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Single(h.Input.Actions); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.Equal(2, controls.Observations);
    }

    [Fact]
    public async Task AssistedGlobalNavigationStillPassesExistingSafetyPolicy()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r,
            new { kind = "act", action = new { type = "hotkey", keys = new[] { "WIN", "I" } }, target = "关闭防火墙", expected = "安全状态改变" })), controls: new Controls(), controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Empty(h.Input.Actions); Assert.Equal("HIGH_IMPACT_MANUAL", h.Coordinator.Progress!.Task.LastError!.Code);
    }

    [Theory]
    [InlineData(ControlSnapshotStatus.Available)]
    [InlineData(ControlSnapshotStatus.Unavailable)]
    [InlineData(ControlSnapshotStatus.Disabled)]
    [InlineData(ControlSnapshotStatus.TimedOut)]
    public async Task AssistedControlInformationLostAfterClickNeverPermitsFurtherInput(ControlSnapshotStatus status)
    {
        var controls = new Controls();
        var original = controls.Candidates;
        controls.CandidatesAt = n => n == 1 ? original : [];
        controls.StatusAt = n => n == 1 ? ControlSnapshotStatus.Available : status;
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r,
            new { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "c1", button = "left", clickCount = 1, target = "普通外观选项", expected = "选项改变" })), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        bool verifyOnly = status is ControlSnapshotStatus.Unavailable or ControlSnapshotStatus.TimedOut;
        Assert.Single(h.Input.Actions); Assert.Equal(verifyOnly ? 2 : 1, h.Provider.Requests.Count);
        Assert.Equal(status is ControlSnapshotStatus.Unavailable or ControlSnapshotStatus.TimedOut ? 3 : 2, controls.Observations);
        Assert.Equal(verifyOnly ? "RESULT_VERIFICATION_ONLY" : "ASSISTED_CONTROLS_REQUIRED", h.Coordinator.Progress!.Task.LastError!.Code);
        Assert.False(h.Gate.Status.IsOpen); Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
    }

    [Theory]
    [InlineData(ControlSnapshotStatus.Partial)]
    [InlineData(ControlSnapshotStatus.Unavailable)]
    [InlineData(ControlSnapshotStatus.TimedOut)]
    public async Task FirstTransientEmptyAfterInjectedInputGetsOneFreshObservationBeforeModel(ControlSnapshotStatus status)
    {
        var desktop = new Desktop();
        var controls = new Controls();
        var original = controls.Candidates;
        controls.CandidatesAt = n => n == 2 ? [] : original;
        controls.StatusAt = n => n == 2 ? status : ControlSnapshotStatus.Available;
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ? ControlClick(r) : Finish(r))),
            desktop: desktop, controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Equal(3, desktop.Captures); Assert.Equal(3, controls.Observations);
        Assert.Single(h.Input.Actions);
        var requests = h.Provider.Requests.ToArray(); Assert.Equal(2, requests.Length);
        Assert.Equal("frame-1", requests[0].CurrentFrame.Id); Assert.Equal("frame-3", requests[1].CurrentFrame.Id);
        Assert.Equal(FrameViewKind.Overview, requests[1].CurrentFrame.ViewKind); Assert.Null(requests[1].OverviewContext);
        Assert.Equal(requests[1].CurrentFrame.Id, requests[1].Controls!.FrameId);
        Assert.Equal(2, h.Coordinator.Progress!.Task.Usage.ApiAttempts);
        Assert.Equal(1, h.Coordinator.Progress.Task.Usage.ActionsAttempted);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdPartialControlsRefreshImageBeforeSpendingAModelRequest(bool staysPartial)
    {
        var desktop = new Desktop();
        var controls = new Controls { StatusAt = n => n == 1 || staysPartial ? ControlSnapshotStatus.Partial : ControlSnapshotStatus.Available };
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))),
            desktop: desktop, controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Equal(2, desktop.Captures); Assert.Equal(2, controls.Observations);
        var request = Assert.Single(h.Provider.Requests);
        Assert.Equal("frame-2", request.CurrentFrame.Id);
        Assert.Equal(request.CurrentFrame.Id, request.Controls!.FrameId);
        Assert.Empty(h.Input.Actions);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Fact]
    public async Task PersistentPartialEmptyOnlyAllowsOneFinalResultCheckWithoutFurtherInput()
    {
        var desktop = new Desktop(); var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n == 1 ? original : [];
        controls.StatusAt = n => n == 1 ? ControlSnapshotStatus.Available : ControlSnapshotStatus.Partial;
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, ControlClick(r))),
            desktop: desktop, controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Equal(3, desktop.Captures); Assert.Equal(3, controls.Observations);
        Assert.Single(h.Input.Actions); Assert.Equal(2, h.Provider.Requests.Count);
        Assert.Equal("RESULT_VERIFICATION_ONLY", h.Coordinator.Progress!.Task.LastError!.Code);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress.Task.State);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Theory]
    [InlineData(ActionStatus.Rejected, 1, 2)]
    [InlineData(ActionStatus.Injected, 0, 2)]
    [InlineData(ActionStatus.Uncertain, 1, 1)]
    [InlineData(ActionStatus.Cancelled, 1, 1)]
    public async Task PartialObservationRetryRequiresActualInjectedEvents(ActionStatus status, int appliedEvents, int observations)
    {
        var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n == 1 ? original : [];
        controls.StatusAt = n => n == 1 ? ControlSnapshotStatus.Available : ControlSnapshotStatus.Partial;
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, ControlClick(r))), controls: controls, controlsRequired: true);
        h.Input.Status = status; h.Input.AppliedEvents = appliedEvents;
        await h.Start(); await h.Done();
        Assert.Equal(observations, controls.Observations); Assert.Single(h.Input.Actions); Assert.Single(h.Provider.Requests);
        Assert.True(h.Coordinator.Progress!.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulFirstPostInputObservationConsumesRetryBeforeLaterReadOnlyDecision(bool inspect)
    {
        var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n < 3 ? original : [];
        controls.StatusAt = n => n < 3 ? ControlSnapshotStatus.Available : ControlSnapshotStatus.Partial;
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ? ControlClick(r) : inspect ?
            new { kind = "inspect", rect = new { x0 = 400, y0 = 300, x1 = 700, y1 = 700 } } :
            new { kind = "wait", milliseconds = 100, reason = "观察界面" })), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Equal(3, controls.Observations); Assert.Equal(2, h.Provider.Requests.Count); Assert.Single(h.Input.Actions);
        Assert.Equal("ASSISTED_CONTROLS_REQUIRED", h.Coordinator.Progress!.Task.LastError!.Code);
    }

    [Theory]
    [InlineData("hwnd")]
    [InlineData("pid")]
    [InlineData("bounds")]
    [InlineData("generation")]
    [InlineData("monitor")]
    public async Task ChangedSurfaceCanRefreshOnceButMissingControlsNeverAuthorizeInput(string changed)
    {
        var desktop = new Desktop(); var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n == 1 ? original : [];
        controls.StatusAt = n => n == 1 ? ControlSnapshotStatus.Available : ControlSnapshotStatus.Partial;
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, ControlClick(r))), desktop: desktop, controls: controls, controlsRequired: true);
        h.Input.AfterExecute = () =>
        {
            switch (changed)
            {
                case "hwnd": desktop.CurrentForeground = Desktop.Foreground with { HwndHex = "2" }; break;
                case "pid": desktop.CurrentForeground = Desktop.Foreground with { ProcessId = 101 }; break;
                case "bounds": desktop.CurrentForeground = Desktop.Foreground with { WindowRect = new(1, 0, 999, 800) }; break;
                case "generation": desktop.DisplayGeneration = 2; break;
                case "monitor": desktop.CapturedMonitorOverride = "other-display"; break;
            }
        };
        await h.Start(); await h.Done();
        int expectedReads = changed is "generation" or "monitor" ? 2 : 3;
        Assert.Equal(expectedReads, desktop.Captures); Assert.Equal(expectedReads, controls.Observations);
        Assert.Single(h.Provider.Requests); Assert.Single(h.Input.Actions);
        Assert.Equal("ASSISTED_CONTROLS_REQUIRED", h.Coordinator.Progress!.Task.LastError!.Code);
    }

    [Fact]
    public async Task NewlyOpenedApplicationGetsFreshControlsBeforeNextModelDecision()
    {
        var desktop = new Desktop(); var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n == 2 ? [] : original;
        controls.StatusAt = n => n == 2 ? ControlSnapshotStatus.Unavailable : ControlSnapshotStatus.Available;
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ? ControlClick(r) : Finish(r))),
            desktop: desktop, controls: controls, controlsRequired: true);
        h.Input.AfterExecute = () => desktop.CurrentForeground = Desktop.Foreground with { HwndHex = "2", ProcessId = 101 };
        await h.Start(); await h.Done();
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(2, requests.Length); Assert.Equal(3, desktop.Captures);
        Assert.Equal("2", requests[1].CurrentFrame.Foreground.HwndHex);
        Assert.Equal(101, requests[1].CurrentFrame.Foreground.ProcessId);
        Assert.Equal(requests[1].CurrentFrame.Id, requests[1].Controls!.FrameId);
        Assert.Single(h.Input.Actions); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrStopDuringPartialObservationDelayNeverReplaysOrCallsModel(bool stop)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desktop = new Desktop(); var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n == 2 ? [] : original;
        controls.StatusAt = n => n == 2 ? ControlSnapshotStatus.Partial : ControlSnapshotStatus.Available;
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, ControlClick(r))), desktop: desktop, controls: controls, controlsRequired: true);
        h.Coordinator.Changed += p => { if (p.Current == "界面可能正在更新，稍候重新观察控件") waiting.TrySetResult(); };
        var task = await h.Start();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (stop) await h.Coordinator.StopAsync(task.Id); else await h.Coordinator.PauseAsync(task.Id);
        await h.Done();
        Assert.Equal(2, desktop.Captures); Assert.Equal(2, controls.Observations);
        Assert.Single(h.Provider.Requests); Assert.Single(h.Input.Actions);
        Assert.True(h.Coordinator.Progress!.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LatePartialRecoveryCaptureOrControlsAfterCancellationCannotAdvance(bool controlsLate, bool stop)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desktop = new Desktop(); var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n == 2 ? [] : original;
        controls.StatusAt = n => n == 2 ? ControlSnapshotStatus.Partial : ControlSnapshotStatus.Available;
        async Task IgnoreCancellationAtThirdRead(int n, CancellationToken _)
        { if (n == 3) { entered.TrySetResult(); await release.Task; } }
        if (controlsLate) controls.BeforeObserve = IgnoreCancellationAtThirdRead; else desktop.BeforeCapture = IgnoreCancellationAtThirdRead;
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, ControlClick(r))), desktop: desktop, controls: controls, controlsRequired: true);
        try
        {
            var task = await h.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (stop) await h.Coordinator.StopAsync(task.Id); else await h.Coordinator.PauseAsync(task.Id);
            Assert.False(h.Gate.Status.IsOpen);
            release.TrySetResult(); await h.Done();
            Assert.Single(h.Provider.Requests); Assert.Single(h.Input.Actions);
            Assert.Equal(controlsLate ? 3 : 2, controls.Observations);
            Assert.True(h.Coordinator.Progress!.Task.CleanupComplete);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ActiveBudgetIncludesPartialObservationDelay()
    {
        var desktop = new Desktop(); var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n == 2 ? [] : original;
        controls.StatusAt = n => n == 2 ? ControlSnapshotStatus.Partial : ControlSnapshotStatus.Available;
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, ControlClick(r))), desktop: desktop, controls: controls, controlsRequired: true);
        await h.Start(new(10, 10, 150)); await h.Done();
        Assert.Single(h.Provider.Requests); Assert.Single(h.Input.Actions); Assert.Equal(2, desktop.Captures);
        Assert.Equal(TaskState.Interrupted, h.Coordinator.Progress!.Task.State);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    private static object ControlClick(ModelRequest r) => new
    { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "c1", button = "left", clickCount = 1, target = "普通外观选项", expected = "选项改变" };

    [Theory]
    [InlineData("finish")]
    [InlineData("wait")]
    [InlineData("hotkey")]
    [InlineData("invalid")]
    public async Task MissingControlsAfterRealInputHasExactlyOneBoundedResultReply(string decision)
    {
        var controls = new Controls(); var original = controls.Candidates;
        controls.CandidatesAt = n => n == 1 ? original : [];
        controls.StatusAt = n => n == 1 ? ControlSnapshotStatus.Available : ControlSnapshotStatus.TimedOut;
        using var h = new Harness((r, n, _) => Task.FromResult(n == 1 ? Reply(r, ControlClick(r)) : decision == "invalid" ?
            new ProviderReply("not json", new(null, null)) : Reply(r, decision switch
            {
                "finish" => Finish(r),
                "wait" => new { kind = "wait", milliseconds = 100, reason = "重试" },
                _ => new { kind = "act", action = new { type = "hotkey", keys = new[] { "ENTER" } }, target = "输入", expected = "变化" }
            })), controls: controls, controlsRequired: true);
        await h.Start(); await h.Done();
        Assert.Equal(3, controls.Observations); Assert.Equal(2, h.Provider.Requests.Count); Assert.Single(h.Input.Actions);
        var last = h.Provider.Requests.Last();
        Assert.Contains("RESULT VERIFICATION ONLY", last.ProtocolPrompt);
        Assert.Equal("frame-3", last.CurrentFrame.Id);
        Assert.Equal(decision == "finish" ? TaskState.Succeeded : TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task AssistedModeKeepsInspectWaitAndCurrentFrameFinishWithoutInput()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n switch
        {
            1 => new { kind = "inspect", rect = new { x0 = 400, y0 = 300, x1 = 700, y1 = 700 } },
            2 => new { kind = "wait", milliseconds = 100, reason = "核对控件状态" },
            _ => Finish(r)
        })), controls: new Controls(), controlsRequired: true);
        await h.Start(); await h.Done();
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(3, requests.Length); Assert.Equal(FrameViewKind.Crop, requests[1].CurrentFrame.ViewKind);
        Assert.NotEqual(requests[0].CurrentFrame.Id, requests[2].CurrentFrame.Id);
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Fact]
    public async Task DragEndpointOccludedByHudRelocatesAwayFromWholePathBeforeInput()
    {
        var originalHud = new PhysicalRect(800, 0, 200, 200);
        var movedHud = new PhysicalRect(0, 0, 200, 200);
        var desktop = new Desktop { OwnWindowRects = [originalHud] };
        var overlay = new Overlay(excluded =>
        {
            if (!FrameChecks.Overlaps(movedHud, excluded)) desktop.OwnWindowRects = [movedHud];
        });
        var drag = new { kind = "act", action = new { type = "drag", fromX = 500, fromY = 500, toX = 900, toY = 100, durationMs = 400 }, target = "普通滑块", expected = "位置改变" };
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n <= 2 ? drag : Finish(r))), desktop: desktop, overlay: overlay);
        await h.Start(); await h.Done();
        var excluded = Assert.Single(overlay.Exclusions);
        Assert.True(FrameChecks.Contains(excluded, InputCoordinates.ToPhysical(new(500, 500), Desktop.Bounds)));
        Assert.True(FrameChecks.Contains(excluded, InputCoordinates.ToPhysical(new(900, 100), Desktop.Bounds)));
        Assert.True(FrameChecks.Contains(excluded, InputCoordinates.ToPhysical(new(700, 300), Desktop.Bounds)));
        var action = Assert.Single(h.Input.Actions);
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(originalHud, Assert.Single(requests[0].CurrentFrame.OwnWindowRects));
        Assert.Equal(movedHud, Assert.Single(requests[1].CurrentFrame.OwnWindowRects));
        Assert.NotEqual(requests[0].CurrentFrame.Id, action.FrameId);
        Assert.Equal(requests[1].CurrentFrame.Id, action.FrameId);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HudWithoutSafeRelocationStopsBeforeConsumingRequestBudget(bool useControls)
    {
        var desktop = new Desktop { OwnWindowRects = [new(400, 250, 200, 300)] };
        var overlay = new Overlay();
        var controls = new Controls();
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, useControls ?
            new { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "c1", button = "left", clickCount = 1, target = "普通外观选项", expected = "选项改变" } : Click)),
            desktop: desktop, overlay: overlay, controls: useControls ? controls : null);
        await h.Start(); await h.Done();
        Assert.Equal(2, overlay.Exclusions.Count);
        Assert.Equal(3, h.Provider.Requests.Count);
        Assert.Empty(h.Input.Actions); Assert.Equal(0, controls.Resolutions);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        Assert.Equal("HUD_OCCLUSION", h.Coordinator.Progress.Task.LastError!.Code);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete); Assert.Null(h.Gate.Status.Lease);
    }

    [Fact]
    public async Task StopDuringCountdownPreventsStartAndAllowsExplicitFreshResume()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))));
        long countdownRevision = h.Gate.Status.Revision;
        h.Gate.Trip(InputStopReason.Stopped);
        var task = await h.Coordinator.StartAsync(new("改为深色", ProviderConfiguration.Fingerprint(h.Profile), "display", TaskBudget.Default, countdownRevision), default);
        Assert.Equal(TaskState.Interrupted, task.State); Assert.True(task.CleanupComplete);
        Assert.Empty(h.Provider.Requests); Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
        long resumeRevision = h.Gate.Status.Revision;
        h.Gate.Trip(InputStopReason.Stopped);
        await h.Coordinator.ResumeWithGateRevisionAsync(task.Id, resumeRevision, default);
        Assert.Empty(h.Provider.Requests); Assert.False(h.Gate.Status.IsOpen);
        await h.Coordinator.ResumeWithGateRevisionAsync(task.Id, h.Gate.Status.Revision, default); await h.Done();
        Assert.Single(h.Provider.Requests); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }
    [Fact]
    public async Task UnverifiedProfileNeverStartsOrCallsModel()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Finish(r))), verified: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Start());
        Assert.Empty(h.Provider.Requests); Assert.False(h.Gate.Status.IsOpen);
    }
    [Fact]
    public async Task InspectReachesNextModelAndEveryInputGetsFreshObservationBeforeFinish()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n switch
        { 1 => new { kind = "inspect", rect = new { x0 = 100, y0 = 100, x1 = 600, y1 = 600 } }, 2 => Click, _ => Finish(r) })));
        await h.Start(); await h.Done();
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(FrameViewKind.Crop, requests[1].CurrentFrame.ViewKind);
        Assert.Equal(new PhysicalRect(100, 80, 500, 400), requests[1].CurrentFrame.PhysicalRegion);
        Assert.Same(requests[0].CurrentFrame, requests[1].OverviewContext);
        Assert.Equal(FrameViewKind.Overview, requests[2].CurrentFrame.ViewKind);
        Assert.NotEqual(requests[1].CurrentFrame.Id, requests[2].CurrentFrame.Id);
        Assert.Single(h.Input.Actions);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
        Assert.Equal(30, h.Coordinator.Progress.Task.Usage.InputTokens);
        Assert.False(h.Gate.Status.IsOpen); Assert.Null(h.Gate.Status.Lease);
    }
    [Fact]
    public async Task PauseClosesInputImmediatelyDiscardsLateReplyAndResumeUsesNewEpoch()
    {
        var entered = new TaskCompletionSource<ModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness((r, n, _) =>
        { if (n == 1) { entered.TrySetResult(r); return late.Task; } return Task.FromResult(Reply(r, Finish(r))); });
        try
        {
            var task = await h.Start(); var first = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await h.Coordinator.PauseAsync(task.Id);
            Assert.False(h.Gate.Status.IsOpen);
            Assert.Equal(TaskState.Pausing, h.Coordinator.Progress!.Task.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Start());
            late.TrySetResult(Reply(first, Click)); await h.Done();
            Assert.Empty(h.Input.Actions);
            Assert.Equal(TaskState.Paused, h.Coordinator.Progress.Task.State);
            await h.Coordinator.ResumeAsync(task.Id, default); await h.Done();
            Assert.True(h.Provider.Requests.Last().Task.Lease.Epoch > first.Task.Lease.Epoch);
            Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State);
            Assert.Null(h.Coordinator.Progress.Task.Usage.InputTokens); // Cancelled first attempt has unknown charge.
        }
        finally { late.TrySetCanceled(); }
    }
    [Fact]
    public async Task RequestBudgetRequiresExplicitAdditionAndDoesNotRetry()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ? new { kind = "wait", milliseconds = 100, reason = "等待界面" } : Finish(r))));
        var task = await h.Start(new(10, 1, 10000)); await h.Done();
        Assert.Equal(TaskState.Interrupted, h.Coordinator.Progress!.Task.State);
        Assert.Single(h.Provider.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ResumeAsync(task.Id, default));
        await h.Coordinator.AddBudgetAsync(task.Id, new(0, 1, 0));
        await h.Coordinator.ResumeAsync(task.Id, default); await h.Done();
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State);
        Assert.Equal(2, h.Coordinator.Progress.Task.Usage.ApiAttempts);
    }
    [Fact]
    public async Task UncertainInputStopsWithoutAnotherModelOrInputAttempt()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Click)));
        h.Input.Status = ActionStatus.Uncertain;
        await h.Start(); await h.Done();
        Assert.Single(h.Input.Actions); Assert.Single(h.Provider.Requests);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        Assert.Contains("不会自动重放", h.Coordinator.Progress.Summary);
    }
    [Fact]
    public async Task DuplicateProposalDoesNotReinjectAfterFreshObservation()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Click, "same-id")));
        await h.Start(); await h.Done();
        Assert.Single(h.Input.Actions); Assert.Equal(2, h.Provider.Requests.Count);
        Assert.Equal("DUPLICATE_PROPOSAL", h.Coordinator.Progress!.Task.LastError!.Code);
        Assert.Single(h.Coordinator.Progress.Task.UntrustedModelObservations);
    }
    [Fact]
    public async Task IndependentDeadlineClosesGateEvenWhenProviderIgnoresCancellation()
    {
        var entered = new TaskCompletionSource<ModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness((r, _, ct) => { entered.TrySetResult(r); ct.Register(() => cancelled.TrySetResult()); return late.Task; });
        try
        {
            await h.Start(new(10, 10, 150));
            var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(h.Gate.Status.IsOpen);
            late.TrySetResult(Reply(request, Click)); await h.Done();
            Assert.Empty(h.Input.Actions);
            Assert.Equal(TaskState.Interrupted, h.Coordinator.Progress!.Task.State);
            Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
        }
        finally { late.TrySetCanceled(); }
    }
    [Fact]
    public async Task PauseDuringInputRequiresReviewOfPossiblyPartialEffect()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r, Click)));
        h.Input.WaitForCancel = true;
        var task = await h.Start();
        await h.Input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await h.Coordinator.PauseAsync(task.Id); await h.Done();
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        Assert.Contains("可能已部分执行", h.Coordinator.Progress.Summary);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
        await h.Coordinator.StopAsync(task.Id);
        Assert.Equal(TaskState.Cancelled, h.Coordinator.Progress.Task.State);
        Assert.Null(h.Gate.Status.Lease);
    }
    [Fact]
    public async Task StoppingWaitingTaskReleasesReservationAndAllowsNewTask()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1 ? new { kind = "ask_user", reason = "需要用户选择", question = "哪个选项？" } : Finish(r))));
        var first = await h.Start(); await h.Done();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Start());
        await h.Coordinator.StopAsync(first.Id);
        var next = await h.Start(); await h.Done();
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Fact]
    public async Task AnswerKeepsGoalAndBudgetAndResumesFromFreshObservation()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1
            ? new { kind = "ask_user", reason = "缺少算式", question = "你要计算什么？" } : Finish(r))));
        var task = await h.Start(); await h.Done();
        var waiting = h.Coordinator.Progress!.Task;
        Assert.False(h.Gate.Status.IsOpen); Assert.Null(h.Gate.Status.Lease); Assert.Empty(h.Input.Actions);
        Assert.True(waiting.CleanupComplete);
        var question = Assert.IsType<UserQuestion>(waiting.PendingQuestion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ResumeAsync(task.Id, default));
        await h.Coordinator.AnswerAsync(waiting.Lease, question.Id, "  3+2+5  ");
        Assert.Single(h.Provider.Requests); Assert.False(h.Gate.Status.IsOpen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.AnswerAsync(waiting.Lease, question.Id, "重复回复"));
        await h.Coordinator.ResumeAsync(task.Id, default); await h.Done();
        var requests = h.Provider.Requests.ToArray();
        Assert.Equal(task.Goal, requests[1].Task.Goal);
        Assert.Equal(task.Budget, requests[1].Task.Budget);
        Assert.Equal(new UserClarification(question.Question, "3+2+5"), Assert.Single(requests[1].Task.Clarifications));
        Assert.NotEqual(requests[0].CurrentFrame.Id, requests[1].CurrentFrame.Id);
        Assert.True(requests[1].Task.Lease.Epoch > requests[0].Task.Lease.Epoch);
        Assert.Equal(2, h.Coordinator.Progress!.Task.Usage.ApiAttempts);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State);
    }

    [Fact]
    public async Task StaleInvalidAndStoppedQuestionAnswersNeverResumeInput()
    {
        using var h = new Harness((r, _, _) => Task.FromResult(Reply(r,
            new { kind = "ask_user", reason = "缺少信息", question = "哪个选项？" })));
        await h.Start(); await h.Done();
        var task = h.Coordinator.Progress!.Task; var question = task.PendingQuestion!;
        await Assert.ThrowsAsync<ArgumentException>(() => h.Coordinator.AnswerAsync(task.Lease, question.Id, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Coordinator.AnswerAsync(task.Lease, question.Id, new string('a', 1001)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.AnswerAsync(task.Lease, Guid.NewGuid(), "深色"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.AnswerAsync(new(task.Id, task.Epoch + 1), question.Id, "深色"));
        await h.Coordinator.StopAsync(task.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.AnswerAsync(task.Lease, question.Id, "深色"));
        Assert.Null(h.Coordinator.Progress!.Task.PendingQuestion);
        Assert.False(h.Gate.Status.IsOpen); Assert.Empty(h.Input.Actions); Assert.Single(h.Provider.Requests);
    }

    [Fact]
    public async Task QuestionAnswerCannotBypassHighImpactActionPolicy()
    {
        using var h = new Harness((r, n, _) => Task.FromResult(Reply(r, n == 1
            ? new { kind = "ask_user", reason = "缺少信息", question = "哪个选项？" } : Click)));
        await h.Start(); await h.Done();
        var task = h.Coordinator.Progress!.Task;
        await h.Coordinator.AnswerAsync(task.Lease, task.PendingQuestion!.Id, "删除所有文件");
        await h.Coordinator.ResumeAsync(task.Id, default); await h.Done();
        Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        Assert.Contains("HIGH_IMPACT", h.Coordinator.Progress.Task.LastError!.Code);
    }
}
