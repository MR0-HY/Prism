using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

// In-memory observations, provider replies and input results only; no Windows or model API calls.
public sealed class TaskPermissionCoordinatorTests
{
    private sealed class Desktop : IDesktopObserver
    {
        private int _sequence;
        internal readonly PhysicalRect Bounds = new(0, 0, 1000, 800);
        internal readonly ForegroundIdentity Foreground = new("1", 100, "WeChat", new(0, 0, 1000, 800)) { WindowClass = "FixtureWindow" };
        public Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct) => Task.FromResult(new DesktopEnvironment(1,
            [new("display", Bounds, Bounds, 120, 120, true)], Foreground, DesktopSessionState.Available));
        public Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? region, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new Frame("frame-" + Interlocked.Increment(ref _sequence), lease, DateTimeOffset.UtcNow, 1, monitorId,
                Bounds, new(1000, 800, "image/png", [1, 2, 3]), FrameViewKind.Overview, Foreground, []));
        }
    }
    private sealed class Controls(string name) : IControlObserver
    {
        public Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct) => Task.FromResult(new ControlSnapshot(Guid.NewGuid().ToString("N"),
            frame.Lease, frame.Id, DateTimeOffset.UtcNow, ControlSnapshotStatus.Available,
            [new("entry", null, name, "Button", new(480, 380, 40, 40), true, false, true, null, null)]));
        public Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct) =>
            Task.FromResult(new ControlResolution(new(500, 400), null));
    }
    private sealed class Provider(Func<ModelRequest, ProviderReply> reply) : IModelProvider, ITaskIntentProvider
    {
        internal readonly ConcurrentQueue<ModelRequest> Decisions = new(), Intents = new();
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Decisions.Enqueue(request); return Task.FromResult(reply(request)); }
        public Task<ProviderReply> InterpretAsync(ModelRequest request, CancellationToken ct)
        {
            Intents.Enqueue(request);
            return Task.FromResult(new ProviderReply(JsonSerializer.Serialize(new
            {
                goal = request.Task.Goal, reply = "我会核对当前按钮，再完成目标。",
                steps = new[] { new { id = "s1", title = "定位当前按钮", completionCheck = "新画面显示目标按钮" },
                    new { id = "s2", title = "完成目标并核对", completionCheck = "新画面符合原始目标" } },
                completionCheck = "完整目标已从新的画面核对"
            }), new(1, 1)));
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
        internal readonly InputSafetyGate Gate = new(); internal readonly Input Input = new();
        internal readonly Provider Provider; internal readonly DesktopTaskCoordinator Coordinator;
        private readonly ProviderProfile _profile;
        internal Harness(string controlName = "进入微信", Func<ModelRequest, ProviderReply>? reply = null)
        {
            Gate.SetHotkeysReady(true); _profile = ProviderConfiguration.DefaultDeepSeek(); string fingerprint = ProviderConfiguration.Fingerprint(_profile);
            _profile = _profile with { ProbeFingerprint = fingerprint, Capabilities = new[] { "vision", "grounding", "schema" }.Select(name =>
                new CapabilityRecord(name, CapabilityStatus.ProbePassed, null, DateTimeOffset.UtcNow, fingerprint, "OFFLINE TEST ONLY")).ToImmutableArray() };
            Provider = new(reply ?? (request => Reply(request, Input.Actions.IsEmpty ? Click(request) : Finish(request), !Input.Actions.IsEmpty)));
            Coordinator = new(Gate, new Desktop(), new Controls(controlName), Provider, _profile,
                new DesktopPolicyValidator(TimeSpan.FromSeconds(90)), _ => Input, controlsRequired: true);
        }
        internal async Task<TaskContext> Run(bool highRisk = false, string goal = "打开微信")
        {
            await Coordinator.StartAsync(new(goal, ProviderConfiguration.Fingerprint(_profile), "display", new(12, 20, 30000))
                { HighRiskEnabled = highRisk }, default);
            await Done(); return Coordinator.Progress!.Task;
        }
        internal Task Done() => Coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        public void Dispose() => Gate.Trip(InputStopReason.Shutdown);
    }
    private static object Click(ModelRequest request) => new { kind = "act_control", snapshotId = request.Controls!.Id, controlId = "entry", button = "left", clickCount = 1,
        target = "当前按钮", expected = "显示目标页面" };
    private static object Finish(ModelRequest request) => new { kind = "finish", outcome = "succeeded", summary = "测试中的完整结果已核对", evidence = new[]
        { new { frameId = request.CurrentFrame.Id, appName = "Fixture", observedText = "目标页面", interpretation = "仅离线测试" } } };
    private static ProviderReply Reply(ModelRequest request, object decision, bool completed = false) => new(JsonSerializer.Serialize(new
    {
        schemaVersion = 2, proposalId = Guid.NewGuid().ToString("N"), taskId = request.Task.Lease.TaskId, epoch = request.Task.Lease.Epoch,
        frameId = request.CurrentFrame.Id, current = "已观察测试界面", next = "完成原始目标", decision,
        planUpdates = new[] { new { stepId = "s1", status = "completed", observation = "当前界面有唯一目标按钮" },
            new { stepId = "s2", status = completed ? "completed" : "active", observation = completed ? "新的画面显示目标结果" : "尚未执行目标步骤" } }
    }), new(1, 1));

    [Theory]
    [InlineData("进入微信", "打开微信", "ACCOUNT_ENTRY_PERMISSION_REQUIRED")]
    [InlineData("其他按钮", "打开微信", "MESSAGE_COMMIT_DISABLED")]
    [InlineData("其他按钮", "删除测试文件", "HIGH_IMPACT_MANUAL")]
    public async Task ExplicitPermissionUpgradePreservesTheWholeTaskButDoesNotApproveOrRun(string name, string goal, string code)
    {
        using var h = new Harness(name); var waiting = await h.Run(goal: goal);
        Assert.Equal(TaskState.WaitingUser, waiting.State); Assert.Equal(code, waiting.LastError!.Code);
        Assert.True(waiting.CleanupComplete); Assert.NotNull(waiting.PendingQuestion); Assert.False(waiting.HighRiskEnabled);
        long gateRevision = h.Gate.Status.Revision; int calls = h.Provider.Decisions.Count;
        Assert.True(await h.Coordinator.EnableHighRiskAsync(waiting.Lease, default));
        var updated = h.Coordinator.Progress!.Task;
        Assert.Equal(waiting.Id, updated.Id); Assert.Equal(waiting.Goal, updated.Goal); Assert.Equal(waiting.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.Equal(waiting.Interpretation, updated.Interpretation); Assert.Equal(waiting.PlanProgress, updated.PlanProgress);
        Assert.Equal(waiting.Budget, updated.Budget); Assert.Equal(waiting.Usage, updated.Usage); Assert.Equal(waiting.Clarifications, updated.Clarifications);
        Assert.Equal(waiting.RecentResults, updated.RecentResults); Assert.True(updated.Epoch > waiting.Epoch);
        Assert.True(updated.HighRiskEnabled); Assert.Equal(TaskState.Paused, updated.State); Assert.True(updated.CleanupComplete);
        Assert.Null(updated.PendingQuestion); Assert.Null(updated.PendingActionApproval); Assert.Null(updated.LastError);
        Assert.False(updated.ActionApprovalGranted); Assert.False(updated.MessageCommitAttempted);
        Assert.Equal(gateRevision, h.Gate.Status.Revision); Assert.False(h.Gate.Status.IsOpen); Assert.Null(h.Gate.Status.Lease);
        Assert.Empty(h.Input.Actions); Assert.Equal(calls, h.Provider.Decisions.Count); Assert.Single(h.Provider.Intents);
        Assert.False(await h.Coordinator.EnableHighRiskAsync(waiting.Lease, default));
        Assert.False(await h.Coordinator.EnableHighRiskAsync(updated.Lease, default));
    }

    [Fact]
    public async Task StaleOrForeignLeaseCannotChangePermission()
    {
        using var h = new Harness(); var waiting = await h.Run();
        foreach (var lease in new[] { new Lease(Guid.NewGuid(), waiting.Epoch), new Lease(waiting.Id, waiting.Epoch + 1), new Lease(waiting.Id, -1) })
        {
            Assert.False(await h.Coordinator.EnableHighRiskAsync(lease, default));
            Assert.Equal(waiting, h.Coordinator.Progress!.Task);
        }
        Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task UserAnswerCannotEnableHighRiskOrApproveTheBlockedStep()
    {
        using var h = new Harness(); var waiting = await h.Run();
        await h.Coordinator.AnswerAsync(waiting.Lease, waiting.PendingQuestion!.Id, "同意，请继续");
        Assert.False(h.Coordinator.Progress!.Task.HighRiskEnabled); Assert.False(h.Coordinator.Progress.Task.ActionApprovalGranted);
        Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval); Assert.Empty(h.Input.Actions);
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress.Task.State); Assert.False(h.Coordinator.Progress.Task.HighRiskEnabled);
        Assert.Equal("ACCOUNT_ENTRY_PERMISSION_REQUIRED", h.Coordinator.Progress.Task.LastError!.Code); Assert.Empty(h.Input.Actions);
    }

    [Fact]
    public async Task CancelledUpgradeLeavesThePausedQuestionAndEpochUnchanged()
    {
        using var h = new Harness(); var waiting = await h.Run();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Coordinator.EnableHighRiskAsync(waiting.Lease, cancellation.Token));
        Assert.Equal(waiting, h.Coordinator.Progress!.Task); Assert.Empty(h.Input.Actions);
    }

    [Fact]
    public async Task PausingOrStoppingNeverImplicitlyEnablesPermission()
    {
        using var h = new Harness(); var waiting = await h.Run();
        await h.Coordinator.PauseAsync(waiting.Id);
        Assert.False(h.Coordinator.Progress!.Task.HighRiskEnabled); Assert.False(h.Coordinator.Progress.Task.ActionApprovalGranted);
        await h.Coordinator.StopAsync(waiting.Id); var stopped = h.Coordinator.Progress.Task;
        Assert.Equal(TaskState.Cancelled, stopped.State);
        Assert.False(await h.Coordinator.EnableHighRiskAsync(waiting.Lease, default));
        Assert.False(await h.Coordinator.EnableHighRiskAsync(stopped.Lease, default));
        Assert.False(h.Coordinator.Progress.Task.HighRiskEnabled); Assert.Empty(h.Input.Actions);
    }

    [Fact]
    public async Task UnknownControlFailuresDoNotOfferAnAccountPermissionBypass()
    {
        using var h = new Harness(reply: request => Reply(request,
            new { kind = "ask_user", question = "请指出当前页面上的目标", reason = "未知界面" }));
        var waiting = await h.Run();
        Assert.Equal(TaskState.WaitingUser, waiting.State); Assert.NotEqual("ACCOUNT_ENTRY_PERMISSION_REQUIRED", waiting.LastError?.Code);
        Assert.False(await h.Coordinator.EnableHighRiskAsync(waiting.Lease, default));
        Assert.Equal(waiting, h.Coordinator.Progress!.Task); Assert.Empty(h.Input.Actions);
    }

    [Theory]
    [InlineData("EMPTY_CONTENT", 2)]
    [InlineData("NETWORK_ERROR", 1)]
    public async Task ProviderFailuresStayResumableWithoutBecomingPermissionRequests(string code, int attempts)
    {
        var failure = new ProviderCallException(code);
        using var h = new Harness(reply: _ => throw failure);
        var diagnostics = new List<Exception>(); h.Coordinator.DiagnosticFault += diagnostics.Add;
        var waiting = await h.Run();
        Assert.Equal(TaskState.WaitingUser, waiting.State); Assert.Equal(code, waiting.LastError!.Code);
        Assert.NotNull(waiting.Interpretation); Assert.Equal(2, waiting.Interpretation.Steps.Length);
        Assert.Equal(attempts, h.Provider.Decisions.Count); Assert.Equal(attempts, diagnostics.Count);
        Assert.All(diagnostics, diagnostic => Assert.Same(failure, diagnostic));
        Assert.False(await h.Coordinator.EnableHighRiskAsync(waiting.Lease, default));
        Assert.Equal(waiting, h.Coordinator.Progress!.Task); Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task RuntimeFailureEmitsDiagnosticWithoutDependingOnTheDiagnosticSubscriber()
    {
        var failure = new InvalidOperationException("PRIVATE_EXCEPTION_TEXT_NOT_FOR_PRODUCT_LOG");
        using var h = new Harness(reply: _ => throw failure);
        Exception? reported = null;
        h.Coordinator.DiagnosticFault += error => { reported = error; throw new InvalidOperationException("broken diagnostic subscriber"); };
        var interrupted = await h.Run();
        Assert.Same(failure, reported); Assert.Equal(TaskState.Interrupted, interrupted.State); Assert.Equal("RUNTIME_ERROR", interrupted.LastError!.Code);
        Assert.DoesNotContain(failure.Message, interrupted.LastError.PublicSummary); Assert.True(interrupted.CleanupComplete);
        Assert.Empty(h.Input.Actions); Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task UpgradedAccountEntryStillRequiresASeparateFreshBoundApprovalWithoutInventedMessage()
    {
        using var h = new Harness(); var waiting = await h.Run();
        Assert.True(await h.Coordinator.EnableHighRiskAsync(waiting.Lease, default));
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        var approval = h.Coordinator.Progress!.Task;
        Assert.Equal(TaskState.AwaitingApproval, approval.State); Assert.Equal("ACTION_APPROVAL_REQUIRED", approval.LastError!.Code);
        Assert.NotNull(approval.PendingActionApproval); Assert.Null(approval.PendingActionApproval.Recipient); Assert.Null(approval.PendingActionApproval.Message);
        Assert.Contains("进入微信", approval.PendingActionApproval.Target); Assert.Empty(h.Input.Actions);
        Assert.Equal(waiting.Interpretation, approval.Interpretation); Assert.Single(h.Provider.Intents);
        string approvalFrame = h.Provider.Decisions.Last().CurrentFrame.Id;
        await h.Coordinator.ApproveActionAsync(approval.Lease, approval.PendingActionApproval.Id);
        Assert.Empty(h.Input.Actions);
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        var finished = h.Coordinator.Progress!.Task;
        Assert.Single(h.Input.Actions); Assert.NotEqual(approvalFrame, h.Input.Actions.Single().FrameId);
        Assert.Equal(TaskState.Succeeded, finished.State); Assert.False(finished.MessageCommitAttempted);
        Assert.Equal(waiting.Id, finished.Id); Assert.Equal(waiting.Goal, finished.Goal); Assert.Single(h.Provider.Intents);
        Assert.DoesNotContain(h.Provider.Decisions, request => request.ProtocolPrompt.Contains("RESULT VERIFICATION ONLY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInitiallyEnabledAccountTaskAlsoNeedsASeparateApprovalWithoutAMessageDraft()
    {
        using var h = new Harness(); var task = await h.Run(highRisk: true);
        Assert.Equal(TaskState.AwaitingApproval, task.State); Assert.Equal("ACTION_APPROVAL_REQUIRED", task.LastError!.Code);
        Assert.Null(task.PendingActionApproval!.Recipient); Assert.Null(task.PendingActionApproval.Message);
        Assert.False(task.MessageCommitAttempted); Assert.Empty(h.Input.Actions);
        Assert.False(await h.Coordinator.EnableHighRiskAsync(task.Lease, default));
    }

    [Fact]
    public async Task ModelTargetTextCannotPretendThatAnUnknownButtonIsTheNativeAccountEntry()
    {
        using var h = new Harness("其他按钮", request => Reply(request, new
        {
            kind = "act_control", snapshotId = request.Controls!.Id, controlId = "entry", button = "left", clickCount = 1,
            target = "进入微信", expected = "显示主页面"
        }));
        var task = await h.Run(highRisk: true);
        Assert.Equal(TaskState.WaitingUser, task.State); Assert.Equal("MESSAGE_REVIEW_REQUIRED", task.LastError!.Code);
        Assert.Null(task.PendingActionApproval); Assert.Empty(h.Input.Actions);
    }

    [Theory]
    [InlineData("WeChat", "Button", "进入微信", true)]
    [InlineData("Weixin", "Button", "进入微信", true)]
    [InlineData("WeChat", "Button", "进入微信并发送", false)]
    [InlineData("WeChat", "Button", "发送", false)]
    [InlineData("WeChat", "Text", "进入微信", false)]
    [InlineData("WeChat", "ListItem", "进入微信", false)]
    [InlineData("Fixture", "Button", "进入微信", false)]
    public void OnlyTheExactEnabledAccountButtonCanProduceLocalAccountEntryEvidence(string process, string role, string name, bool expected)
    {
        var foreground = new ForegroundIdentity("1", 100, process, new(0, 0, 1000, 800));
        var candidate = new ControlCandidate("entry", null, name, role, new(480, 380, 40, 40), true, false, true, null, null);
        Assert.Equal(expected, HighRiskActions.IsAccountEntryCandidate(foreground, candidate));
        Assert.False(HighRiskActions.IsAccountEntryCandidate(foreground, candidate with { Enabled = false }));
        var act = new ActDecision(new ClickAction(new(500, 500), MouseButton.Left, 1), "进入微信", "显示目标页面") { VerifiedAccountEntry = true };
        Assert.DoesNotContain("VerifiedAccountEntry", JsonSerializer.Serialize(act), StringComparison.OrdinalIgnoreCase);
    }
}
