using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

// State-machine regression only: all model responses, screen frames and input executors are fakes.
public sealed class ProviderResponseRecoveryCoordinatorTests
{
    private sealed class Desktop : IDesktopObserver
    {
        private static readonly PhysicalRect Bounds = new(0, 0, 1000, 800);
        private static readonly ForegroundIdentity Foreground = new("1", 100, "Fixture", Bounds);
        private int _captures;
        internal Func<int, CancellationToken, Task>? BeforeCapture;
        public Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct) => Task.FromResult(new DesktopEnvironment(1,
            [new("display", Bounds, Bounds, 120, 120, true)], Foreground, DesktopSessionState.Available));
        public async Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? region, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); int count = Interlocked.Increment(ref _captures);
            if (BeforeCapture is not null) await BeforeCapture(count, ct); // May deliberately ignore late cancellation in a test.
            return new("frame-" + count, lease, DateTimeOffset.UtcNow, 1, monitorId, Bounds,
                new(1000, 800, "image/png", [1, 2, (byte)count]), FrameViewKind.Overview, Foreground, []);
        }
    }
    private sealed class Controls(bool rejectResolution) : IControlObserver
    {
        internal int Resolutions;
        public Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct) => Task.FromResult(new ControlSnapshot(Guid.NewGuid().ToString("N"),
            frame.Lease, frame.Id, DateTimeOffset.UtcNow, ControlSnapshotStatus.Available,
            [new("option", null, "普通外观选项", "Button", new(480, 380, 40, 40), true, false, true, null, null)]));
        public Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct)
        { Interlocked.Increment(ref Resolutions); return Task.FromResult(rejectResolution ? new ControlResolution(null, "CONTROL_UNAVAILABLE") : new(new(500, 400), null)); }
    }
    private sealed class Provider(Func<ModelRequest, int, CancellationToken, Task<ProviderReply>> decide, bool planned) : IModelProvider, ITaskIntentProvider
    {
        internal readonly ConcurrentQueue<ModelRequest> Requests = new(), Intents = new();
        private int _calls;
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
        { Requests.Enqueue(request); return decide(request, Interlocked.Increment(ref _calls), ct); }
        public Task<ProviderReply> InterpretAsync(ModelRequest request, CancellationToken ct)
        {
            Intents.Enqueue(request);
            return Task.FromResult(new ProviderReply(planned
                ? """{"goal":"核对普通外观选项","reply":"我会核对目标状态。","steps":[{"id":"s1","title":"核对选项","completionCheck":"目标状态已观察"}],"completionCheck":"目标状态已核对"}"""
                : """{"goal":"核对普通外观选项","reply":"我会核对目标状态。"}""", new(10, 10)));
        }
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
        internal readonly InputSafetyGate Gate = new(); internal readonly Desktop Desktop = new(); internal readonly Input Input = new();
        internal readonly Provider Provider; internal readonly Controls Controls; internal readonly DesktopTaskCoordinator Coordinator;
        private readonly ProviderProfile _profile;
        internal Harness(Func<ModelRequest, int, CancellationToken, Task<ProviderReply>> decide, bool planned = false, bool rejectResolution = false)
        {
            Gate.SetHotkeysReady(true); _profile = ProviderConfiguration.DefaultDeepSeek();
            string fingerprint = ProviderConfiguration.Fingerprint(_profile);
            _profile = _profile with { ProbeFingerprint = fingerprint, Capabilities = new[] { "vision", "grounding", "schema" }.Select(n =>
                new CapabilityRecord(n, CapabilityStatus.ProbePassed, null, DateTimeOffset.UtcNow, fingerprint, "OFFLINE TEST ONLY")).ToImmutableArray() };
            Provider = new(decide, planned); Controls = new(rejectResolution);
            Coordinator = new(Gate, Desktop, Controls, Provider, _profile, new DesktopPolicyValidator(TimeSpan.FromSeconds(90)), _ => Input, controlsRequired: true);
        }
        internal Task<TaskContext> Start(int requestLimit = 20) => Coordinator.StartAsync(new("核对普通外观选项", ProviderConfiguration.Fingerprint(_profile), "display", new(10, requestLimit, 30000)), default);
        internal Task Done() => Coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        public void Dispose() => Gate.Trip(InputStopReason.Shutdown);
    }
    private static ProviderCallException Failure(string code, long? input = 7, long? output = 9) => new(code,
        new(200, "string", 0, true, code == "OUTPUT_TOKEN_LIMIT" ? "length" : "stop", new(input, output), code == "EMPTY_REASONING_ONLY", 30, code == "RESPONSE_REFUSAL"));
    private static Task<ProviderReply> Error(string code, long? input = 7, long? output = 9) => Task.FromException<ProviderReply>(Failure(code, input, output));
    private static object Click(ModelRequest r) => new { kind = "act_control", snapshotId = r.Controls!.Id, controlId = "option", button = "left", clickCount = 1,
        target = "普通外观选项", expected = "核对状态" };
    private static object Finish(ModelRequest r) => new { kind = "finish", outcome = "succeeded", summary = "测试状态已核对", evidence = new[]
        { new { frameId = r.CurrentFrame.Id, appName = "Fixture", observedText = "测试状态", interpretation = "仅离线证据" } } };
    private static ProviderReply Reply(ModelRequest r, object decision) => new(JsonSerializer.Serialize(new
    {
        schemaVersion = 2, proposalId = Guid.NewGuid().ToString("N"), taskId = r.Task.Lease.TaskId, epoch = r.Task.Lease.Epoch,
        frameId = r.CurrentFrame.Id, current = "核对当前测试画面", next = "完成测试", decision
    }), new(11, 13));

    [Theory]
    [InlineData("EMPTY_CONTENT")]
    [InlineData("EMPTY_REASONING_ONLY")]
    [InlineData("EMPTY_SEARCH_ANSWER")]
    [InlineData("OUTPUT_TOKEN_LIMIT")]
    public async Task RecoverableResponseGetsFreshFrameActualErrorAndCountedUsage(string code)
    {
        using var h = new Harness((r, count, _) => count == 1 ? Error(code) : Task.FromResult(Reply(r, Finish(r))));
        await h.Start(); await h.Done();
        var requests = h.Provider.Requests.ToArray(); Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].CurrentFrame.Id, requests[1].CurrentFrame.Id);
        Assert.NotEqual(requests[0].Controls!.Id, requests[1].Controls!.Id);
        Assert.Equal(requests[1].CurrentFrame.Id, requests[1].Controls!.FrameId);
        Assert.False(requests[0].Reconsidering); Assert.True(requests[1].Reconsidering); Assert.Equal(0, requests[1].RecoveryLevel);
        Assert.Equal(requests[0].Task.Lease, requests[1].Task.Lease);
        var actualError = Assert.Single(requests[1].RecentResults.Where(r => r.Code == code));
        Assert.Equal(ExecutionFeedback.ModelFailure(code), actualError.PublicSummary);
        Assert.Equal(requests[0].CurrentFrame.Id, actualError.PostFrameId);
        Assert.Equal(ActionStatus.Rejected, actualError.Status); Assert.Equal(0, actualError.AppliedEventCount);
        Assert.Equal(3, requests[1].Task.Usage.ApiAttempts); Assert.Equal(17, requests[1].Task.Usage.InputTokens); Assert.Equal(19, requests[1].Task.Usage.OutputTokens);
        var final = h.Coordinator.Progress!.Task;
        Assert.Equal(TaskState.Succeeded, final.State); Assert.Equal(3, final.Usage.ApiAttempts);
        Assert.Equal(28, final.Usage.InputTokens); Assert.Equal(32, final.Usage.OutputTokens);
        Assert.Empty(h.Input.Actions); Assert.True(final.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Theory]
    [InlineData("EMPTY_CONTENT", "EMPTY_REASONING_ONLY")]
    [InlineData("EMPTY_REASONING_ONLY", "EMPTY_SEARCH_ANSWER")]
    [InlineData("EMPTY_SEARCH_ANSWER", "OUTPUT_TOKEN_LIMIT")]
    [InlineData("OUTPUT_TOKEN_LIMIT", "EMPTY_CONTENT")]
    public async Task ConsecutiveDifferentEmptyResponseTypesStillRetryOnlyOnceAndKeepContinuationUi(string first, string second)
    {
        using var h = new Harness((_, count, _) => Error(count == 1 ? first : second), planned: true);
        await h.Start(); await h.Done();
        Assert.Equal(2, h.Provider.Requests.Count); Assert.Empty(h.Input.Actions);
        var task = h.Coordinator.Progress!.Task;
        Assert.Equal(TaskState.WaitingUser, task.State); Assert.Equal(second, task.LastError!.Code);
        Assert.Null(task.PendingQuestion); Assert.Null(task.PendingActionApproval);
        Assert.NotNull(task.Interpretation); Assert.Single(task.Interpretation.Steps);
        Assert.Contains("点击继续", h.Coordinator.Progress.Summary);
        Assert.DoesNotContain("请手动处理", h.Coordinator.Progress.Summary);
        Assert.Equal(3, task.Usage.ApiAttempts); Assert.Equal(24, task.Usage.InputTokens); Assert.Equal(28, task.Usage.OutputTokens);
        Assert.True(task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task MissingUsageIsNotInventedAsZeroDuringRecovery()
    {
        using var h = new Harness((r, count, _) => count == 1 ? Error("EMPTY_CONTENT", null, 9) : Task.FromResult(Reply(r, Finish(r))));
        await h.Start(); await h.Done();
        Assert.Equal(3, h.Coordinator.Progress!.Task.Usage.ApiAttempts);
        Assert.Null(h.Coordinator.Progress.Task.Usage.InputTokens); Assert.Equal(32, h.Coordinator.Progress.Task.Usage.OutputTokens);
        Assert.Empty(h.Input.Actions);
    }

    [Fact]
    public async Task AnEmptyResponseCannotCreateAnotherRequestPastTheOriginalBudget()
    {
        using var h = new Harness((_, _, _) => Error("EMPTY_CONTENT"));
        await h.Start(requestLimit: 2); await h.Done();
        Assert.Single(h.Provider.Requests); Assert.Equal(2, h.Coordinator.Progress!.Task.Usage.ApiAttempts);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress.Task.State); Assert.Null(h.Coordinator.Progress.Task.PendingQuestion);
        Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
    }

    [Theory]
    [InlineData("AUTHENTICATION")]
    [InlineData("PERMISSION")]
    [InlineData("NETWORK_ERROR")]
    [InlineData("TIMEOUT")]
    [InlineData("RATE_LIMIT")]
    [InlineData("RESPONSE_REFUSAL")]
    [InlineData("CONTENT_FILTERED")]
    public async Task AuthenticationNetworkTimeoutAndRefusalDoNotEnterEmptyResponseRetry(string code)
    {
        using var h = new Harness((_, _, _) => Error(code));
        await h.Start(); await h.Done();
        Assert.Single(h.Provider.Requests); Assert.False(h.Provider.Requests.Single().Reconsidering);
        var task = h.Coordinator.Progress!.Task;
        Assert.Equal(TaskState.WaitingUser, task.State); Assert.Equal(code, task.LastError!.Code);
        Assert.Equal(2, task.Usage.ApiAttempts); Assert.Null(task.PendingQuestion); Assert.Empty(h.Input.Actions);
        Assert.True(task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchRecoveryIsDowngradedAndExistingSearchContinuationSurvivesTheEmptyRetry(bool searchAlreadyCompleted)
    {
        int failureCall = searchAlreadyCompleted ? 6 : 5;
        using var h = new Harness((r, count, _) => count < 5 ? Task.FromResult(Reply(r, Click(r))) :
            searchAlreadyCompleted && count == 5 ? Task.FromResult(new ProviderReply("", new(0, 0)) { SearchContinuation = "bounded-search-reference" }) :
            Error(count == failureCall ? "EMPTY_SEARCH_ANSWER" : "EMPTY_CONTENT"), planned: true, rejectResolution: true);
        await h.Start(); await h.Done();
        var requests = h.Provider.Requests.ToArray(); Assert.Equal(failureCall + 1, requests.Length);
        Assert.Equal(2, requests[4].RecoveryLevel);
        var firstError = requests[failureCall - 1]; var retry = requests[failureCall];
        Assert.NotEqual(firstError.CurrentFrame.Id, retry.CurrentFrame.Id);
        Assert.Equal(0, retry.RecoveryLevel); Assert.True(retry.Reconsidering);
        Assert.Equal(searchAlreadyCompleted ? "bounded-search-reference" : null, retry.SearchContinuation);
        Assert.Equal(firstError.SearchContinuation, retry.SearchContinuation);
        Assert.Contains(retry.RecentResults, r => r.Code == "EMPTY_SEARCH_ANSWER");
        Assert.Equal(4, h.Controls.Resolutions); Assert.Empty(h.Input.Actions);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress!.Task.State); Assert.Null(h.Coordinator.Progress.Task.PendingQuestion);
        Assert.Equal(failureCall + 2, h.Coordinator.Progress.Task.Usage.ApiAttempts); // Intent is separately accounted.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PauseOrStopDropsLateFirstErrorOrLateRetryAction(bool stop, bool duringRetry)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness((_, count, _) =>
        {
            if (duringRetry && count == 1) return Error("EMPTY_CONTENT");
            entered.TrySetResult(); return late.Task; // Ignores cancellation to model a late backend result.
        });
        try
        {
            var task = await h.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cancel = stop ? h.Coordinator.StopAsync(task.Id) : h.Coordinator.PauseAsync(task.Id);
            if (duringRetry) late.TrySetResult(Reply(h.Provider.Requests.Last(), Click(h.Provider.Requests.Last())));
            else late.TrySetException(Failure("EMPTY_CONTENT", 900, 800));
            await cancel; await h.Done();
            Assert.Equal(duringRetry ? 2 : 1, h.Provider.Requests.Count); Assert.Empty(h.Input.Actions);
            Assert.Equal(stop ? TaskState.Cancelled : TaskState.Paused, h.Coordinator.Progress!.Task.State);
            Assert.Null(h.Coordinator.Progress.Task.Usage.InputTokens); Assert.Null(h.Coordinator.Progress.Task.Usage.OutputTokens);
            Assert.True(h.Coordinator.Progress.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
        }
        finally { late.TrySetCanceled(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrStopDuringFreshRecoveryCaptureNeverDispatchesTheRetry(bool stop)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness((_, _, _) => Error("EMPTY_CONTENT"));
        h.Desktop.BeforeCapture = async (count, _) => { if (count == 3) { entered.TrySetResult(); await release.Task; } };
        try
        {
            var task = await h.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cancel = stop ? h.Coordinator.StopAsync(task.Id) : h.Coordinator.PauseAsync(task.Id);
            release.TrySetResult(); await cancel; await h.Done();
            Assert.Single(h.Provider.Requests); Assert.Empty(h.Input.Actions);
            Assert.Equal(stop ? TaskState.Cancelled : TaskState.Paused, h.Coordinator.Progress!.Task.State);
            Assert.Equal(2, h.Coordinator.Progress.Task.Usage.ApiAttempts);
            Assert.Equal(17, h.Coordinator.Progress.Task.Usage.InputTokens); Assert.Equal(19, h.Coordinator.Progress.Task.Usage.OutputTokens);
            Assert.True(h.Coordinator.Progress.Task.CleanupComplete); Assert.False(h.Gate.Status.IsOpen);
        }
        finally { release.TrySetResult(); }
    }
}
