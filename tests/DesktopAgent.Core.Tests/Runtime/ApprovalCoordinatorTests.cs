using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

// In-memory fakes only: these tests never open or send anything in a messaging app.
public sealed class ApprovalCoordinatorTests
{
    private sealed class Desktop : IDesktopObserver
    {
        internal static readonly PhysicalRect Bounds = new(0, 0, 1000, 800);
        internal ForegroundIdentity Foreground = new("1", 100, "WeChat", Bounds) { WindowClass = "WeChatMainWnd" };
        private int _sequence;
        private Frame? _last;
        public Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct) => Task.FromResult(new DesktopEnvironment(1,
            [new("display", Bounds, Bounds, 120, 120, true)], Foreground, DesktopSessionState.Available));
        public Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? physicalRegion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var region = physicalRegion ?? Bounds;
            var frame = new Frame("frame-" + Interlocked.Increment(ref _sequence), lease, DateTimeOffset.UtcNow, 1, monitorId, region,
                new(region.Width, region.Height, "image/png", [1, 2, 3]), physicalRegion is null ? FrameViewKind.Overview : FrameViewKind.Crop,
                Foreground, [], physicalRegion is null ? null : _last!.Id);
            _last = frame; return Task.FromResult(frame);
        }
    }
    private sealed class Controls : IControlObserver
    {
        internal string Draft = "你好";
        public Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct) => Task.FromResult(new ControlSnapshot(Guid.NewGuid().ToString("N"),
            frame.Lease, frame.Id, DateTimeOffset.UtcNow, ControlSnapshotStatus.Available,
            [new ControlCandidate("draft", null, "聊天输入框", "Edit", new(50, 200, 400, 100), true, true, true, null, null)
                { CurrentValue = Draft, ValueTruncated = false, SelectionStart = Draft.Length, SelectionLength = 0 },
             new("send", null, "发送", "Button", new(480, 380, 40, 40), true, false, true, null, null)]));
        public Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct)
        {
            var bounds = snapshot.Candidates.Single(c => c.Id == controlId).Bounds;
            return Task.FromResult(new ControlResolution(new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), null));
        }
    }
    private sealed class Provider(Func<ModelRequest, int, Task<ProviderReply>> handler) : IModelProvider
    {
        internal readonly ConcurrentQueue<ModelRequest> Requests = new(); private int _count;
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
        { Requests.Enqueue(request); return handler(request, Interlocked.Increment(ref _count)); }
        public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Input : IInputExecutor
    {
        internal readonly ConcurrentQueue<ValidatedAction> Actions = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WaitForCancellation;
        internal ActionStatus Status = ActionStatus.Injected;
        public async Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Actions.Enqueue(action); Entered.TrySetResult();
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Status = ActionStatus.Cancelled; }
            }
            return new(action.ProposalId, lease.TaskId, lease.Epoch, Status, Status == ActionStatus.Uncertain ? "INPUT_UNCERTAIN" : null,
                DateTimeOffset.UtcNow, 1, 1, 1, "FAKE INPUT ONLY", null);
        }
    }
    private sealed class Harness : IDisposable
    {
        internal readonly InputSafetyGate Gate = new();
        internal readonly Desktop Desktop = new(); internal readonly Controls Controls = new(); internal readonly Input Input = new();
        internal readonly Provider Provider; internal readonly DesktopTaskCoordinator Coordinator; private readonly ProviderProfile _profile;
        internal Harness(Func<ModelRequest, int, object>? decision = null, Func<ModelRequest, int, Task<ProviderReply>>? asyncHandler = null)
        {
            Gate.SetHotkeysReady(true);
            _profile = ProviderConfiguration.DefaultDeepSeek(); string fingerprint = ProviderConfiguration.Fingerprint(_profile);
            _profile = _profile with { ProbeFingerprint = fingerprint, Capabilities = new[] { "vision", "grounding", "schema" }
                .Select(name => new CapabilityRecord(name, CapabilityStatus.ProbePassed, null, DateTimeOffset.UtcNow, fingerprint, "OFFLINE_TEST_ONLY")).ToImmutableArray() };
            Provider = new(asyncHandler ?? ((request, count) => Task.FromResult(Reply(request, decision?.Invoke(request, count) ??
                (request.ProtocolPrompt.Contains("RESULT VERIFICATION ONLY", StringComparison.Ordinal) ? Finish(request) : Send(request))))));
            Coordinator = new(Gate, Desktop, Controls, Provider, _profile, new DesktopPolicyValidator(TimeSpan.FromSeconds(90)), _ => Input, controlsRequired: true);
        }
        internal Task<TaskContext> Start() => Coordinator.StartAsync(new("给张三发送你好", ProviderConfiguration.Fingerprint(_profile), "display", new(20, 30, 30000))
            { HighRiskEnabled = true }, default);
        internal Task Done() => Coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        internal async Task<TaskContext> AwaitReview()
        {
            await Start(); await Done(); var task = Coordinator.Progress!.Task;
            Assert.Equal(TaskState.AwaitingApproval, task.State); Assert.True(task.CleanupComplete);
            Assert.NotNull(task.PendingActionApproval); Assert.Empty(Input.Actions); Assert.False(Gate.Status.IsOpen); return task;
        }
        internal async Task Approve(TaskContext waiting)
        { await Coordinator.ApproveActionAsync(waiting.Lease, waiting.PendingActionApproval!.Id); Assert.Empty(Input.Actions); }
        public void Dispose() => Gate.Trip(InputStopReason.Shutdown);
    }
    private static object Send(ModelRequest request, string recipient = "张三", string message = "你好") => new
    {
        kind = "act_control", snapshotId = request.Controls!.Id, controlId = "send", button = "left", clickCount = 1,
        target = "发送消息", expected = "当前会话出现消息", messageReview = new { recipient, message }
    };
    private static object Finish(ModelRequest request) => new { kind = "finish", outcome = "succeeded", summary = "测试核对完成", evidence = new[]
        { new { frameId = request.CurrentFrame.Id, appName = "OFFLINE TEST", observedText = "测试结果", interpretation = "仅测试" } } };
    private static ProviderReply Reply(ModelRequest request, object decision) => new(JsonSerializer.Serialize(new
    {
        schemaVersion = 2, proposalId = Guid.NewGuid().ToString("N"), taskId = request.Task.Lease.TaskId, epoch = request.Task.Lease.Epoch,
        frameId = request.CurrentFrame.Id, current = "测试观察", next = "测试下一步", decision
    }), new(1, 1));

    [Fact]
    public async Task ExplicitApprovalRequiresFreshObservationAndCommitsExactlyOnce()
    {
        using var h = new Harness(); var waiting = await h.AwaitReview();
        Assert.Equal("张三", waiting.PendingActionApproval!.Recipient); Assert.Equal("你好", waiting.PendingActionApproval.Message);
        string priorFrame = h.Provider.Requests.Last().CurrentFrame.Id;
        await h.Approve(waiting); Assert.Equal(TaskState.Paused, h.Coordinator.Progress!.Task.State);
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Single(h.Input.Actions); Assert.True(h.Coordinator.Progress.Task.MessageCommitAttempted);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State);
        Assert.NotEqual(priorFrame, h.Input.Actions.Single().FrameId);
        Assert.Contains("RESULT VERIFICATION ONLY", h.Provider.Requests.Last().ProtocolPrompt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ApproveActionAsync(waiting.Lease, waiting.PendingActionApproval.Id));
    }

    [Fact]
    public async Task OrdinaryReplyAndStaleConfirmationNeverApprove()
    {
        using var h = new Harness(); var waiting = await h.AwaitReview();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.AnswerAsync(waiting.Lease, waiting.PendingActionApproval!.Id, "批准"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ApproveActionAsync(waiting.Lease, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ApproveActionAsync(new(waiting.Id, waiting.Epoch + 1), waiting.PendingActionApproval!.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ResumeAsync(waiting.Id, default));
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.AwaitingApproval, h.Coordinator.Progress!.Task.State);
    }

    [Fact]
    public async Task PausingAnAwaitingApprovalTaskClearsTheCardAndRemainsResumable()
    {
        using var h = new Harness(); var waiting = await h.AwaitReview();
        await h.Coordinator.PauseAsync(waiting.Id);
        Assert.Equal(TaskState.Paused, h.Coordinator.Progress!.Task.State);
        Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval);
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Equal(TaskState.AwaitingApproval, h.Coordinator.Progress.Task.State); Assert.Empty(h.Input.Actions);
        Assert.NotEqual(waiting.PendingActionApproval!.Id, h.Coordinator.Progress.Task.PendingActionApproval!.Id);
    }

    [Fact]
    public async Task FocusingAVerifiedChatEditIsPreparationAndDoesNotConsumeTheMessageCommit()
    {
        using var h = new Harness((request, count) => count == 1 ? new
        {
            kind = "act_control", snapshotId = request.Controls!.Id, controlId = "draft", button = "left", clickCount = 1,
            target = "聊天输入框", expected = "光标进入输入框"
        } : Finish(request));
        await h.Start(); await h.Done();
        Assert.Single(h.Input.Actions); Assert.False(h.Coordinator.Progress!.Task.MessageCommitAttempted);
        Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval); Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress.Task.State);
    }

    [Fact]
    public async Task ARealSendButtonCannotBeRelabeledAsSearchToSkipApproval()
    {
        using var h = new Harness((request, _) => new
        {
            kind = "act_control", snapshotId = request.Controls!.Id, controlId = "send", button = "left", clickCount = 1,
            target = "搜索联系人", expected = "进入搜索结果"
        });
        await h.Start(); await h.Done();
        Assert.Empty(h.Input.Actions); Assert.False(h.Coordinator.Progress!.Task.MessageCommitAttempted);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress.Task.State);
        Assert.Equal("MESSAGE_REVIEW_REQUIRED", h.Coordinator.Progress.Task.LastError!.Code);
    }

    [Fact]
    public async Task CorrectingAnAwaitingApprovalTaskClearsOldApprovalAndCanResume()
    {
        using var h = new Harness(); var waiting = await h.AwaitReview();
        await h.Coordinator.CorrectAsync(waiting.Id, "给张三发送你好并核对结果");
        Assert.Equal(TaskState.Paused, h.Coordinator.Progress!.Task.State); Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ApproveActionAsync(waiting.Lease, waiting.PendingActionApproval!.Id));
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.AwaitingApproval, h.Coordinator.Progress.Task.State);
    }

    [Fact]
    public async Task RejectAndStopDiscardAuthorityAndANewTaskGetsANewConfirmation()
    {
        using var h = new Harness(); var waiting = await h.AwaitReview();
        await h.Coordinator.RejectActionAsync(waiting.Lease, waiting.PendingActionApproval!.Id);
        Assert.Equal(TaskState.Paused, h.Coordinator.Progress!.Task.State); Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval);
        await h.Coordinator.StopAsync(waiting.Id);
        var next = await h.AwaitReview(); Assert.NotEqual(waiting.Id, next.Id);
        Assert.False(next.MessageCommitAttempted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ApproveActionAsync(waiting.Lease, waiting.PendingActionApproval.Id));
        Assert.Empty(h.Input.Actions);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("window")]
    [InlineData("recipient")]
    public async Task ChangesWhileUserApprovesRequireANewCardBeforeInput(string change)
    {
        bool changed = false;
        using var h = new Harness((request, _) => Send(request, changed && change == "recipient" ? "李四" : "张三"));
        var waiting = await h.AwaitReview(); await h.Approve(waiting); changed = true;
        if (change == "draft") h.Controls.Draft = "更改后的草稿";
        if (change == "window") h.Desktop.Foreground = h.Desktop.Foreground with { HwndHex = "2" };
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.AwaitingApproval, h.Coordinator.Progress!.Task.State);
        Assert.NotEqual(waiting.PendingActionApproval!.Id, h.Coordinator.Progress.Task.PendingActionApproval!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SentOrUncertainMessageCannotBeReplayedOnFollowingRequestsOrResume(bool uncertain)
    {
        using var h = new Harness((request, _) => Send(request)); if (uncertain) h.Input.Status = ActionStatus.Uncertain;
        var waiting = await h.AwaitReview(); await h.Approve(waiting);
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Single(h.Input.Actions); Assert.True(h.Coordinator.Progress!.Task.MessageCommitAttempted);
        Assert.Equal(TaskState.WaitingUser, h.Coordinator.Progress.Task.State);
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Single(h.Input.Actions); Assert.Equal("RESULT_VERIFICATION_ONLY", h.Coordinator.Progress.Task.LastError!.Code);
        Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval);
    }

    [Fact]
    public async Task StopAfterApprovalBeforeResumePreventsAnyInput()
    {
        using var h = new Harness(); var waiting = await h.AwaitReview(); await h.Approve(waiting);
        await h.Coordinator.StopAsync(waiting.Id);
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.Cancelled, h.Coordinator.Progress!.Task.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ResumeAsync(waiting.Id, default));
    }

    [Theory]
    [InlineData(InputStopReason.Paused, TaskState.Paused)]
    [InlineData(InputStopReason.HotkeysUnavailable, TaskState.Interrupted)]
    public async Task NativeGateCancellationRevokesApprovalEvenWithoutCallingPauseAsync(InputStopReason reason, TaskState expectedState)
    {
        var entered = new TaskCompletionSource<ModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness(asyncHandler: (request, count) =>
        {
            if (count != 2) return Task.FromResult(Reply(request, Send(request)));
            entered.TrySetResult(request); return late.Task;
        });
        try
        {
            var waiting = await h.AwaitReview(); await h.Approve(waiting);
            await h.Coordinator.ResumeAsync(waiting.Id, default);
            var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // A native hotkey trips the gate directly; it does not call Coordinator.PauseAsync.
            h.Gate.Trip(reason);
            late.TrySetResult(Reply(request, Send(request))); await h.Done();
            Assert.Equal(expectedState, h.Coordinator.Progress!.Task.State); Assert.Empty(h.Input.Actions);
            Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval);
            await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
            Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.AwaitingApproval, h.Coordinator.Progress.Task.State);
            Assert.NotEqual(waiting.PendingActionApproval!.Id, h.Coordinator.Progress.Task.PendingActionApproval!.Id);
        }
        finally { late.TrySetCanceled(); }
    }

    [Fact]
    public async Task AChangedGateRevisionDuringApprovalCountdownRevokesTheApprovedStep()
    {
        using var h = new Harness((request, _) => Send(request));
        var waiting = await h.AwaitReview(); await h.Approve(waiting);
        long countdownRevision = h.Gate.Status.Revision;
        h.Gate.Trip(InputStopReason.Paused);
        await h.Coordinator.ResumeWithGateRevisionAsync(waiting.Id, countdownRevision, default); await h.Done();
        Assert.Equal(TaskState.Interrupted, h.Coordinator.Progress!.Task.State); Assert.Empty(h.Input.Actions);
        Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval);
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.AwaitingApproval, h.Coordinator.Progress.Task.State);
        Assert.NotEqual(waiting.PendingActionApproval!.Id, h.Coordinator.Progress.Task.PendingActionApproval!.Id);
    }

    [Fact]
    public async Task AClarificationAfterApprovalCannotReuseTheEarlierGrant()
    {
        using var h = new Harness((request, count) => count == 2 ? new
        {
            kind = "ask_user", reason = "需要重新确认任务范围", question = "是否继续处理当前会话？"
        } : Send(request));
        var waiting = await h.AwaitReview(); await h.Approve(waiting);
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        var question = h.Coordinator.Progress!.Task;
        Assert.Equal(TaskState.WaitingUser, question.State); Assert.NotNull(question.PendingQuestion);
        await h.Coordinator.AnswerAsync(question.Lease, question.PendingQuestion.Id, "先不要发送，我要重新核对正文");
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Empty(h.Input.Actions); Assert.Equal(TaskState.AwaitingApproval, h.Coordinator.Progress.Task.State);
        Assert.NotEqual(waiting.PendingActionApproval!.Id, h.Coordinator.Progress.Task.PendingActionApproval!.Id);
    }

    [Fact]
    public async Task PausingAnInFlightCommitKeepsTheNoResendFlagAcrossEpochs()
    {
        using var h = new Harness((request, _) => Send(request)); h.Input.WaitForCancellation = true;
        var waiting = await h.AwaitReview(); await h.Approve(waiting);
        await h.Coordinator.ResumeAsync(waiting.Id, default);
        await h.Input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(h.Coordinator.Progress!.Task.MessageCommitAttempted);
        await h.Coordinator.PauseAsync(waiting.Id); await h.Done();
        h.Input.WaitForCancellation = false;
        await h.Coordinator.ResumeAsync(waiting.Id, default); await h.Done();
        Assert.Single(h.Input.Actions); Assert.Equal("RESULT_VERIFICATION_ONLY", h.Coordinator.Progress.Task.LastError!.Code);
    }

    [Fact]
    public async Task AnAttemptedMessageCannotBeReauthorizedByEnablingTaskPermission()
    {
        using var h = new Harness(); h.Input.Status = ActionStatus.Uncertain;
        var approval = await h.AwaitReview(); await h.Approve(approval);
        await h.Coordinator.ResumeAsync(approval.Id, default); await h.Done();
        var uncertain = h.Coordinator.Progress!.Task;
        Assert.Equal(TaskState.WaitingUser, uncertain.State); Assert.True(uncertain.MessageCommitAttempted);
        Assert.Single(h.Input.Actions);
        Assert.False(await h.Coordinator.EnableHighRiskAsync(uncertain.Lease, default));
        Assert.Equal(uncertain, h.Coordinator.Progress.Task); Assert.Single(h.Input.Actions);
        Assert.False(h.Gate.Status.IsOpen); Assert.Null(h.Coordinator.Progress.Task.PendingActionApproval);
    }
}
