using System.Collections.Immutable;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Ports;

public interface IDesktopObserver
{
    Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? physicalRegion, CancellationToken ct);
    Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct);
}

public interface IControlObserver
{
    Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct);
    Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct);
}

// No input executor is ever passed to a provider.
public interface IModelProvider
{
    Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct);
    Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct);
}

/// <summary>Minted only by a local policy implementation; a parsed proposal alone cannot authorize input.</summary>
public sealed class ValidatedAction
{
    public Lease Lease { get; }
    public string FrameId { get; }
    public string ProposalId { get; }
    public AgentAction Action { get; }
    internal ValidatedAction(Lease lease, string frameId, string proposalId, AgentAction action) =>
        (Lease, FrameId, ProposalId, Action) = (lease, frameId, proposalId, action);
}

public interface IInputExecutor
{
    Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct);
}

public enum PolicyDisposition { Allow, Reject, Wait }
public sealed record PolicyResult(PolicyDisposition Disposition, string Code, string PublicSummary, ValidatedAction? Action);
public interface IPolicyValidator
{
    PolicyResult Validate(TaskContext context, Frame frame, Proposal proposal, DesktopEnvironment currentEnvironment);
}

public sealed record TaskStartRequest(string Goal, string ProviderProfileFingerprint, string MonitorId, TaskBudget Budget, long? InputGateRevision = null)
{
    public bool HighRiskEnabled { get; init; }
}
public interface ITaskCoordinator
{
    Task<TaskContext> StartAsync(TaskStartRequest request, CancellationToken ct);
    Task PauseAsync(Guid taskId);
    Task ResumeAsync(Guid taskId, CancellationToken ct);
    Task StopAsync(Guid taskId);
    Task CorrectAsync(Guid taskId, string goal);
    Task ApproveAsync(Lease lease, Guid transactionId);
    Task RejectAsync(Lease lease, Guid transactionId);
    Task AddBudgetAsync(Guid taskId, TaskBudget additionalBudget);
}

public sealed record OverlayState(Lease Lease, TaskState State, string GoalSummary, string Current, string Next);
public interface IOverlayController
{
    Task SetStateAsync(OverlayState state, CancellationToken ct);
    // Caller owns this scope and must dispose it in finally to restore decorations.
    Task<IAsyncDisposable> HideForCaptureAsync(Lease lease, CancellationToken ct);
    Task RelocateAsync(Lease lease, PhysicalRect excludedRegion, CancellationToken ct);
}

public interface IProfileStore
{
    Task<ImmutableArray<ProviderProfile>> ReadAsync(CancellationToken ct);
    Task SaveAsync(ProviderProfile profile, CancellationToken ct);
    Task DeleteAsync(Guid profileId, CancellationToken ct);
}
public interface ISecretStore
{
    // Caller owns/disposes the returned secret. Secret text must never enter event storage.
    Task<SecretValue?> ReadAsync(string secretRef, CancellationToken ct);
    Task WriteAsync(string secretRef, SecretValue value, CancellationToken ct);
    Task DeleteAsync(string secretRef, CancellationToken ct);
}

// Deliberately no generic request/body/image/goal/message payload in the event port.
public sealed record TaskEventData(string Code, string PublicSummary, string? ActionType,
    ActionStatus? ActionStatus, long? Count);
public sealed record TaskEvent(long Seq, Guid TaskId, long Epoch, DateTimeOffset AtUtc, string Type, TaskEventData Data);
public sealed record EventReplay(Guid TaskId, long LastSeq, ImmutableArray<TaskEvent> Events, bool IgnoredTruncatedTail);
public interface IEventStore
{
    Task AppendAsync(Lease lease, TaskEvent taskEvent, CancellationToken ct);
    Task<EventReplay> ReplayAsync(Guid taskId, CancellationToken ct);
}
