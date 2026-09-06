using System.Collections.Immutable;

namespace DesktopAgent.Core.Protocol;

/// <summary>The locally selected reply shape. The model cannot choose its own transaction stage.</summary>
public enum ProposalRequestKind { General, DraftFocusCheck, DraftCheck, CommitCheck, CommitResult }

public sealed record ProposalScope(Guid TaskId, long Epoch, string FrameId,
    ProposalRequestKind RequestKind = ProposalRequestKind.General, int SchemaVersion = 1);

public sealed record Proposal(int SchemaVersion, string ProposalId, Guid TaskId, long Epoch,
    string FrameId, string Current, string Next, Decision Decision)
{
    public ImmutableArray<PlanStepUpdate> PlanUpdates { get; init; } = [];
}
public sealed record PlanStepUpdate(string StepId, string Status, string Observation);

public readonly record struct NormalizedPoint(double X, double Y);

/// <summary>Normalized boundaries; the right and bottom edge are exclusive when mapped to pixels.</summary>
public readonly record struct NormalizedRect(double X0, double Y0, double X1, double Y1)
{
    // A protocol-level geometric filter only. Point and rectangle pixel mappings differ;
    // the live policy must map both using the actual frame and verify physical containment again.
    // Interior right/bottom boundaries are excluded; 1000 still denotes the image's last pixel.
    public bool Contains(NormalizedPoint point) =>
        point.X >= X0 && (point.X < X1 || (X1 == 1000 && point.X == 1000)) &&
        point.Y >= Y0 && (point.Y < Y1 || (Y1 == 1000 && point.Y == 1000));

    public bool Overlaps(NormalizedRect other) =>
        X0 < other.X1 && X1 > other.X0 && Y0 < other.Y1 && Y1 > other.Y0;
}

public sealed record Evidence(string FrameId, NormalizedRect? Region, string AppName,
    string ObservedText, string Interpretation, string? SourceUrl, string? Uncertainty);

public abstract record Decision;
/// <summary>Untrusted model transcription for a local final-confirmation card; never an approval.</summary>
public sealed record MessageReview(string Recipient, string Message)
{
    public override string ToString() => "MessageReview(contents omitted)";
}
public sealed record ActDecision(AgentAction Action, string Target, string Expected) : Decision
{
    // Set only after local control resolution; the model cannot grant this through JSON.
    internal bool VerifiedEditorPreparation { get; init; }
    internal bool VerifiedMessageNavigation { get; init; }
    internal bool VerifiedAccountEntry { get; init; }
    public MessageReview? MessageReview { get; init; }
}
public sealed record ControlActDecision(string SnapshotId, string ControlId, MouseButton Button, int ClickCount,
    string Target, string Expected) : Decision
{
    public MessageReview? MessageReview { get; init; }
}
public sealed record InspectDecision(NormalizedRect Rect) : Decision;
public sealed record SelectMonitorDecision(string MonitorId) : Decision;
public sealed record WaitDecision(int Milliseconds, string Reason) : Decision;
public sealed record AskUserDecision(string Reason, string Question) : Decision;
public sealed record PrepareMessageDecision(string Recipient, string Text,
    NormalizedRect RecipientRegion, NormalizedRect DraftRegion, NormalizedRect SendRegion,
    NormalizedPoint DraftPoint, AgentAction SendAction) : Decision;
public enum FinishOutcome { Succeeded, Partial }
public sealed record FinishDecision(FinishOutcome Outcome, string Summary,
    ImmutableArray<Evidence> Evidence) : Decision;
public sealed record FailDecision(string Code, string Reason) : Decision;
public sealed record DraftFocusCheckDecision(bool RecipientMatches, bool DraftEmpty,
    bool FocusInDraft, string Reason) : Decision;
public sealed record DraftCheckDecision(bool RecipientMatches, bool DraftMatches, string Reason) : Decision;
public sealed record CommitCheckDecision(bool RecipientMatches, bool DraftMatches,
    NormalizedRect? SendRegion, AgentAction? SendAction, string Reason) : Decision;
public enum CommitStatus { Sent, NotSent, Uncertain }
public sealed record CommitResultDecision(CommitStatus Status, ImmutableArray<Evidence> Evidence,
    string Reason) : Decision;

public abstract record AgentAction;
public sealed record MoveAction(NormalizedPoint Point) : AgentAction;
public enum MouseButton { Left, Right }
public sealed record ClickAction(NormalizedPoint Point, MouseButton Button, int ClickCount) : AgentAction;
public sealed record DragAction(NormalizedPoint From, NormalizedPoint To, int DurationMs) : AgentAction;
public sealed record ScrollAction(NormalizedPoint Point, int Delta) : AgentAction;
public sealed record HotkeyAction(ImmutableArray<AgentKey> Keys) : AgentAction;
public sealed record TextAction(string Text) : AgentAction;
/// <summary>Bound request to replace one current Edit value. The coordinator must verify selection
/// between Ctrl+A and native text input; this is not a low-level gesture.</summary>
public sealed record ReplaceTextAction(string Text, string ExpectedCurrent, string SnapshotId, string ControlId) : AgentAction;

public enum AgentKey
{
    CTRL, ALT, SHIFT, WIN, ENTER, ESC, TAB, SPACE, BACKSPACE, DELETE,
    HOME, END, PAGEUP, PAGEDOWN, UP, DOWN, LEFT, RIGHT,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9
}

public static class ProtocolLimits
{
    public const int JsonUtf8Bytes = 64 * 1024;
    public const int JsonDepth = 16;
    public const int Id = 80;
    public const int FrameId = 128;
    public const int MonitorId = 128;
    public const int StatusText = 200;
    public const int Text = 1000;
    public const int ReplaceText = 512;
    public const int Recipient = 200;
    public const int Reason = 1000;
    public const int Question = 1000;
    public const int Summary = 2000;
    public const int EvidenceAppName = 200;
    public const int EvidenceText = 2000;
    public const int SourceUrl = 2048;
    public const int Uncertainty = 1000;
}

/// <summary>Only a fixed code and schema path are exposed, never the model's raw response.</summary>
public sealed class ProtocolValidationException : Exception
{
    public ProtocolValidationException(string code, string path)
        : base($"Protocol validation failed ({code}) at {path}.")
    {
        Code = code;
        Path = path;
    }

    public string Code { get; }
    public string Path { get; }
}
