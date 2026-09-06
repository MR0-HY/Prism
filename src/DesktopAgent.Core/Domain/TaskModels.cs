using System.Collections.Immutable;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Domain;

// Ready belongs to the application, not an active task.
public enum TaskState
{
    Running, Pausing, Paused, WaitingUser, PreparingMessage, AwaitingApproval,
    VerifyingCommit, VerifyingResult, Interrupted, Succeeded, Partial, Failed, Cancelled
}

public enum ActionStatus { Injected, Observed, Rejected, Uncertain, Cancelled }
public enum MessageStage
{
    Navigating, Focusing, Typing, CheckingDraft, AwaitingApproval, CommitCheck,
    Committing, CheckingResult, PartialDraft, Terminal
}
public enum MessageSendOutcome { Sent, NotSent, Uncertain }

public sealed record TaskBudget(int ActionLimit, int RequestLimit, long ActiveMsLimit)
{
    public static TaskBudget Default => new(30, 50, 600_000);
}
public sealed record TaskUsage(long ActionsAttempted, long ApiAttempts, long ActiveMs, long? InputTokens, long? OutputTokens);
public sealed record TaskError(string Code, string PublicSummary);
public sealed record UserQuestion(Guid Id, string Question, string Reason)
{
    public override string ToString() => $"UserQuestion({Id}, details omitted)";
}
public sealed record UserClarification(string Question, string Answer)
{
    public override string ToString() => "UserClarification(details omitted)";
}

public sealed record PendingActionApproval(Guid Id, string Summary, string Target, string Expected, string ActionDescription)
{
    public string OriginalGoal { get; init; } = "";
    public string? Recipient { get; init; }
    public string? Message { get; init; }
    public override string ToString() => $"PendingActionApproval({Id}, details omitted)";
}

public sealed record ActionResult(string ProposalId, Guid TaskId, long Epoch, ActionStatus Status,
    string? Code, DateTimeOffset StartedAtUtc, long ElapsedMs, int AppliedEventCount, int ExpectedEventCount,
    string PublicSummary, string? PostFrameId);

/// <summary>Sensitive task details, never an event/log DTO. D07 implements the transaction behavior.</summary>
public sealed record MessageTransaction(
    Guid Id, Guid TaskId, long Epoch, MessageStage Stage, string RecipientRequested, string? TextRequested,
    string? RecipientResolved, string? DraftText, string? DraftHash, ForegroundIdentity? ForegroundIdentity,
    NormalizedRect? RecipientRegion, NormalizedRect? DraftRegion, NormalizedRect? SendRegion,
    string? PreparedFrameId, string? ApprovalTokenHash, DateTimeOffset? ApprovedAt, DateTimeOffset? ExpiresAt,
    bool CommitUsed, MessageSendOutcome? SendOutcome)
{
    public override string ToString() => $"MessageTransaction({Id}, {Stage}, details omitted)";
}

public sealed record TaskContext(
    Guid Id, long Epoch, string Goal, DateTimeOffset CreatedAtUtc, TaskState State, bool CleanupComplete,
    string ProviderProfileFingerprint, string SelectedMonitorId, TaskBudget Budget, TaskUsage Usage,
    ImmutableArray<ActionResult> RecentResults, MessageTransaction? MessageTransaction, TaskError? LastError)
{
    public Lease Lease => new(Id, Epoch);
    public bool HighRiskEnabled { get; init; }
    public PendingActionApproval? PendingActionApproval { get; init; }
    public bool MessageCommitAttempted { get; init; }
    internal bool ActionApprovalGranted { get; init; }
    public ImmutableArray<UntrustedModelObservation> UntrustedModelObservations { get; init; } = [];
    public UserQuestion? PendingQuestion { get; init; }
    // Model interpretation is advisory context, never a user instruction or approval.
    public TaskInterpretation? Interpretation { get; init; }
    public ImmutableArray<TaskStepReport> PlanProgress { get; init; } = [];
    public ImmutableArray<UserClarification> Clarifications { get; init; } = [];
    public override string ToString() => $"TaskContext({Id}, epoch={Epoch}, {State}, details omitted)";
}

/// <summary>Immutable, minimal input to a provider. No secret or approval token is present.</summary>
public sealed record ModelTaskSnapshot(Lease Lease, string Goal, TaskState State,
    string ProviderProfileFingerprint, string SelectedMonitorId, TaskBudget Budget, TaskUsage Usage)
{
    public bool HighRiskEnabled { get; init; }
    public TaskInterpretation? Interpretation { get; init; }
    public ImmutableArray<TaskStepReport> PlanProgress { get; init; } = [];
    public ImmutableArray<UserClarification> Clarifications { get; init; } = [];
    public override string ToString() => $"ModelTaskSnapshot({Lease}, {State}, details omitted)";
}

public sealed class ModelRequest
{
    // Local recovery policy, never a model-supplied permission or provider profile mutation.
    public int RecoveryLevel { get; init; }
    public bool Reconsidering { get; init; }
    public string? SearchContinuation { get; init; }
    public ModelTaskSnapshot Task { get; }
    public Frame CurrentFrame { get; }
    public Frame? OverviewContext { get; }
    public ImmutableArray<ActionResult> RecentResults { get; }
    public ImmutableArray<UntrustedModelObservation> UntrustedModelObservations { get; }
    public string ProtocolPrompt { get; }
    public ProposalScope Scope { get; }
    public ControlSnapshot? Controls { get; }

    public ModelRequest(ModelTaskSnapshot task, Frame currentFrame, ImmutableArray<ActionResult> recentResults,
        string protocolPrompt, ProposalScope scope, Frame? overviewContext = null, ControlSnapshot? controls = null,
        ImmutableArray<UntrustedModelObservation> untrustedModelObservations = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(currentFrame);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolPrompt);
        task.Lease.EnsureValid();
        task.Interpretation?.Validate();
        TaskPlanTracking.Validate(task.Interpretation, task.PlanProgress, task.Lease);
        if (task.Clarifications.IsDefault || task.Clarifications.Length > 8 || task.Clarifications.Any(c => c is null ||
            string.IsNullOrWhiteSpace(c.Question) || c.Question.Length > ProtocolLimits.Question ||
            string.IsNullOrWhiteSpace(c.Answer) || c.Answer.Length > 1000 ||
            c.Answer.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))))
            throw new ArgumentException("Clarifications must contain bounded questions and user answers.");
        if (currentFrame.Lease != task.Lease || scope.TaskId != task.Lease.TaskId ||
            scope.Epoch != task.Lease.Epoch || scope.FrameId != currentFrame.Id)
            throw new ArgumentException("Request identities must all match the current frame.");
        if (recentResults.IsDefault || recentResults.Length > 8 ||
            recentResults.Any(r => r.TaskId != task.Lease.TaskId || r.Epoch > task.Lease.Epoch || r.Epoch < 0))
            throw new ArgumentException("History is limited to eight results from this task.");
        ImmutableArray<UntrustedModelObservation> observations = untrustedModelObservations.IsDefault ? [] : untrustedModelObservations;
        if (observations.Length > UntrustedModelObservation.MaximumEntries || observations.Any(o => o is null ||
            o.Lease.TaskId != task.Lease.TaskId || o.Lease.Epoch < 0 || o.Lease.Epoch > task.Lease.Epoch ||
            string.IsNullOrWhiteSpace(o.FrameId) || o.FrameId.Length > ProtocolLimits.FrameId || o.FrameId.Any(char.IsControl) ||
            o.Current is null || o.Current.Length > ProtocolLimits.StatusText || o.Current.Any(char.IsControl) ||
            o.Next is null || o.Next.Length > ProtocolLimits.StatusText || o.Next.Any(char.IsControl)))
            throw new ArgumentException("Untrusted model observations must be bounded reports from this task's current or earlier epochs.");
        if (overviewContext is not null && (currentFrame.ViewKind != FrameViewKind.Crop ||
            overviewContext.ViewKind != FrameViewKind.Overview || overviewContext.Id == currentFrame.Id ||
            overviewContext.Lease != task.Lease || overviewContext.DisplayGeneration != currentFrame.DisplayGeneration ||
            overviewContext.MonitorId != currentFrame.MonitorId ||
            !overviewContext.PhysicalRegion.Contains(currentFrame.PhysicalRegion)))
            throw new ArgumentException("Only a matching overview may accompany the current crop.");
        if (controls is not null)
        {
            if (scope.SchemaVersion != 2) throw new ArgumentException("Control snapshots require explicit v2 scope.");
            controls.Validate(currentFrame);
        }
        Controls = controls;
        UntrustedModelObservations = observations;
        (Task, CurrentFrame, RecentResults, ProtocolPrompt, Scope, OverviewContext) =
            (task, currentFrame, recentResults, protocolPrompt, scope, overviewContext);
    }

    public override string ToString() => $"ModelRequest(task={Task.Lease.TaskId}, frame={CurrentFrame.Id}, content omitted)";
}
