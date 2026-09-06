using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

// Offline protocol/state tests. No real model, screenshot, UI Automation or input is used.
public sealed class TaskPlanCoordinatorTests
{
    private const string Interpretation = """{"goal":"定位普通外观选项，切换后核对","reply":"我会先定位选项，再切换并核对结果。","steps":[{"id":"s1","title":"定位选项","completionCheck":"当前窗口显示目标选项"},{"id":"s2","title":"切换并核对","completionCheck":"目标选项显示请求状态"}],"completionCheck":"完整任务所需的选项状态已观察并核对"}""";
    private sealed class Desktop : IDesktopObserver
    {
        private int _sequence;
        private static readonly PhysicalRect Bounds = new(0, 0, 1000, 800);
        private static readonly ForegroundIdentity Foreground = new("1", 100, "Fixture", Bounds);
        public Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct) => Task.FromResult(new DesktopEnvironment(1,
            [new("display", Bounds, Bounds, 120, 120, true)], Foreground, DesktopSessionState.Available));
        public Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? region, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); int sequence = Interlocked.Increment(ref _sequence);
            return Task.FromResult(new Frame("frame-" + sequence, lease, DateTimeOffset.UtcNow, 1, monitorId, Bounds,
                new(1000, 800, "image/png", [1, 2, (byte)sequence]), FrameViewKind.Overview, Foreground, []));
        }
    }
    private sealed class Controls(Func<int, ControlResolution>? resolve = null) : IControlObserver
    {
        private int _resolutions;
        public Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct) => Task.FromResult(new ControlSnapshot(Guid.NewGuid().ToString("N"),
            frame.Lease, frame.Id, DateTimeOffset.UtcNow, ControlSnapshotStatus.Available,
            [new("option", null, "普通外观选项", "Button", new(480, 380, 40, 40), true, false, true, null, null)]));
        public Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string id, CancellationToken ct) =>
            Task.FromResult(resolve?.Invoke(Interlocked.Increment(ref _resolutions)) ?? new ControlResolution(new(500, 400), null));
    }
    private sealed class Provider(Func<ModelRequest, int, ProviderReply> decide, bool structured) : IModelProvider, ITaskIntentProvider
    {
        internal readonly ConcurrentQueue<ModelRequest> Decisions = new(), Intents = new();
        private int _calls;
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Decisions.Enqueue(request); return Task.FromResult(decide(request, Interlocked.Increment(ref _calls))); }
        public Task<ProviderReply> InterpretAsync(ModelRequest request, CancellationToken ct)
        { Intents.Enqueue(request); return Task.FromResult(new ProviderReply(structured ? Interpretation : """{"goal":"切换普通外观选项并核对","reply":"我会切换后核对。"}""", new(10, 10))); }
        public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Input : IInputExecutor
    {
        internal readonly ConcurrentQueue<ValidatedAction> Actions = new();
        public Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Actions.Enqueue(action);
            return Task.FromResult(new ActionResult(action.ProposalId, lease.TaskId, lease.Epoch, ActionStatus.Injected, null,
                DateTimeOffset.UtcNow, 1, 1, 1, "OFFLINE TEST ONLY", null));
        }
    }
    private sealed class Harness : IDisposable
    {
        internal readonly InputSafetyGate Gate = new(); internal readonly Input Input = new();
        internal readonly Provider Provider; internal readonly DesktopTaskCoordinator Coordinator; private readonly ProviderProfile _profile;
        internal Harness(Func<ModelRequest, int, ProviderReply> decide, bool structured = true, Func<int, ControlResolution>? resolve = null)
        {
            Gate.SetHotkeysReady(true); _profile = ProviderConfiguration.DefaultDeepSeek(); string fingerprint = ProviderConfiguration.Fingerprint(_profile);
            _profile = _profile with { ProbeFingerprint = fingerprint, Capabilities = new[] { "vision", "grounding", "schema" }.Select(name =>
                new CapabilityRecord(name, CapabilityStatus.ProbePassed, null, DateTimeOffset.UtcNow, fingerprint, "OFFLINE TEST ONLY")).ToImmutableArray() };
            Provider = new(decide, structured);
            Coordinator = new(Gate, new Desktop(), new Controls(resolve), Provider, _profile, new DesktopPolicyValidator(TimeSpan.FromSeconds(90)), _ => Input, controlsRequired: true);
        }
        internal async Task Run()
        {
            await Coordinator.StartAsync(new("定位普通外观选项，切换后核对", ProviderConfiguration.Fingerprint(_profile), "display", new(10, 10, 30000)), default);
            await Coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public void Dispose() => Gate.Trip(InputStopReason.Shutdown);
    }
    private static object Click(ModelRequest request) => new { kind = "act_control", snapshotId = request.Controls!.Id, controlId = "option", button = "left", clickCount = 1,
        target = "普通外观选项", expected = "选项状态改变" };
    private static object Finish(ModelRequest request, string outcome = "succeeded") => new { kind = "finish", outcome, summary = "测试中的目标核对", evidence = new[]
        { new { frameId = request.CurrentFrame.Id, appName = "Fixture", observedText = "测试选项状态", interpretation = "仅离线测试证据" } } };
    private static object[] Updates(string secondStatus) =>
        [new { stepId = "s1", status = "completed", observation = "当前窗口显示普通外观选项" },
         new { stepId = "s2", status = secondStatus, observation = secondStatus == "completed" ? "新画面显示目标状态" : "目标状态尚未核对" }];
    private static ProviderReply Reply(ModelRequest request, object decision, object[]? updates = null)
    {
        var envelope = new Dictionary<string, object>
        {
            ["schemaVersion"] = 2, ["proposalId"] = Guid.NewGuid().ToString("N"), ["taskId"] = request.Task.Lease.TaskId,
            ["epoch"] = request.Task.Lease.Epoch, ["frameId"] = request.CurrentFrame.Id, ["current"] = "基于当前测试画面的反馈", ["next"] = "继续未完成步骤", ["decision"] = decision
        };
        if (updates is not null) envelope["planUpdates"] = updates;
        return new(JsonSerializer.Serialize(envelope), new(1, 1));
    }

    [Fact]
    public async Task PrematureFinishContinuesAndWholeGoalRequiresAnAdditionalFreshFinalCheck()
    {
        using var h = new Harness((request, count) => count switch
        {
            1 => Reply(request, Click(request), Updates("active")),
            2 => Reply(request, Finish(request), Updates("active")), // Navigation/one action is not the full goal.
            _ => Reply(request, Finish(request), Updates("completed"))
        });
        await h.Run();
        Assert.Single(h.Provider.Intents); Assert.Single(h.Input.Actions);
        var requests = h.Provider.Decisions.ToArray(); Assert.Equal(4, requests.Length);
        Assert.Equal(2, requests[0].Task.Interpretation!.Steps.Length);
        Assert.Equal("active", requests[1].Task.PlanProgress.Single(r => r.StepId == "s2").Status);
        Assert.Contains(requests[2].RecentResults, r => r.Status == ActionStatus.Rejected);
        Assert.DoesNotContain("\nFINAL_GOAL_CHECK:", requests[2].ProtocolPrompt);
        Assert.Contains("\nFINAL_GOAL_CHECK:", requests[3].ProtocolPrompt);
        Assert.NotEqual(requests[2].CurrentFrame.Id, requests[3].CurrentFrame.Id);
        Assert.All(requests, r => Assert.NotEqual(h.Provider.Intents.Single().CurrentFrame.Id, r.CurrentFrame.Id));
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
        Assert.True(TaskPlanTracking.AllCompleted(h.Coordinator.Progress.Task.Interpretation, h.Coordinator.Progress.Task.PlanProgress));
        Assert.Equal(5, h.Coordinator.Progress.Task.Usage.ApiAttempts);
    }

    [Fact]
    public async Task AFinalCheckCanCorrectProgressContinueOrdinaryWorkAndMustThenVerifyAgain()
    {
        using var h = new Harness((request, count) => count == 2
            ? Reply(request, Click(request), Updates("active")) // Final look discovers unfinished ordinary work.
            : Reply(request, Finish(request), Updates("completed")));
        await h.Run();
        Assert.Single(h.Input.Actions); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        var requests = h.Provider.Decisions.ToArray(); Assert.Equal(4, requests.Length);
        Assert.Contains("\nFINAL_GOAL_CHECK:", requests[1].ProtocolPrompt);
        Assert.DoesNotContain("\nFINAL_GOAL_CHECK:", requests[2].ProtocolPrompt);
        Assert.Equal("active", requests[2].Task.PlanProgress.Single(r => r.StepId == "s2").Status);
        Assert.Contains("\nFINAL_GOAL_CHECK:", requests[3].ProtocolPrompt);
        Assert.NotEqual(requests[2].CurrentFrame.Id, requests[3].CurrentFrame.Id);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task AReopenedStepInTheFinalFinishReplyInvalidatesTheEarlierFinalAudit()
    {
        using var h = new Harness((request, count) => Reply(request, Finish(request), Updates(count == 2 ? "active" : "completed")));
        await h.Run();
        var requests = h.Provider.Decisions.ToArray();
        Assert.Equal(4, requests.Length); // The changed completion state needs its own new final observation.
        Assert.Contains("\nFINAL_GOAL_CHECK:", requests[1].ProtocolPrompt);
        Assert.DoesNotContain("\nFINAL_GOAL_CHECK:", requests[2].ProtocolPrompt);
        Assert.Contains("\nFINAL_GOAL_CHECK:", requests[3].ProtocolPrompt);
        Assert.NotEqual(requests[2].CurrentFrame.Id, requests[3].CurrentFrame.Id);
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
    }

    [Fact]
    public async Task UnknownTaskStepReportsCannotCarryAnOtherwiseValidInput()
    {
        using var h = new Harness((request, count) => count == 1
            ? Reply(request, Click(request), [new { stepId = "s8", status = "completed", observation = "不是此任务的步骤" }])
            : Reply(request, Finish(request, "partial")));
        await h.Run(); Assert.Empty(h.Input.Actions);
        Assert.NotEqual(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.DoesNotContain(h.Coordinator.Progress.Task.PlanProgress, r => r.StepId == "s8");
    }

    [Fact]
    public async Task APlannedTaskRecoversAfterTwoFailedNativeResolutionsAndContinues()
    {
        using var h = new Harness((request, count) => Reply(request, count <= 3 ? Click(request) : Finish(request), Updates(count <= 3 ? "active" : "completed")),
            resolve: count => count <= 2 ? new(null, "CONTROL_UNAVAILABLE") : new(new(500, 400), null));
        await h.Run();
        var requests = h.Provider.Decisions.ToArray(); Assert.Equal(5, requests.Length);
        Assert.Equal(1, requests[2].RecoveryLevel); Assert.Contains(requests[2].RecentResults, r => r.Code == "CONTROL_UNAVAILABLE");
        Assert.Single(h.Input.Actions); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.Null(h.Coordinator.Progress.Task.PendingQuestion);
    }

    [Fact]
    public async Task PersistentlyUnresolvablePlannedControlsUseBoundedRecoveryThenKeepTheTaskForUserHelp()
    {
        using var h = new Harness((request, _) => Reply(request, Click(request), Updates("active")), resolve: _ => new(null, "CONTROL_UNAVAILABLE"));
        await h.Run();
        var requests = h.Provider.Decisions.ToArray(); Assert.Equal(6, requests.Length);
        Assert.Equal(1, requests[2].RecoveryLevel); Assert.Equal(2, requests[4].RecoveryLevel);
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        Assert.NotNull(h.Coordinator.Progress.Task.PendingQuestion); Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
        Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task APartialPlannedResultIsReconsideredAndCanContinueToVerifiedSuccess()
    {
        using var h = new Harness((request, count) => count switch
        {
            1 => Reply(request, Finish(request, "partial"), Updates("active")),
            2 => Reply(request, Click(request), Updates("active")),
            _ => Reply(request, Finish(request), Updates("completed"))
        });
        await h.Run();
        var requests = h.Provider.Decisions.ToArray(); Assert.Equal(4, requests.Length);
        Assert.True(requests[1].Reconsidering); Assert.Contains(requests[1].RecentResults, r => r.Code == "PARTIAL_RECONSIDER");
        Assert.Single(h.Input.Actions); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.Contains("\nFINAL_GOAL_CHECK:", requests[^1].ProtocolPrompt);
        Assert.Null(h.Coordinator.Progress.Task.PendingQuestion); Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
    }

    [Fact]
    public async Task RepeatedPartialResultsKeepThePlanAndQuestionInsteadOfTerminatingTheTask()
    {
        using var h = new Harness((request, _) => Reply(request, Finish(request, "partial"), Updates("active")));
        await h.Run();
        var requests = h.Provider.Decisions.ToArray(); Assert.Equal(2, requests.Length); Assert.True(requests[1].Reconsidering);
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State);
        var task = h.Coordinator.Progress.Task; Assert.NotNull(task.PendingQuestion); Assert.NotNull(task.Interpretation);
        Assert.Equal("active", task.PlanProgress.Single(r => r.StepId == "s2").Status);
        Assert.True(task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task LegacyInterpretationWithoutStepsKeepsTheOriginalCompletionFlow()
    {
        using var h = new Harness((request, count) => Reply(request, count == 1 ? Click(request) : Finish(request)), structured: false);
        await h.Run(); Assert.Single(h.Provider.Intents); Assert.Equal(2, h.Provider.Decisions.Count);
        Assert.Single(h.Input.Actions); Assert.Empty(h.Coordinator.Progress!.Task.PlanProgress);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State); Assert.Equal(3, h.Coordinator.Progress.Task.Usage.ApiAttempts);
    }
}
