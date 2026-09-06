using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Providers;

namespace DesktopAgent.Core.Runtime;

public sealed record StageTimings(long ObservationMs, long ControlsMs, long ModelMs, long InputMs);
public sealed record AgentProgress(TaskContext Task, string Current, string Next, string Summary,
    ImmutableArray<Evidence> Evidence, StageTimings Timings, long Revision);

public sealed class DesktopTaskCoordinator : ITaskCoordinator
{
    private sealed class Run(LeaseSnapshot lease, LeaseWorker worker, InputRun permit, CancellationToken external, long baseline)
    {
        public readonly LeaseSnapshot Lease = lease;
        public readonly LeaseWorker Worker = worker;
        public readonly InputRun Permit = permit;
        public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(permit.Token, external);
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly long BaselineMs = baseline;
        // Only digests of actually injected visual/action pairs; bounded by the 300-action hard limit.
        // A resumed epoch creates a new Run, so an explicit user continuation can retry.
        public readonly HashSet<string> InjectedVisualActions = new(StringComparer.Ordinal);
        public TaskState? FinalState;
        public bool InputInFlight, ModelInFlight, Sealed;
    }
    private readonly object _sync = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly TaskLeaseRegistry _registry;
    private readonly InputSafetyGate _gate;
    private readonly IDesktopObserver _desktop;
    private readonly IControlObserver? _controls;
    private readonly IModelProvider _provider;
    private readonly ProviderProfile _profile;
    private readonly IPolicyValidator _policy;
    private readonly Func<InputRun, IInputExecutor> _input;
    private readonly IOverlayController? _overlay;
    private readonly bool _controlsRequired;
    private readonly Func<InputRun, CancellationToken, Task>? _prepareDesktop;
    private TaskLeaseSession? _session;
    private Run? _run;
    private Task _loop = Task.CompletedTask;
    private AgentProgress? _progress;
    private ActionApprovalBinding? _actionApproval;
    private readonly HashSet<string> _proposals = new(StringComparer.Ordinal);
    private long _inputTokens, _outputTokens;
    private bool _inputUsageKnown = true, _outputUsageKnown = true;
    public event Action<AgentProgress>? Changed;
    public event Action<Exception>? DiagnosticFault;
    public AgentProgress? Progress { get { lock (_sync) return _progress; } }
    public Task Completion { get { lock (_sync) return _loop; } }
    public DesktopTaskCoordinator(InputSafetyGate gate, IDesktopObserver desktop, IControlObserver? controls,
        IModelProvider provider, ProviderProfile profile, IPolicyValidator policy, Func<InputRun, IInputExecutor> input,
        TaskLeaseRegistry? registry = null, IOverlayController? overlay = null, bool controlsRequired = false,
        Func<InputRun, CancellationToken, Task>? prepareDesktop = null)
        => (_gate, _desktop, _controls, _provider, _profile, _policy, _input, _registry, _overlay, _controlsRequired, _prepareDesktop) = (gate, desktop, controls, provider, profile, policy, input, registry ?? new(), overlay, controlsRequired, prepareDesktop);

    public static bool IsProfileVerified(ProviderProfile profile, bool controlsRequired = false)
    {
        string fingerprint = ProviderConfiguration.Fingerprint(profile);
        bool Passed(string[] names) => !profile.Capabilities.IsDefaultOrEmpty && names.All(name =>
            profile.Capabilities.Any(c => c.Name == name && c.Status == CapabilityStatus.ProbePassed && c.ProfileFingerprint == fingerprint && c.TestedAt is not null));
        return (profile.ProbeFingerprint == fingerprint && Passed(["vision", "grounding", "schema"])) ||
            (controlsRequired && Passed(["hybrid_vision", "hybrid_grounding", "hybrid_schema"]));
    }

    private static bool HasUsableControls(ControlSnapshot controls)
        => (controls.Status is ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial) && !controls.Candidates.IsDefaultOrEmpty;

    private static bool SameObservationSurface(Frame first, Frame second) => first.DisplayGeneration == second.DisplayGeneration &&
        first.MonitorId == second.MonitorId && FrameChecks.SameForeground(first.Foreground, second.Foreground);

    private static bool IsGlobalNavigation(AgentAction action) => DesktopNavigation.IsGlobalNavigation(action);
    private static void ValidateBudget(TaskBudget budget)
    {
        if (budget.ActionLimit is < 1 or > 300 || budget.RequestLimit is < 1 or > 500 || budget.ActiveMsLimit is < 10 or > 600000)
            throw new ArgumentException("BUDGET_OUT_OF_RANGE");
    }
    private static void ValidateGoal(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal) || goal.Length > 4000) throw new ArgumentException("GOAL_REQUIRED");
    }
    public async Task<TaskContext> StartAsync(TaskStartRequest request, CancellationToken ct)
    {
        ValidateGoal(request.Goal); ValidateBudget(request.Budget);
        if (!IsProfileVerified(_profile, _controlsRequired) || request.ProviderProfileFingerprint != ProviderConfiguration.Fingerprint(_profile))
            throw new InvalidOperationException("VISUAL_PROBE_REQUIRED");
        await _commands.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                if (_session is not null || !_loop.IsCompleted || !_gate.Status.HotkeysReady || _gate.Status.Lease is not null)
                    throw new InvalidOperationException("PREVIOUS_TASK_OR_INPUT_NOT_CLEAN");
                if (!_registry.TryAcquire(Guid.NewGuid(), out _session)) throw new InvalidOperationException("TASK_BUSY");
                _proposals.Clear(); _inputTokens = _outputTokens = 0; _inputUsageKnown = _outputUsageKnown = true;
                var task = new TaskContext(_session!.Identity.TaskId, _session.Identity.Epoch, request.Goal.Trim(), DateTimeOffset.UtcNow,
                    TaskState.Running, false, request.ProviderProfileFingerprint, request.MonitorId, request.Budget, new(0, 0, 0, null, null), [], null, null)
                    { HighRiskEnabled = request.HighRiskEnabled };
                _actionApproval = null;
                _progress = new(task, "准备观察桌面", "理解你的目标", "", [], new(0, 0, 0, 0), 1);
            }
            await BeginEpochAsync(ct, request.InputGateRevision);
            Notify();
            return Progress!.Task;
        }
        finally { _commands.Release(); }
    }

    private async Task BeginEpochAsync(CancellationToken ct, long? expectedGateRevision = null)
    {
        Task? failedCancellation = null;
        lock (_sync)
        {
            var task = _progress!.Task;
            var lease = _session!.Current;
            long remaining = task.Budget.ActiveMsLimit - task.Usage.ActiveMs;
            if (remaining <= 0) throw new InvalidOperationException("ACTIVE_BUDGET_EXHAUSTED");
            if (!_session.TryStartWorker(lease, out var worker)) throw new InvalidOperationException("WORKER_BUSY");
            if (!_gate.TryArm(_session, lease, expectedGateRevision ?? _gate.Status.Revision, TimeSpan.FromMilliseconds(remaining), out var permit))
            {
                worker!.Dispose();
                failedCancellation = _session.InvalidateAsync();
                _actionApproval = null;
                _progress = _progress with { Task = task with { Epoch = _session.Identity.Epoch, State = TaskState.Interrupted, PendingActionApproval = null }, Summary = "紧急热键或输入许可未就绪，请处理后继续。" };
            }
            else
            {
                var run = _run = new(lease, worker!, permit!, ct, task.Usage.ActiveMs);
                _progress = _progress with { Task = task with { Epoch = lease.Lease.Epoch, State = TaskState.Running, CleanupComplete = false, LastError = null }, Summary = "", Revision = _progress.Revision + 1 };
                _loop = Task.Run(() => LoopAsync(run));
            }
        }
        if (failedCancellation is not null)
        {
            await failedCancellation;
            lock (_sync) _progress = _progress! with { Task = _progress.Task with { CleanupComplete = true } };
        }
    }

    public Task PauseAsync(Guid taskId) { RequestStop(taskId, TaskState.Paused, "已停止输入，正在收尾。", InputStopReason.Paused); return Task.CompletedTask; }
    private void RequestStop(Guid taskId, TaskState state, string summary, InputStopReason reason)
    {
        lock (_sync)
        {
            if (_progress?.Task.Id == taskId)
            {
                _actionApproval = null;
                _progress = _progress with { Task = _progress.Task with { PendingActionApproval = null } };
            }
            if (_progress?.Task.Id != taskId) return;
            if (_run is null || _run.Sealed)
            {
                if (_run is null && _progress.Task.State == TaskState.AwaitingApproval)
                    _progress = _progress with { Task = _progress.Task with { State = state }, Summary = summary, Revision = _progress.Revision + 1 };
            }
            else
            {
                if (_run.FinalState != TaskState.Cancelled) _run.FinalState = state;
                _run.Clock.Stop();
                _gate.Trip(reason);
                _ = _session!.InvalidateAsync();
                _progress = _progress with { Task = _progress.Task with { Epoch = _session.Identity.Epoch, State = TaskState.Pausing }, Summary = summary, Revision = _progress.Revision + 1 };
            }
        }
        Notify();
    }

    public async Task StopAsync(Guid taskId)
    {
        RequestStop(taskId, TaskState.Cancelled, "已停止输入，正在收尾。", InputStopReason.Stopped);
        await _commands.WaitAsync();
        try
        {
            TaskLeaseSession? session;
            Task? closing = null;
            lock (_sync) if (_progress?.Task.Id == taskId && _run?.Sealed == true) closing = _loop;
            if (closing is not null) await closing;
            lock (_sync)
            {
                if (_progress?.Task.Id != taskId || _run is not null || _session is null) return;
                session = _session;
            }
            await session.RequestCompletionAsync();
            bool clean = _gate.Status.Lease is null && session.TryCompleteCleanup();
            lock (_sync)
            {
                if (clean) _session = null;
                _actionApproval = null;
                _progress = _progress! with { Task = _progress.Task with { Epoch = session.Identity.Epoch, State = TaskState.Cancelled, CleanupComplete = clean, PendingQuestion = null, PendingActionApproval = null }, Summary = "任务已停止。", Revision = _progress.Revision + 1 };
            }
            Notify();
        }
        finally { _commands.Release(); }
    }

    public Task ResumeAsync(Guid taskId, CancellationToken ct) => ResumeWithGateRevisionAsync(taskId, null, ct);
    public async Task ResumeWithGateRevisionAsync(Guid taskId, long? expectedGateRevision, CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                if (_progress?.Task.Id != taskId || _session is null || !_loop.IsCompleted || !_progress.Task.CleanupComplete ||
                    _progress.Task.State is not (TaskState.Paused or TaskState.WaitingUser or TaskState.Interrupted)) throw new InvalidOperationException("TASK_NOT_READY_TO_RESUME");
                if (_progress.Task.Usage.ActiveMs >= _progress.Task.Budget.ActiveMsLimit || _progress.Task.Usage.ApiAttempts >= _progress.Task.Budget.RequestLimit)
                    throw new InvalidOperationException("BUDGET_EXHAUSTED");
                if (_progress.Task.PendingQuestion is not null) throw new InvalidOperationException("QUESTION_REPLY_REQUIRED");
                _session.Resume();
            }
            await BeginEpochAsync(ct, expectedGateRevision);
            Notify();
        }
        finally { _commands.Release(); }
    }
    public Task CorrectAsync(Guid taskId, string goal)
    {
        ValidateGoal(goal);
        RequestStop(taskId, TaskState.Paused, "目标已修正，请核对桌面后继续。", InputStopReason.Paused);
        lock (_sync)
        {
            if (_progress?.Task.Id != taskId || _session is null) throw new InvalidOperationException("NO_ACTIVE_TASK");
            _progress = _progress with { Task = _progress.Task with { Goal = goal.Trim(), Interpretation = null, PlanProgress = [], RecentResults = [], UntrustedModelObservations = [], PendingQuestion = null, Clarifications = [] }, Evidence = [], Summary = "目标已修正，等待继续。", Revision = _progress.Revision + 1 };
        }
        Notify(); return Task.CompletedTask;
    }
    public async Task AnswerAsync(Lease lease, Guid questionId, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 1000 || answer.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("ANSWER_REQUIRED");
        await _commands.WaitAsync();
        try
        {
            lock (_sync)
            {
                var task = _progress?.Task;
                if (task is null || task.Lease != lease || task.State != TaskState.WaitingUser ||
                    task.PendingQuestion?.Id != questionId || !task.CleanupComplete || !_loop.IsCompleted || _run is not null || _session is null)
                    throw new InvalidOperationException("STALE_QUESTION");
                if (task.Clarifications.Length >= 8) throw new InvalidOperationException("CLARIFICATION_LIMIT");
                _actionApproval = null; // A new user reply may change the intended recipient, draft, or whether to send.
                _progress = _progress! with { Task = task with { PendingQuestion = null,
                    Clarifications = task.Clarifications.Add(new(task.PendingQuestion.Question, answer.Trim())) },
                    Summary = "已收到回复，准备继续原任务。", Revision = _progress.Revision + 1 };
            }
            Notify();
        }
        finally { _commands.Release(); }
    }
    public Task ApproveActionAsync(Lease lease, Guid approvalId) => DecideApprovalAsync(lease, approvalId, true);
    public async Task<bool> EnableHighRiskAsync(Lease lease, CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            TaskLeaseSession session;
            lock (_sync)
            {
                if (_progress?.Task is not { } task || task.Lease != lease || task.HighRiskEnabled ||
                    task.State != TaskState.WaitingUser || !task.CleanupComplete || !_loop.IsCompleted || _run is not null || _session is null ||
                    task.MessageCommitAttempted || task.LastError?.Code is not ("HIGH_IMPACT_MANUAL" or "MESSAGE_COMMIT_DISABLED" or "ACCOUNT_ENTRY_PERMISSION_REQUIRED")) return false;
                session = _session;
            }
            await session.InvalidateAsync();
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_session != session || _progress?.Task.Lease != lease || _progress.Task.State != TaskState.WaitingUser) return false;
                var task = _progress.Task;
                _actionApproval = null;
                _progress = _progress with { Task = task with { Epoch = session.Identity.Epoch, HighRiskEnabled = true, State = TaskState.Paused,
                    PendingQuestion = null, PendingActionApproval = null, LastError = null, ActionApprovalGranted = false },
                    Current = "本任务权限已启用", Next = "重新观察后继续；具体高风险操作仍需确认", Summary = "权限已更新，任务计划和进度已保留。", Revision = _progress.Revision + 1 };
            }
            Notify(); return true;
        }
        finally { _commands.Release(); }
    }
    public Task RejectActionAsync(Lease lease, Guid approvalId) => DecideApprovalAsync(lease, approvalId, false);
    private async Task DecideApprovalAsync(Lease lease, Guid approvalId, bool approved)
    {
        await _commands.WaitAsync();
        try
        {
            lock (_sync)
            {
                var task = _progress?.Task;
                if (task is null || task.Lease != lease || task.State != TaskState.AwaitingApproval || !task.HighRiskEnabled ||
                    !task.CleanupComplete || !_loop.IsCompleted || _run is not null || _session is null ||
                    task.PendingActionApproval?.Id != approvalId || _actionApproval?.Review.Id != approvalId)
                    throw new InvalidOperationException("STALE_ACTION_APPROVAL");
                if (approved) _actionApproval.ApprovedAt = DateTimeOffset.UtcNow;
                else _actionApproval = null;
                _progress = _progress! with { Task = task with { PendingActionApproval = null, PendingQuestion = null, State = TaskState.Paused },
                    Summary = approved ? "已批准这一步；重新观察并核对目标后才会执行。" : "已取消这一步，可开启新任务。", Revision = _progress.Revision + 1 };
            }
            Notify();
        }
        finally { _commands.Release(); }
    }
    public Task AddBudgetAsync(Guid taskId, TaskBudget additionalBudget)
    {
        if (additionalBudget.ActionLimit < 0 || additionalBudget.RequestLimit < 0 || additionalBudget.ActiveMsLimit < 0) throw new ArgumentException("INVALID_ADDITIONAL_BUDGET");
        lock (_sync)
        {
            if (_progress?.Task.Id != taskId || _run is not null || !_progress.Task.CleanupComplete || _session is null) throw new InvalidOperationException("PAUSE_BEFORE_BUDGET_CHANGE");
            var old = _progress.Task.Budget;
            var next = new TaskBudget(checked(old.ActionLimit + additionalBudget.ActionLimit), checked(old.RequestLimit + additionalBudget.RequestLimit), checked(old.ActiveMsLimit + additionalBudget.ActiveMsLimit));
            ValidateBudget(next);
            _progress = _progress with { Task = _progress.Task with { Budget = next }, Revision = _progress.Revision + 1 };
        }
        Notify(); return Task.CompletedTask;
    }
    public Task ApproveAsync(Lease lease, Guid transactionId) => throw new NotSupportedException("CONFIRMATION_EXECUTION_NOT_IMPLEMENTED");
    public Task RejectAsync(Lease lease, Guid transactionId) => PauseAsync(lease.TaskId);

    private bool IsLive(Run run) => ReferenceEquals(_run, run) && !run.Cancellation.IsCancellationRequested && _session is not null && _session.IsCurrent(run.Lease);
    private TaskContext Current(Run run)
    {
        lock (_sync) { if (!IsLive(run)) throw new OperationCanceledException(run.Cancellation.Token); return _progress!.Task; }
    }
    private void Update(Run run, Func<AgentProgress, AgentProgress> update)
    {
        lock (_sync)
        {
            if (!IsLive(run)) throw new OperationCanceledException(run.Cancellation.Token);
            var value = update(_progress!);
            _progress = value with { Task = value.Task with { Usage = value.Task.Usage with { ActiveMs = run.BaselineMs + run.Clock.ElapsedMilliseconds } }, Revision = value.Revision + 1 };
        }
        Notify();
    }
    private void Finish(Run run, TaskState state, string summary, string? code = null, ImmutableArray<Evidence> evidence = default)
    {
        Update(run, p => p with { Summary = summary, Evidence = evidence.IsDefault ? p.Evidence : evidence,
            Task = p.Task with { LastError = code is null ? null : new(code, summary) } });
        lock (_sync) { if (run.FinalState is null) run.FinalState = state; run.Clock.Stop(); }
    }
    private void OnCancelled(Run run)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_run, run)) return;
            _actionApproval = null;
            run.FinalState ??= _gate.Status.Reason switch
            { InputStopReason.Paused => TaskState.Paused, InputStopReason.Deadline or InputStopReason.HotkeysUnavailable or InputStopReason.InputFault => TaskState.Interrupted, _ => TaskState.Cancelled };
            run.Clock.Stop();
            if (run.InputInFlight && run.FinalState == TaskState.Paused) run.FinalState = TaskState.WaitingUser;
            if (run.ModelInFlight) _inputUsageKnown = _outputUsageKnown = false;
            _gate.Trip(run.FinalState == TaskState.Paused ? InputStopReason.Paused : InputStopReason.Stopped);
            _ = _session!.InvalidateAsync();
            _progress = _progress! with { Task = _progress.Task with { Epoch = _session.Identity.Epoch, State = TaskState.Pausing, PendingActionApproval = null,
                    Usage = _progress.Task.Usage with { InputTokens = _inputUsageKnown ? _progress.Task.Usage.InputTokens : null, OutputTokens = _outputUsageKnown ? _progress.Task.Usage.OutputTokens : null } }, Current = "输入已停止，正在收尾",
                Summary = run.InputInFlight ? "动作已中断，可能已部分执行。请核对当前界面再继续，程序不会自动重放。" :
                    _gate.Status.Reason == InputStopReason.Deadline ? "活动时间预算已用尽，已停止输入。" : _progress.Summary,
                Revision = _progress.Revision + 1 };
        }
        Notify();
    }

    private async Task LoopAsync(Run run)
    {
        using var stopped = run.Cancellation.Token.Register(() => OnCancelled(run));
        var ct = run.Cancellation.Token;
        Frame? frame = null, overview = null, precedingInjectedFrame = null;
        bool initialControlObservation = true;
        string? monitorId = null;
        int invalidReplies = 0, staleCount = 0, repeatedFailures = 0, hudRelocations = 0;
        string? lastAction = null;
        int recoveries = 0, recoveryRequest = 0;
        int reconsiderations = 0, controlFreeDecisions = 0;
        bool reconsideringNext = false;
        bool committedMessage = false;
        string? finalReviewFrame = null;
        int planErrors = 0;
        string? lastPlanStage = null;
        int messageReviewRetries = 0;
        int emptyReplyRetries = 0;
        bool providerRetryNext = false;
        string? searchContinuation = null;
        var blockedClicks = new List<(ForegroundIdentity? Window, long Generation, PhysicalPoint Point)>();
        bool Reconsider(Proposal proposal, string code, string detail)
        {
            var task = Current(run);
            if (task.Interpretation is null || reconsiderations >= 1 || task.Usage.ApiAttempts >= task.Budget.RequestLimit)
                return false;
            reconsiderations++;
            reconsideringNext = true;
            AddResult(run, Rejected(proposal, code) with { PublicSummary = "尚未执行。请结合原目标和已推断目标重新判断，优先合理默认值、其他可用入口或可靠控件；只有关键事实确实缺失才提问。上次困难：" + detail });
            Update(run, p => p with { Current = "正在重新理解目标并寻找可行做法", Next = "先由模型复核，必要时再向你提问" });
            frame = null;
            return true;
        }
        void AskForHelp(string code, string? reason = null)
        {
            var stalled = Current(run);
            if (!stalled.HighRiskEnabled && code is "HIGH_IMPACT_MANUAL" or "MESSAGE_COMMIT_DISABLED" or "ACCOUNT_ENTRY_PERMISSION_REQUIRED")
            {
                string permission = (reason ?? "当前操作需要额外的任务权限，尚未执行。") + " 点击“启用高风险并继续”可保留本任务接着处理；开启权限不等于批准具体提交。";
                Update(run, p => p with { Task = p.Task with { PendingQuestion = new(Guid.NewGuid(), permission, code) } });
                Finish(run, TaskState.WaitingUser, permission, code); return;
            }
            var stepId = stalled.PlanProgress.FirstOrDefault(s => s.Status == "active")?.StepId;
            var step = stalled.Interpretation?.Steps.FirstOrDefault(s => s.Id == stepId)?.Title;
            if (reason is null)
            {
                string feedback = stalled.RecentResults.LastOrDefault()?.PublicSummary ?? "重新观察后仍缺少可执行的目标信息。";
                reason = (step is null ? "当前步骤" : "步骤“" + step + "”") + "暂未完成（" + code + "）。" + feedback;
                if (reason.Length > 700) reason = reason[..700];
            }
            string question = reason is null
                ? "我重新核对后仍无法可靠完成这一步。请告诉我当前界面的正确控件或补充操作要求，也可以手动调整后回复“已调整”。"
                : reason + " 请手动处理这一步后回复“已调整”，或补充说明。当前任务会保留，回复不代表批准高影响操作。";
            Update(run, p => p with { Task = p.Task with { PendingQuestion = new(Guid.NewGuid(), question, code) } });
            Finish(run, TaskState.WaitingUser, question, code);
        }
        async Task<bool> RecoverAsync(Proposal proposal, string code, PhysicalPoint? point = null)
        {
            var current = Current(run);
            if (recoveries >= 2 || current.Usage.ApiAttempts >= current.Budget.RequestLimit)
            { AskForHelp(code); return false; }
            recoveryRequest = ++recoveries;
            AddResult(run, Rejected(proposal, code));
            Update(run, p => p with { Current = "正在重新核对控件和相邻区域", Next = recoveryRequest == 1 ? "局部观察并启用思考" : "进一步核对，必要时查阅软件资料" });
            var before = frame!;
            var timer = Stopwatch.StartNew();
            frame = await _desktop.CaptureAsync(run.Lease.Lease, monitorId!, null, ct);
            Current(run); overview = frame;
            if (point is { } center && SameObservationSurface(before, frame) && FrameChecks.Contains(frame.PhysicalRegion, center))
            {
                var bounds = frame.PhysicalRegion;
                int width = Math.Min(bounds.Width, Math.Max(320, bounds.Width / 2));
                int height = Math.Min(bounds.Height, Math.Max(240, bounds.Height / 2));
                int left = Math.Clamp(center.X - width / 2, bounds.Left, (int)bounds.Right - width);
                int top = Math.Clamp(center.Y - height / 2, bounds.Top, (int)bounds.Bottom - height);
                frame = await _desktop.CaptureAsync(run.Lease.Lease, monitorId!, new(left, top, width, height), ct);
                Current(run);
            }
            Update(run, p => p with { Timings = p.Timings with { ObservationMs = p.Timings.ObservationMs + timer.ElapsedMilliseconds } });
            return true;
        }
        async Task<bool> RelocateHudAsync(Proposal proposal, PhysicalRect excludedRegion)
        {
            // A window may have no safe corner, or relocation may not have changed its bounds.
            // Never consume the entire model budget trying the same unresolved obstruction.
            if (hudRelocations >= 2)
            {
                AskForHelp("HUD_OCCLUSION");
                return false;
            }
            await _overlay!.RelocateAsync(run.Lease.Lease, excludedRegion, ct);
            Current(run);
            hudRelocations++;
            AddResult(run, Rejected(proposal, "HUD_RELOCATED"));
            return true;
        }
        try
        {
            var executor = _input(run.Permit);
            while (true)
            {
                var context = Current(run);
                bool interpreting = _provider is ITaskIntentProvider && context.Interpretation is null;
                if (context.Usage.ApiAttempts >= context.Budget.RequestLimit) { Finish(run, TaskState.Interrupted, "模型请求预算已用尽，请核对后增加预算。", "REQUEST_BUDGET"); break; }
                monitorId ??= context.SelectedMonitorId;
                if (frame is null)
                {
                    if (_prepareDesktop is not null) { await _prepareDesktop(run.Permit, ct); Current(run); }
                    Update(run, p => p with { Current = "正在观察当前桌面", Next = "识别下一步目标" });
                    var timer = Stopwatch.StartNew();
                    var environment = await _desktop.GetEnvironmentAsync(ct);
                    Current(run);
                    if (!environment.Displays.Any(d => d.Id == monitorId))
                    { Finish(run, TaskState.Interrupted, "显示器已变化，请重新选择。", "MONITOR_UNAVAILABLE"); break; }
                    frame = await _desktop.CaptureAsync(run.Lease.Lease, monitorId, null, ct);
                    overview = frame;
                    Update(run, p => p with { Timings = p.Timings with { ObservationMs = p.Timings.ObservationMs + timer.ElapsedMilliseconds } });
                }
                var controlTimer = Stopwatch.StartNew();
                var controls = _controls is null ? ControlSnapshot.Empty(frame, ControlSnapshotStatus.Disabled) : await _controls.ObserveAsync(frame, ct);
                Current(run);
                controls.Validate(frame);
                Update(run, p => p with { Timings = p.Timings with { ControlsMs = p.Timings.ControlsMs + controlTimer.ElapsedMilliseconds } });
                // A real input grants one opportunity on its first post-input observation only.
                // Partial/empty can reflect an incomplete transition, but never proves that it does.
                var inputFrame = precedingInjectedFrame;
                precedingInjectedFrame = null;
                bool firstControlSurface = initialControlObservation;
                bool planned = context.Interpretation is { Steps.IsEmpty: false };
                if (!interpreting) initialControlObservation = false;
                if (_controlsRequired && !interpreting &&
                    (firstControlSurface && controls.Status == ControlSnapshotStatus.Partial ||
                     controls.Candidates.IsEmpty && controls.Status is (ControlSnapshotStatus.Partial or ControlSnapshotStatus.Unavailable or ControlSnapshotStatus.TimedOut) &&
                     inputFrame is not null && inputFrame.DisplayGeneration == frame.DisplayGeneration && inputFrame.MonitorId == frame.MonitorId))
                {
                    Update(run, p => p with { Current = "界面可能正在更新，稍候重新观察控件", Next = "核对新画面，尚未请求模型或输入" });
                    // A newly launched app can also have a cold UIA provider. Refresh once,
                    // binding the next read to the new surface; no old controls authorize input.
                    // Refresh the image too; never attach newer controls to the old image.
                    if (!firstControlSurface) await Task.Delay(500, ct);
                    Current(run);
                    var observationTimer = Stopwatch.StartNew();
                    var environment = await _desktop.GetEnvironmentAsync(ct);
                    Current(run);
                    if (FrameChecks.Validate(frame, run.Lease.Lease, environment, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90)) is not null)
                    {
                        if (planned) { frame = null; continue; } // Normal app/menu transitions need a new observation, not task termination.
                        Finish(run, TaskState.WaitingUser, "等待期间目标界面已变化，请核对后继续。", "ASSISTED_CONTROLS_REQUIRED"); break;
                    }
                    var previousFrame = frame;
                    frame = await _desktop.CaptureAsync(run.Lease.Lease, monitorId, null, ct);
                    Current(run);
                    Update(run, p => p with { Timings = p.Timings with { ObservationMs = p.Timings.ObservationMs + observationTimer.ElapsedMilliseconds } });
                    if (frame.Id == previousFrame.Id || frame.ViewKind != FrameViewKind.Overview ||
                        !planned && (!SameObservationSurface(previousFrame, frame) || frame.PhysicalRegion != previousFrame.PhysicalRegion))
                    { Finish(run, TaskState.WaitingUser, "重新观察时目标界面已变化，请核对后继续。", "ASSISTED_CONTROLS_REQUIRED"); break; }
                    overview = frame;
                    controlTimer.Restart();
                    controls = await _controls!.ObserveAsync(frame, ct);
                    Current(run);
                    controls.Validate(frame);
                    Update(run, p => p with { Timings = p.Timings with { ControlsMs = p.Timings.ControlsMs + controlTimer.ElapsedMilliseconds } });
                }
                if (planned && HasUsableControls(controls)) controlFreeDecisions = 0;
                bool verificationOnly = context.MessageCommitAttempted || committedMessage || !planned && _controlsRequired && !HasUsableControls(controls) &&
                    controls.Status is (ControlSnapshotStatus.Partial or ControlSnapshotStatus.Unavailable or ControlSnapshotStatus.TimedOut) &&
                    inputFrame is not null && SameObservationSurface(inputFrame, frame);
                bool navigationOnly = _controlsRequired && !HasUsableControls(controls) && !verificationOnly &&
                    context.Interpretation is not null && _controls is not null && controls.Status != ControlSnapshotStatus.Disabled && controlFreeDecisions < (planned ? 3 : 2);
                if (_controlsRequired && !HasUsableControls(controls) && !verificationOnly && !interpreting && !navigationOnly)
                {
                    AskForHelp("ASSISTED_CONTROLS_REQUIRED"); break;
                }
                Update(run, p => p with { Current = interpreting ? "正在理解你的目标并补全合理细节" : "正在判断下一步", Next = interpreting ? "模型先说明理解，再重新观察并执行" : "核对后执行一个动作",
                    Task = p.Task with { Usage = p.Task.Usage with { ApiAttempts = p.Task.Usage.ApiAttempts + 1 } } });
                context = Current(run);
                var scope = new ProposalScope(context.Id, context.Epoch, frame.Id, SchemaVersion: 2);
                var request = new ModelRequest(new(context.Lease, context.Goal, context.State, context.ProviderProfileFingerprint, context.SelectedMonitorId, context.Budget, context.Usage) { Clarifications = context.Clarifications, Interpretation = context.Interpretation, PlanProgress = context.PlanProgress, HighRiskEnabled = context.HighRiskEnabled },
                    frame, context.RecentResults, (_controlsRequired ? DesktopProtocolPrompt.V2Assisted : DesktopProtocolPrompt.V2) +
                        (verificationOnly ? "\nRESULT VERIFICATION ONLY: A real action has completed and only result verification is allowed now. Examine this NEW screenshot. You have ONE read-only reply: finish only if visible evidence supports the goal, otherwise ask_user or fail. ALL actions, control clicks, navigation, inspect and wait are disabled. Never resend or infer success from the expected effect." : "") +
                        (finalReviewFrame is not null ? "\nFINAL_GOAL_CHECK: You previously proposed completion. Independently check the ENTIRE original user goal and every planned completionCheck using this NEW image plus step reports tied to their observation frames. A prior claim or injected action is not proof. If any requested part remains undone, correct planUpdates and CONTINUE working on it; do not finish after merely opening a menu, searching or creating an unnamed folder. Finish succeeded only when every part including naming, contents, save/exit if requested is actually supported. Never resend a message." : "") +
                        (context.HighRiskEnabled ? "\nLOCAL TASK CONFIGURATION: HIGH_RISK_ENABLED=true. Navigate and prepare the user's requested task. Every high-impact commit still requires the application's explicit one-action approval. Only a message SEND proposal needs messageReview with the currently verified recipient and complete actual draft; entering an app/account page is not sending a message and must not invent a draft. Start/search remains ordinary navigation: use a reliable visible app shortcut or type in the focused search field; ENTER may open its matching app result outside messaging windows. Within messaging windows use control clicks for search results instead of ENTER. Ordinary replies are never approval. This flag does not permit commands, UAC, credential entry or unknown draft replacement." : "") +
                        (_actionApproval?.ApprovedAt is not null ? "\nONE ACTION WAS APPROVED: After checking this new image, re-propose exactly this step with the same messageReview if still correct; otherwise ask for a new confirmation. Review data: " + JsonSerializer.Serialize(_actionApproval.Review) : "") +
                        (navigationOnly ? "\nNAVIGATION ONLY: Controls are not available on this surface. You may use existing global navigation WIN, WIN+I, WIN+E, WIN+D, WIN+M or ALT+TAB to reach the task's ordinary app or desktop, or inspect/wait/report. All other input and control clicks are disabled until a fresh observation has usable controls. Choose the relevant app from the user's intent; do not ask the user to find a control before trying an ordinary entry point." : "") +
                        (providerRetryNext ? "\nRESPONSE FORMAT RECOVERY: The previous response produced no complete final operation JSON; no input was executed from that reply. Use this NEW image, preserve task progress, and return exactly one complete, concise JSON object in final content. Do not return only reasoning or an empty string. Follow the normal action, permission and verification rules; do not repeat an earlier input just because this response was empty." : "") +
                        (reconsideringNext ? "\nRECONSIDER ONCE: The previous proposed question or failed step is in recentResults. Re-evaluate the goal and current image with the saved interpretation and user constraints. Infer ordinary missing preferences, explain assumptions in current/next, and use a supported alternate route where possible. Do not repeat the same unavailable target. Never invent private/critical facts, bypass input restrictions or claim success from a plan. If the obstacle is genuinely unresolved, ask one specific question describing the missing fact." : ""), scope, frame.ViewKind == FrameViewKind.Crop ? overview : null, controls,
                    context.UntrustedModelObservations) { RecoveryLevel = recoveryRequest, SearchContinuation = searchContinuation, Reconsidering = reconsideringNext };
                recoveryRequest = 0;
                reconsideringNext = false;
                providerRetryNext = false;
                searchContinuation = null;
                var modelTimer = Stopwatch.StartNew();
                ProviderReply reply;
                lock (_sync) run.ModelInFlight = true;
                var progressSource = _provider as IModelProgressSource;
                void ModelProgress(string phase)
                {
                    try { Update(run, p => p with { Current = phase, Next = "等待完整结果后重新核对，暂停和停止始终有效" }); }
                    catch (OperationCanceledException) { } // Late stream events cannot revive a stopped task.
                }
                if (progressSource is not null) progressSource.Progress += ModelProgress;
                try { reply = interpreting ? await ((ITaskIntentProvider)_provider).InterpretAsync(request, ct) : await _provider.DecideAsync(request, ct); }
                catch (OperationCanceledException) { throw; }
                catch (ProviderCallException error)
                {
                    Current(run); // An error arriving after cancellation cannot change accounting or task state.
                    lock (_sync) run.ModelInFlight = false;
                    var usage = error.ResponseMetadata?.Usage ?? new ProviderUsage(null, null);
                    _inputUsageKnown &= usage.InputTokens.HasValue; _outputUsageKnown &= usage.OutputTokens.HasValue;
                    _inputTokens = checked(_inputTokens + (usage.InputTokens ?? 0)); _outputTokens = checked(_outputTokens + (usage.OutputTokens ?? 0));
                    Update(run, p => p with { Timings = p.Timings with { ModelMs = p.Timings.ModelMs + modelTimer.ElapsedMilliseconds },
                        Task = p.Task with { Usage = p.Task.Usage with { InputTokens = _inputUsageKnown ? _inputTokens : null, OutputTokens = _outputUsageKnown ? _outputTokens : null } } });
                    try { DiagnosticFault?.Invoke(error); } catch { }
                    string feedback = ExecutionFeedback.ModelFailure(error.Code);
                    AddResult(run, new("model-response", context.Id, context.Epoch, ActionStatus.Rejected, error.Code, DateTimeOffset.UtcNow,
                        modelTimer.ElapsedMilliseconds, 0, 0, feedback, frame.Id));
                    if (error.Code is "EMPTY_CONTENT" or "EMPTY_REASONING_ONLY" or "EMPTY_SEARCH_ANSWER" or "OUTPUT_TOKEN_LIMIT" &&
                        emptyReplyRetries++ < 1 && Current(run).Usage.ApiAttempts < Current(run).Budget.RequestLimit)
                    {
                        reconsideringNext = true; providerRetryNext = true; recoveryRequest = 0; searchContinuation = request.SearchContinuation;
                        Update(run, p => p with { Current = "模型本轮未给出可执行回复，正在重新请求", Next = "读取新画面后重试一次，尚未执行本轮输入" });
                        frame = null; continue;
                    }
                    Finish(run, TaskState.WaitingUser, feedback + " 点击继续可重新请求；任务计划已保留。", error.Code);
                    break;
                }
                finally { if (progressSource is not null) progressSource.Progress -= ModelProgress; }
                Current(run); // Late replies cannot update usage, state, or reach policy/input.
                emptyReplyRetries = 0;
                lock (_sync) run.ModelInFlight = false;
                _inputUsageKnown &= reply.Usage.InputTokens.HasValue; _outputUsageKnown &= reply.Usage.OutputTokens.HasValue;
                _inputTokens = checked(_inputTokens + (reply.Usage.InputTokens ?? 0)); _outputTokens = checked(_outputTokens + (reply.Usage.OutputTokens ?? 0));
                Update(run, p => p with { Timings = p.Timings with { ModelMs = p.Timings.ModelMs + modelTimer.ElapsedMilliseconds }, Task = p.Task with
                { Usage = p.Task.Usage with { InputTokens = _inputUsageKnown ? _inputTokens : null, OutputTokens = _outputUsageKnown ? _outputTokens : null } } });
                if (interpreting)
                {
                    try
                    {
                        if (reply.SearchContinuation is not null) throw new TaskInterpretationException();
                        var interpretation = TaskInterpretation.Parse(reply.Content);
                        Update(run, p => p with { Task = p.Task with { Interpretation = interpretation }, Current = "已理解目标，准备执行", Next = "重新观察当前桌面" });
                    }
                    catch (TaskInterpretationException error)
                    { Finish(run, TaskState.WaitingUser, "模型理解的回复格式无效，未执行操作。可继续重试。", error.Code); break; }
                    frame = null; continue; // Intent text never carries an action or authorizes input on its old frame.
                }
                if (reply.SearchContinuation is { } searchResult)
                {
                    if (request.RecoveryLevel != 2 || request.SearchContinuation is not null || searchResult.Length > 64 * 1024)
                    { AskForHelp("INVALID_SEARCH_REFERENCE"); break; }
                    if (Current(run).Usage.ApiAttempts >= Current(run).Budget.RequestLimit) { AskForHelp("REQUEST_BUDGET"); break; }
                    searchContinuation = searchResult;
                    Update(run, p => p with { Current = "已取得搜索结果，正在重新观察桌面", Next = "停止搜索，用当前界面核对下一步" });
                    frame = null; continue; // A separately budgeted decision sees a fresh frame and cannot search again.
                }
                Proposal proposal;
                try { proposal = ProposalParser.Parse(reply.Content, scope); }
                catch (ProtocolValidationException error)
                {
                    if (verificationOnly) { Finish(run, TaskState.WaitingUser, "最后核对的回复格式无效，请检查实际结果。", error.Code); break; }
                    if (++invalidReplies >= 2) { Finish(run, TaskState.WaitingUser, "模型连续返回无效动作格式，已停止。", error.Code); break; }
                    AddResult(run, new("invalid-reply", context.Id, context.Epoch, ActionStatus.Rejected, error.Code, DateTimeOffset.UtcNow, 0, 0, 0, "上一条JSON无效，请严格按协议生成新提议。", null));
                    frame = null; continue;
                }
                if (!_proposals.Add(proposal.ProposalId)) { Finish(run, TaskState.WaitingUser, "模型重复了已处理动作编号，未重放。", "DUPLICATE_PROPOSAL"); break; }
                invalidReplies = 0;
                if (navigationOnly)
                {
                    controlFreeDecisions++;
                    if (proposal.Decision is ControlActDecision || proposal.Decision is ActDecision navigation && !IsGlobalNavigation(navigation.Action))
                    { AskForHelp("ASSISTED_CONTROLS_REQUIRED", "当前没有可核对的控件，尚未执行点击或文字输入。"); break; }
                }
                if (verificationOnly && proposal.Decision is not (FinishDecision or AskUserDecision or FailDecision))
                { Finish(run, TaskState.WaitingUser, "控件信息不可用，只能核对已执行结果；未继续操作，请检查当前界面。", "RESULT_VERIFICATION_ONLY"); break; }
                // Parse has bound this report to the current task/epoch/frame; Update rejects late replies.
                // This is only model text. It neither approves this decision nor proves an action's expected effect.
                ImmutableArray<TaskStepReport> updatedPlan;
                try { updatedPlan = TaskPlanTracking.Apply(Current(run).Interpretation, Current(run).PlanProgress, proposal.PlanUpdates, frame); }
                catch (ArgumentException)
                {
                    AddResult(run, Rejected(proposal, "INVALID_PLAN_UPDATE"));
                    if (++planErrors >= 2) { AskForHelp("INVALID_PLAN_UPDATE", "模型的计划进度引用无效，未执行该动作。"); break; }
                    frame = null; continue;
                }
                planErrors = 0;
                Update(run, p => p with { Current = proposal.Current, Next = proposal.Next,
                    Task = p.Task with { PlanProgress = updatedPlan, UntrustedModelObservations = RememberObservation(p.Task.UntrustedModelObservations, proposal) } });
                string? currentPlanStage = updatedPlan.FirstOrDefault(s => s.Status == "active")?.StepId;
                if (currentPlanStage is not null && currentPlanStage != lastPlanStage)
                { reconsiderations = 0; recoveries = 0; lastPlanStage = currentPlanStage; }
                if (proposal.Decision is not FinishDecision) finalReviewFrame = null;
                // Check model-originated actions before resolving act_control to a physical click.
                if (_controlsRequired && proposal.Decision is ActDecision assistedAct)
                {
                    if (assistedAct.Action is MoveAction or ClickAction or ScrollAction or DragAction)
                    { Finish(run, TaskState.WaitingUser, "辅助核心版需要可靠控件定位，未执行模型给出的图像坐标。", "ASSISTED_COORDINATES_DISABLED"); break; }
                    if (assistedAct.Action is not (TextAction or ReplaceTextAction or HotkeyAction))
                    { Finish(run, TaskState.WaitingUser, "辅助核心版尚不支持此动作，请手动处理。", "ASSISTED_ACTION_UNSUPPORTED"); break; }
                    if (!IsGlobalNavigation(assistedAct.Action) && !DesktopNavigation.IsWindowClose(assistedAct.Action))
                    {
                        var focused = controls.Candidates.Where(c => c.Enabled && c.Focused && c.Focusable).ToArray();
                        if (assistedAct.Action is TextAction && frame.Foreground.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                            focused.Any(c => c.Role == "Edit" && !string.IsNullOrEmpty(c.CurrentValue)))
                        {
                            AddResult(run, Rejected(proposal, "TEXT_REPLACEMENT_REQUIRED") with { PublicSummary = "当前资源管理器输入框已有文字。普通text会追加或只覆盖部分选区。请核对完整currentValue及扩展名，用replace_existing写入完整目标值；若当前值已正确则无需重输。" });
                            if (++staleCount >= 3) { AskForHelp("TEXT_REPLACEMENT_REQUIRED"); break; }
                            frame = null; continue;
                        }
                        if (focused.Length == 0)
                        {
                            if (Reconsider(proposal, "ASSISTED_FOCUS_REQUIRED", "目标输入框未确认焦点。可先选择可靠的可聚焦控件，重新观察后再输入。")) continue;
                            AskForHelp("ASSISTED_FOCUS_REQUIRED", "目标输入框未确认焦点，尚未输入。"); break;
                        }
                        var focusTimer = Stopwatch.StartNew();
                        var freshControls = await _controls!.ObserveAsync(frame, ct);
                        Current(run);
                        freshControls.Validate(frame);
                        Update(run, p => p with { Timings = p.Timings with { ControlsMs = p.Timings.ControlsMs + focusTimer.ElapsedMilliseconds } });
                        if (!HasUsableControls(freshControls) || !freshControls.Candidates.Any(c => c.Enabled && c.Focused && c.Focusable &&
                            focused.Any(previous => previous.Name == c.Name && previous.Role == c.Role && previous.Bounds == c.Bounds)))
                        { Finish(run, TaskState.WaitingUser, "键盘焦点已变化或无法重新核对，未执行输入。", "ASSISTED_FOCUS_CHANGED"); break; }
                    }
                }
                if (proposal.Decision is ControlActDecision control)
                {
                    if (_controls is null || control.SnapshotId != controls.Id)
                    { Finish(run, TaskState.WaitingUser, "控件引用已失效，请重新观察。", "STALE_CONTROL_REFERENCE"); break; }
                    var hinted = controls.Candidates.FirstOrDefault(c => c.Id == control.ControlId);
                    if (hinted?.Role == "DesktopBackground" && (control.Button != MouseButton.Right || control.ClickCount != 1))
                    {
                        if (Reconsider(proposal, "DESKTOP_BACKGROUND_RIGHT_CLICK_ONLY", "桌面空白候选仅用于单次右键打开桌面菜单，不用于打开图标或连续点击。")) continue;
                        AskForHelp("DESKTOP_BACKGROUND_RIGHT_CLICK_ONLY", "桌面空白候选只能用于单次右键，未执行其他点击。"); break;
                    }
                    if (_controlsRequired && (hinted is null || !hinted.Enabled))
                    {
                        if (Reconsider(proposal, "ASSISTED_CONTROL_UNAVAILABLE", "上次选择的控件不存在或不可用；请从新画面选择可靠控件或其他入口。")) continue;
                        AskForHelp("ASSISTED_CONTROL_UNAVAILABLE", "模型选择的控件不存在或不可用，未执行点击。"); break;
                    }
                    if (hinted is not null && ControlMeaningConflict(control.Target, hinted.Name))
                    {
                        var center = new PhysicalPoint(hinted.Bounds.Left + hinted.Bounds.Width / 2, hinted.Bounds.Top + hinted.Bounds.Height / 2);
                        if (!await RecoverAsync(proposal, "CONTROL_MEANING_CONFLICT", center)) break;
                        continue;
                    }
                    if (_overlay is not null && hinted is not null && frame.OwnWindowRects.Any(r => FrameChecks.Overlaps(r, hinted.Bounds)))
                    {
                        if (!await RelocateHudAsync(proposal, hinted.Bounds)) break;
                        frame = null; continue;
                    }
                    var resolved = await _controls.ResolveAsync(frame, controls, control.ControlId, ct);
                    Current(run);
                    if (resolved.Point is not { } point)
                    {
                        AddResult(run, Rejected(proposal, resolved.ErrorCode ?? "CONTROL_UNAVAILABLE"));
                        if (++staleCount >= 2)
                        {
                            if (Current(run).Interpretation is { Steps.IsEmpty: false } &&
                                await RecoverAsync(proposal, resolved.ErrorCode ?? "CONTROL_UNAVAILABLE"))
                            { staleCount = 0; continue; }
                            AskForHelp("CONTROL_UNAVAILABLE", "目标控件连续未通过定位核对，重新观察后仍未找到可靠入口。"); break;
                        }
                        frame = null; continue;
                    }
                    var candidate = controls.Candidates.Single(c => c.Id == control.ControlId);
                    var normalized = new NormalizedPoint((point.X - frame.PhysicalRegion.Left) * 1000d / (frame.PhysicalRegion.Width - 1), (point.Y - frame.PhysicalRegion.Top) * 1000d / (frame.PhysicalRegion.Height - 1));
                    proposal = proposal with { Decision = new ActDecision(new ClickAction(normalized, control.Button, control.ClickCount), control.Target + " " + candidate.Name, control.Expected)
                        { MessageReview = control.MessageReview, VerifiedEditorPreparation = candidate.Role == "Edit" && candidate.Focusable && control.Button == MouseButton.Left && control.ClickCount == 1,
                            VerifiedMessageNavigation = control.Button == MouseButton.Left && control.ClickCount == 1 && HighRiskActions.IsMessageNavigationCandidate(candidate),
                            VerifiedAccountEntry = control.Button == MouseButton.Left && control.ClickCount == 1 && HighRiskActions.IsAccountEntryCandidate(frame.Foreground, candidate) } };
                }
                if (proposal.Decision is ActDecision { Action: ReplaceTextAction replacementPreparation } replaceAct &&
                    TextReplacement.Prepare(frame, controls, replacementPreparation, out _) is null or "REPLACEMENT_ALREADY_MATCHES")
                    proposal = proposal with { Decision = replaceAct with { VerifiedEditorPreparation = true } };
                var live = await _desktop.GetEnvironmentAsync(ct);
                var policyContext = Current(run);
                bool approvedAction = false;
                if (_actionApproval?.ApprovedAt is not null && proposal.Decision is ActDecision toApprove)
                {
                    approvedAction = _actionApproval.Matches(policyContext, frame, controls, toApprove, DateTimeOffset.UtcNow);
                    if (!approvedAction) _actionApproval = null;
                }
                var validation = _policy.Validate(policyContext with { ActionApprovalGranted = approvedAction }, frame, proposal, live);
                if (validation.Disposition == PolicyDisposition.Wait && validation.Code == "MESSAGE_REVIEW_REQUIRED" && messageReviewRetries++ < 2)
                {
                    AddResult(run, Rejected(proposal, validation.Code) with { PublicSummary = "尚未发送：请从当前图核对联系人和完整实际草稿，在该发送act/act_control上添加messageReview:{recipient,message}供用户确认。不以推断的草稿代替界面现状，也不要改成其他快捷键绕过确认。" });
                    frame = null; continue;
                }
                if (validation.Disposition == PolicyDisposition.Wait && validation.Code == "ACTION_APPROVAL_REQUIRED" && proposal.Decision is ActDecision pendingAct)
                {
                    _actionApproval = ActionApprovalBinding.Create(Current(run), frame, controls, pendingAct);
                    Update(run, p => p with { Task = p.Task with { PendingActionApproval = _actionApproval.Review, PendingQuestion = null } });
                    Finish(run, TaskState.AwaitingApproval, "等待你核对并批准这一步。批准前不会继续输入。", validation.Code); break;
                }
                if (validation.Disposition == PolicyDisposition.Wait) { AskForHelp(validation.Code, validation.PublicSummary); break; }
                if (validation.Disposition == PolicyDisposition.Reject)
                {
                    if (validation.Code == "OWN_WINDOW_TARGET" && _overlay is not null && proposal.Decision is ActDecision occupied)
                    {
                        if (!await RelocateHudAsync(proposal, ActionAvoidanceRegion(occupied.Action, frame.PhysicalRegion))) break;
                        frame = null; continue;
                    }
                    AddResult(run, Rejected(proposal, validation.Code));
                    if (++staleCount >= 2) { Finish(run, TaskState.WaitingUser, validation.PublicSummary, validation.Code); break; }
                    frame = null; continue;
                }
                switch (proposal.Decision)
                {
                    case ActDecision act:
                    {
                        context = Current(run);
                        if (approvedAction)
                        {
                            if (_actionApproval is null || !_actionApproval.Matches(context, frame, controls, act, DateTimeOffset.UtcNow))
                            { AskForHelp("ACTION_APPROVAL_EXPIRED"); return; }
                            _actionApproval.Used = true; _actionApproval = null; // Consume before any possible input; never replay after an uncertain result.
                        }
                        if (act.Action is ReplaceTextAction replacement)
                        {
                            var error = TextReplacement.Prepare(frame, controls, replacement, out var plan);
                            if (error == "REPLACEMENT_ALREADY_MATCHES")
                            { AddResult(run, Rejected(proposal, error) with { PublicSummary = "完整当前值已等于目标，无需再次输入。请从新画面核对后继续下一步。" }); frame = null; continue; }
                            if (error is not null)
                            { if (!await RecoverAsync(proposal, error)) return; continue; }
                            if (context.Usage.ActionsAttempted + 2 > context.Budget.ActionLimit)
                            { Finish(run, TaskState.Interrupted, "剩余动作预算不足以完成一次安全替换。", "ACTION_BUDGET"); return; }
                            async Task<ActionResult?> EditGesture(AgentAction action, Frame targetFrame, bool grant)
                            {
                                var local = proposal with { ProposalId = Guid.NewGuid().ToString("N"), FrameId = targetFrame.Id,
                                    Decision = new ActDecision(action, act.Target, act.Expected) { MessageReview = act.MessageReview, VerifiedEditorPreparation = act.VerifiedEditorPreparation } };
                                var localPolicy = _policy.Validate(Current(run) with { ActionApprovalGranted = grant }, targetFrame, local, await _desktop.GetEnvironmentAsync(ct));
                                Current(run);
                                if (localPolicy.Disposition != PolicyDisposition.Allow || localPolicy.Action is null) return null;
                                Update(run, p => p with { Task = p.Task with { Usage = p.Task.Usage with { ActionsAttempted = p.Task.Usage.ActionsAttempted + 1 } } });
                                var timer = Stopwatch.StartNew(); lock (_sync) run.InputInFlight = true;
                                var result = await executor.ExecuteAsync(context.Lease, localPolicy.Action, ct);
                                Current(run); lock (_sync) run.InputInFlight = false;
                                Update(run, p => p with { Timings = p.Timings with { InputMs = p.Timings.InputMs + timer.ElapsedMilliseconds } });
                                AddResult(run, WithAttemptContext(result, act));
                                return result;
                            }
                            var selected = await EditGesture(new HotkeyAction([AgentKey.CTRL, AgentKey.A]), frame, approvedAction);
                            if (selected?.Status != ActionStatus.Injected)
                            { AskForHelp(selected?.Code ?? "REPLACEMENT_SELECTION_REJECTED"); return; }
                            frame = await _desktop.CaptureAsync(run.Lease.Lease, monitorId!, null, ct); Current(run); overview = frame;
                            var selectedControls = await _controls!.ObserveAsync(frame, ct); Current(run);
                            error = TextReplacement.ValidateSelection(plan!, frame, selectedControls);
                            if (error is not null)
                            { if (!await RecoverAsync(proposal, error)) return; continue; }
                            var typed = await EditGesture(new TextAction(replacement.Text), frame, approvedAction);
                            if (typed?.Status != ActionStatus.Injected)
                            { AskForHelp(typed?.Code ?? "REPLACEMENT_INPUT_REJECTED"); return; }
                            frame = await _desktop.CaptureAsync(run.Lease.Lease, monitorId!, null, ct); Current(run); overview = frame;
                            var afterControls = await _controls.ObserveAsync(frame, ct); Current(run);
                            error = TextReplacement.VerifyResult(plan!, frame, afterControls);
                            AddResult(run, new(proposal.ProposalId, context.Id, context.Epoch, ActionStatus.Observed, error ?? "REPLACEMENT_VERIFIED", DateTimeOffset.UtcNow, 0, 0, 0,
                                error is null ? "完整输入框文字已核对，与替换目标一致；请继续核对文件名及后缀再确认。" : "替换后的完整文字与目标不符或无法核对。请根据新图和当前值重新判断，不要追加文字。", frame.Id));
                            if (error is not null && !await RecoverAsync(proposal, error)) return;
                            else if (error is null) { staleCount = 0; frame = null; }
                            continue;
                        }
                        var validated = validation.Action ?? throw new InvalidOperationException("NO_VALIDATED_ACTION");
                        string visualAction = VisualActionSignature(frame, validated);
                        PhysicalPoint? clickPoint = act.Action is ClickAction clicked ? InputCoordinates.ToPhysical(clicked.Point, frame.PhysicalRegion) : null;
                        bool blockedClick = clickPoint is { } location && blockedClicks.Any(b => b.Window == frame.Foreground &&
                            b.Generation == frame.DisplayGeneration && Math.Abs((long)b.Point.X - location.X) <= 2 && Math.Abs((long)b.Point.Y - location.Y) <= 2);
                        if (run.InjectedVisualActions.Contains(visualAction) || blockedClick)
                        {
                            if (!blockedClick && clickPoint is { } point) blockedClicks.Add((frame.Foreground, frame.DisplayGeneration, point));
                            if (!await RecoverAsync(proposal, "VISUAL_ACTION_LOOP", clickPoint)) return;
                            continue;
                        }
                        if (context.Usage.ActionsAttempted >= context.Budget.ActionLimit) { Finish(run, TaskState.Interrupted, "输入动作预算已用尽，请核对后增加预算。", "ACTION_BUDGET"); return; }
                        string signature = JsonSerializer.Serialize(act.Action, act.Action.GetType());
                        repeatedFailures = lastAction == signature ? repeatedFailures + 1 : 0;
                        lastAction = signature;
                        if (repeatedFailures >= 2) { if (!await RecoverAsync(proposal, "REPEATED_ACTION")) return; continue; }
                        Update(run, p => p with { Current = "正在执行：" + act.Target, Task = p.Task with { Usage = p.Task.Usage with { ActionsAttempted = p.Task.Usage.ActionsAttempted + 1 },
                            MessageCommitAttempted = p.Task.MessageCommitAttempted || approvedAction && act.MessageReview is not null && HighRiskActions.RequiresMessageReview(frame, act) } });
                        var timer = Stopwatch.StartNew();
                        lock (_sync) run.InputInFlight = true;
                        var result = await executor.ExecuteAsync(context.Lease, validated, ct);
                        Current(run);
                        lock (_sync) run.InputInFlight = false;
                        Update(run, p => p with { Timings = p.Timings with { InputMs = p.Timings.InputMs + timer.ElapsedMilliseconds } });
                        result = WithAttemptContext(result, act);
                        AddResult(run, result);
                        if (result.Status == ActionStatus.Injected && result.AppliedEventCount > 0)
                        {
                            run.InjectedVisualActions.Add(visualAction);
                            if (approvedAction && act.MessageReview is not null && HighRiskActions.RequiresMessageReview(frame, act)) committedMessage = true;
                        }
                        precedingInjectedFrame = _controlsRequired && result.Status == ActionStatus.Injected && result.AppliedEventCount > 0 ? frame : null;
                        if (result.Status is ActionStatus.Uncertain or ActionStatus.Cancelled)
                        { Finish(run, TaskState.WaitingUser, "动作可能部分执行，请核对桌面，程序不会自动重放。", result.Code ?? "INPUT_UNCERTAIN"); return; }
                        if (result.Status == ActionStatus.Rejected && ++staleCount >= 2)
                        { Finish(run, TaskState.WaitingUser, result.PublicSummary, result.Code); return; }
                        if (result.Status == ActionStatus.Injected) { staleCount = 0; hudRelocations = 0; }
                        frame = null; // The next model reply always sees a fresh post-input observation.
                        break;
                    }
                    case InspectDecision inspect:
                    {
                        var region = FrameChecks.MapRegion(inspect.Rect, frame.PhysicalRegion);
                        var timer = Stopwatch.StartNew();
                        frame = await _desktop.CaptureAsync(run.Lease.Lease, monitorId, region, ct);
                        Update(run, p => p with { Timings = p.Timings with { ObservationMs = p.Timings.ObservationMs + timer.ElapsedMilliseconds } });
                        // Keep this exact crop for the next request; no intervening overview capture.
                        break;
                    }
                    case SelectMonitorDecision select:
                        if (!live.Displays.Any(d => d.Id == select.MonitorId)) { Finish(run, TaskState.WaitingUser, "模型选择了不存在的显示器。", "MONITOR_UNAVAILABLE"); return; }
                        monitorId = select.MonitorId;
                        Update(run, p => p with { Task = p.Task with { SelectedMonitorId = monitorId } });
                        frame = null; break;
                    case WaitDecision wait: await Task.Delay(wait.Milliseconds, ct); frame = null; break;
                    case AskUserDecision ask:
                        if (!verificationOnly && !DesktopPolicyValidator.GoalNeedsManualHandling(context.Goal) &&
                            !DesktopPolicyValidator.NeedsManualHandling(ask.Reason + " " + ask.Question) && Reconsider(proposal, "QUESTION_RECONSIDER", ask.Reason + " " + ask.Question))
                            continue;
                        Update(run, p => p with { Task = p.Task with { PendingQuestion = new(Guid.NewGuid(), ask.Question, ask.Reason) } });
                        Finish(run, TaskState.WaitingUser, ask.Reason + "\n" + ask.Question); return;
                    case FinishDecision finish:
                    {
                        var finishing = Current(run);
                        if (finish.Outcome == FinishOutcome.Partial && !verificationOnly && finishing.Interpretation is { Steps.IsEmpty: false })
                        {
                            finalReviewFrame = null;
                            if (Reconsider(proposal, "PARTIAL_RECONSIDER", finish.Summary)) continue;
                            AskForHelp("PLAN_STILL_INCOMPLETE", finish.Summary); return;
                        }
                        if (finish.Outcome == FinishOutcome.Succeeded && finishing.Interpretation is { Steps.IsEmpty: false })
                        {
                            if (!TaskPlanTracking.AllCompleted(finishing.Interpretation, finishing.PlanProgress))
                            {
                                finalReviewFrame = null;
                                AddResult(run, Rejected(proposal, "PLAN_INCOMPLETE") with { PublicSummary = "仍有计划步骤未完成。请基于新图更新实际进度，完成原用户请求的全部部分；导航或搜索完成不等于任务完成。" });
                                frame = null; continue;
                            }
                            if (finalReviewFrame is null || finalReviewFrame == frame.Id)
                            {
                                finalReviewFrame = frame.Id;
                                AddResult(run, Rejected(proposal, "FINAL_GOAL_CHECK") with { PublicSummary = "准备从新画面核对整个目标，尚未宣告完成。" });
                                Update(run, p => p with { Current = "正在核对完整任务结果", Next = "逐项检查计划，再等待新的提示词" });
                                frame = null; continue;
                            }
                        }
                        Finish(run, finish.Outcome == FinishOutcome.Succeeded ? TaskState.Succeeded : TaskState.Partial, finish.Summary, evidence: finish.Evidence); return;
                    }
                    case FailDecision fail:
                        if (!verificationOnly && Reconsider(proposal, fail.Code, fail.Reason)) continue;
                        AskForHelp(fail.Code, fail.Reason); return;
                    default: Finish(run, TaskState.WaitingUser, "当前核心版尚未支持此操作，请手动处理。", "UNSUPPORTED_DECISION"); return;
                }
            }
        }
        catch (OperationCanceledException) { OnCancelled(run); }
        catch (Exception error)
        {
            try { DiagnosticFault?.Invoke(error); } catch { }
            try { Finish(run, TaskState.Interrupted, "观察或运行过程出现问题，输入已停止，请核对桌面。", "RUNTIME_ERROR"); } catch (OperationCanceledException) { OnCancelled(run); }
        }
        finally
        {
            // Cleanup closes the gate itself; that is not a user cancellation of a newly created review.
            stopped.Dispose();
            await CleanRunAsync(run);
        }
    }

    internal static bool ControlMeaningConflict(string target, string name)
    {
        bool parenthesis = target.Trim() is "(" or ")" || target.Contains("括号", StringComparison.Ordinal) || target.Contains("parenthes", StringComparison.OrdinalIgnoreCase);
        return parenthesis && (name.Trim() is "C" or "CE" || name.Contains("清除", StringComparison.Ordinal) || name.Equals("Clear", StringComparison.OrdinalIgnoreCase) || name.Equals("Clear entry", StringComparison.OrdinalIgnoreCase));
    }

    private static string VisualActionSignature(Frame frame, ValidatedAction validated)
    {
        if (validated.Lease != frame.Lease || validated.FrameId != frame.Id) throw new InvalidOperationException("STALE_VALIDATED_ACTION");
        PhysicalPoint Point(NormalizedPoint point) => InputCoordinates.ToPhysical(point, frame.PhysicalRegion);
        // Canonicalize pointer positions to the same physical pixels that the input runner uses.
        // Proposal/control/frame IDs and model prose are deliberately absent; they are not an action's effects.
        object action = validated.Action switch
        {
            MoveAction move => new { type = "move", point = Point(move.Point) },
            ClickAction click => new { type = "click", point = Point(click.Point), click.Button, click.ClickCount },
            DragAction drag => new { type = "drag", from = Point(drag.From), to = Point(drag.To), drag.DurationMs },
            ScrollAction scroll => new { type = "scroll", point = Point(scroll.Point), scroll.Delta },
            HotkeyAction hotkey => new { type = "hotkey", hotkey.Keys },
            TextAction text => new { type = "text", text.Text },
            ReplaceTextAction text => new { type = "replace_existing", text.Text, text.ExpectedCurrent },
            _ => throw new InvalidOperationException("UNSUPPORTED_VALIDATED_ACTION")
        };
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, frame.DisplayGeneration, frame.MonitorId, frame.PhysicalRegion, frame.Foreground,
            frame.Image.Width, frame.Image.Height, frame.Image.Mime, encodedLength = frame.Image.Bytes.Length, action
        });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(metadata);
        hash.AppendData(frame.Image.Bytes.Span);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static PhysicalRect ActionAvoidanceRegion(AgentAction action, PhysicalRect frame)
    {
        NormalizedPoint[] points = action switch
        {
            ClickAction c => [c.Point], MoveAction m => [m.Point], ScrollAction s => [s.Point], DragAction d => [d.From, d.To],
            _ => throw new ArgumentException("ACTION_HAS_NO_POINTER_TARGET")
        };
        var regions = points.Select(p => FrameChecks.Around(InputCoordinates.ToPhysical(p, frame), frame)).ToArray();
        int left = regions.Min(r => r.Left), top = regions.Min(r => r.Top);
        // For a drag, exclude both endpoints and the straight path between them.
        return new(left, top, checked((int)(regions.Max(r => r.Right) - left)), checked((int)(regions.Max(r => r.Bottom) - top)));
    }

    private static ActionResult WithAttemptContext(ActionResult result, Decision decision)
    {
        (string? Target, string? Expected) context = decision switch
        {
            ActDecision act => (act.Target, act.Expected),
            ControlActDecision control => (control.Target, control.Expected),
            _ => (null, null)
        };
        if (context.Target is null) return result;
        // These are the proposal's claims, not observations of the action's effect.
        return result with { PublicSummary = result.PublicSummary +
            $" 尝试目标（模型提议）：{context.Target}；预期效果（未验证）：{context.Expected}。须用当前新画面核对，预期不代表已观察结果。" };
    }
    private static ActionResult Rejected(Proposal proposal, string code) => WithAttemptContext(
        new(proposal.ProposalId, proposal.TaskId, proposal.Epoch, ActionStatus.Rejected, code, DateTimeOffset.UtcNow, 0, 0, 0,
            "本轮未执行输入。" + ExecutionFeedback.ForRejection(code), null), proposal.Decision);
    private static ImmutableArray<UntrustedModelObservation> RememberObservation(ImmutableArray<UntrustedModelObservation> history, Proposal proposal)
    {
        var appended = history.Add(new(new(proposal.TaskId, proposal.Epoch), proposal.FrameId, proposal.Current, proposal.Next));
        // Keep early reported baselines and the recent stage without extracting or certifying a setting value.
        return appended.Length <= UntrustedModelObservation.MaximumEntries ? appended :
            appended.Take(UntrustedModelObservation.PreservedInitialEntries)
                .Concat(appended.TakeLast(UntrustedModelObservation.MaximumEntries - UntrustedModelObservation.PreservedInitialEntries)).ToImmutableArray();
    }
    private void AddResult(Run run, ActionResult result) => Update(run, p => p with { Task = p.Task with { RecentResults = p.Task.RecentResults.Append(result).TakeLast(8).ToImmutableArray() } });
    private async Task CleanRunAsync(Run run)
    {
        run.Clock.Stop();
        lock (_sync) run.FinalState ??= TaskState.Interrupted;
        _gate.Trip(InputStopReason.Stopped);
        bool clean = true;
        var session = _session!;
        try { await session.InvalidateAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { clean = false; }
        var release = Stopwatch.StartNew();
        while (!_gate.TryRelease(run.Permit) && release.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        clean &= run.Permit.IsClosed;
        run.Worker.Dispose();
        bool terminal;
        lock (_sync)
        {
            run.Sealed = true;
            terminal = run.FinalState is TaskState.Succeeded or TaskState.Partial or TaskState.Failed or TaskState.Cancelled;
        }
        if (terminal)
        {
            try { await session.RequestCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { clean = false; }
            clean = clean && session.TryCompleteCleanup();
        }
        run.Cancellation.Dispose();
        lock (_sync)
        {
            var value = _progress!;
            _progress = value with { Task = value.Task with { Epoch = session.Identity.Epoch, State = run.FinalState!.Value, CleanupComplete = clean,
                Usage = value.Task.Usage with { ActiveMs = run.BaselineMs + run.Clock.ElapsedMilliseconds } },
                Current = clean ? "桌面控制已释放" : "输入已停止，清理尚未完成", Next = "",
                Summary = string.IsNullOrEmpty(value.Summary) ? (run.FinalState == TaskState.Paused ? "任务已暂停。" : "任务已停止。") : value.Summary,
                Revision = value.Revision + 1 };
            if (terminal && clean) _session = null;
            _run = null;
        }
        Notify();
    }
    private void Notify()
    {
        var progress = Progress;
        if (progress is null || Changed is null) return;
        foreach (Action<AgentProgress> subscriber in Changed.GetInvocationList()) { try { subscriber(progress); } catch { } }
    }
}
