using System.Collections.Immutable;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Domain;

public enum ControlSnapshotStatus { Available, Partial, Unavailable, TimedOut, Disabled }
public sealed record ControlCandidate(string Id, string? ParentId, string Name, string Role, PhysicalRect Bounds,
    bool Enabled, bool Focused, bool Focusable, string? ToggleState, bool? Selected)
{
    public const int MaximumValueLength = 512;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LocalIdentity { get; init; }
    public string? CurrentValue { get; init; }
    public bool? ValueTruncated { get; init; }
    public int? SelectionStart { get; init; }
    public int? SelectionLength { get; init; }
    public string? ExpandCollapseState { get; init; }
    public bool? HasSubmenu { get; init; }
    internal bool HasValidMetadata() =>
        (CurrentValue is null ? ValueTruncated is null or true :
            Role == "Edit" && ValueTruncated == false && CurrentValue.Length <= MaximumValueLength && ValidText(CurrentValue)) &&
        (ValueTruncated != true || Role == "Edit") &&
        (SelectionStart is null && SelectionLength is null || CurrentValue is not null && ValueTruncated == false &&
            SelectionStart is >= 0 && SelectionLength is >= 0 && (long)SelectionStart.Value + SelectionLength.Value <= CurrentValue.Length) &&
        ExpandCollapseState is null or "collapsed" or "expanded" or "partiallyExpanded" or "leaf" &&
        (HasSubmenu is null || Role == "MenuItem") &&
        (HasSubmenu is null || ExpandCollapseState is null || HasSubmenu == (ExpandCollapseState != "leaf"));

    private static bool ValidText(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\0') return false;
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
    public override string ToString() => $"ControlCandidate({Id}, text omitted)";
}
public sealed record ControlSnapshot(string Id, Lease Lease, string FrameId, DateTimeOffset CapturedAtUtc,
    ControlSnapshotStatus Status, ImmutableArray<ControlCandidate> Candidates)
{
    public const int MaximumCandidates = 96;
    public void Validate(Frame frame)
    {
        if (!Guid.TryParseExact(Id, "N", out _) || Lease != frame.Lease || FrameId != frame.Id || !Enum.IsDefined(Status) ||
            Candidates.IsDefault || Candidates.Length > MaximumCandidates || Candidates.Select(c => c.Id).Distinct().Count() != Candidates.Length ||
            ((Status is not (ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial)) && !Candidates.IsEmpty))
            throw new ArgumentException("INVALID_CONTROL_SNAPSHOT");
        var ids = Candidates.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (Candidates.Sum(c => c.Name.Length + (c.CurrentValue?.Length ?? 0)) > 12000 || Candidates.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 32 ||
            c.Name.Length > 128 || c.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(c.Role) || c.Role.Length > 40 ||
            (c.ParentId is not null && (c.ParentId == c.Id || !ids.Contains(c.ParentId))) || !frame.PhysicalRegion.Contains(c.Bounds) ||
            c.ToggleState is not (null or "on" or "off" or "indeterminate") || !c.HasValidMetadata())) throw new ArgumentException("INVALID_CONTROL_CANDIDATE");
    }
    public static ControlSnapshot Empty(Frame frame, ControlSnapshotStatus status) => new(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow, status, []);
    public override string ToString() => $"ControlSnapshot({Id}, {Status}, {Candidates.Length} candidates; text omitted)";
}
public sealed record ControlResolution(PhysicalPoint? Point, string? ErrorCode);
