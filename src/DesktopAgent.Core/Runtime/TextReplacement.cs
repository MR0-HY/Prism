using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Runtime;

/// <summary>Immutable local binding for one two-gesture replacement. Never an input permit.</summary>
public sealed record TextReplacementPlan(Lease Lease, string SourceFrameId, long DisplayGeneration,
    ForegroundIdentity Foreground, PhysicalRect EditorBounds, string EditorName, string ExpectedCurrent, string Text)
{
    public string? EditorIdentity { get; init; }
    public override string ToString() => $"TextReplacementPlan({Lease}, contents omitted)";
}

/// <summary>Checks full, read-only Edit values and selection between genuine keyboard gestures.
/// Production observations exclude password controls; unknown or truncated text is never replaceable.</summary>
public static class TextReplacement
{
    public static string? Prepare(Frame frame, ControlSnapshot? snapshot, ReplaceTextAction action, out TextReplacementPlan? plan)
    {
        plan = null;
        if (action.Text.Length is < 1 or > ProtocolLimits.ReplaceText || action.ExpectedCurrent.Length > ProtocolLimits.ReplaceText ||
            action.Text.Any(char.IsControl) || action.ExpectedCurrent.Any(char.IsControl)) return "REPLACEMENT_TEXT_INVALID";
        string? error = SnapshotError(frame, snapshot);
        if (error is not null) return error;
        if (snapshot!.Id != action.SnapshotId) return "REPLACEMENT_STALE_CONTROL_REFERENCE";
        var focused = FocusedInputTargets(snapshot).ToArray();
        if (focused.Length != 1 || focused[0].Id != action.ControlId) return "REPLACEMENT_FOCUS_NOT_UNIQUE";
        var editor = focused[0];
        if (editor.Role != "Edit" || !editor.Enabled || !editor.Focusable) return "REPLACEMENT_EDIT_REQUIRED";
        if (editor.ValueTruncated != false || editor.CurrentValue is null) return "REPLACEMENT_CURRENT_VALUE_UNAVAILABLE";
        if (!StringComparer.Ordinal.Equals(editor.CurrentValue, action.ExpectedCurrent)) return "REPLACEMENT_CURRENT_VALUE_CHANGED";
        if (StringComparer.Ordinal.Equals(editor.CurrentValue, action.Text)) return "REPLACEMENT_ALREADY_MATCHES";
        plan = new(frame.Lease, frame.Id, frame.DisplayGeneration, frame.Foreground, editor.Bounds, editor.Name, action.ExpectedCurrent, action.Text)
            { EditorIdentity = editor.LocalIdentity };
        return null;
    }

    public static string? ValidateSelection(TextReplacementPlan plan, Frame frame, ControlSnapshot? snapshot)
    {
        string? error = CurrentEditor(plan, frame, snapshot, out var editor);
        if (error is not null) return error;
        if (!StringComparer.Ordinal.Equals(editor!.Name, plan.EditorName)) return "REPLACEMENT_EDIT_CHANGED";
        if (!StringComparer.Ordinal.Equals(editor.CurrentValue, plan.ExpectedCurrent)) return "REPLACEMENT_CURRENT_VALUE_CHANGED";
        // UTF-16 offsets match System.String.Length, including surrogate pairs. Unknown selection is not full selection.
        if (editor.SelectionStart != 0 || editor.SelectionLength != plan.ExpectedCurrent.Length)
            return "REPLACEMENT_FULL_SELECTION_UNVERIFIED";
        return null;
    }

    public static string? VerifyResult(TextReplacementPlan plan, Frame frame, ControlSnapshot? snapshot)
    {
        string? error = CurrentEditor(plan, frame, snapshot, out var editor);
        if (error is not null) return error;
        // Some filename editors expose their current value as the accessible name; it can legitimately change here.
        return StringComparer.Ordinal.Equals(editor!.CurrentValue, plan.Text) ? null : "REPLACEMENT_RESULT_MISMATCH";
    }

    private static string? CurrentEditor(TextReplacementPlan plan, Frame frame, ControlSnapshot? snapshot, out ControlCandidate? editor)
    {
        editor = null;
        if (frame.Lease != plan.Lease || frame.Id == plan.SourceFrameId || frame.DisplayGeneration != plan.DisplayGeneration ||
            !FrameChecks.SameForeground(frame.Foreground, plan.Foreground)) return "REPLACEMENT_FRAME_CHANGED_OR_NOT_FRESH";
        string? error = SnapshotError(frame, snapshot);
        if (error is not null) return error;
        var focused = FocusedInputTargets(snapshot!).ToArray();
        if (focused.Length != 1) return "REPLACEMENT_FOCUS_NOT_UNIQUE";
        var current = focused[0];
        if (current.Role != "Edit" || !current.Enabled || !current.Focusable ||
            (plan.EditorIdentity is not null ? current.LocalIdentity != plan.EditorIdentity : current.Bounds != plan.EditorBounds))
            return "REPLACEMENT_EDIT_CHANGED";
        if (current.ValueTruncated != false || current.CurrentValue is null) return "REPLACEMENT_CURRENT_VALUE_UNAVAILABLE";
        editor = current;
        return null;
    }

    // Native Windows' standard system MenuBar provider can claim keyboard focus concurrently with the
    // real Edit. It is a structural container, not the text receiver. Ignore only this evidenced role;
    // an actual focused MenuItem, Button, second Edit or other focusable target still makes focus ambiguous.
    // Fresh complete-value and full-selection checks remain required after Ctrl+A.
    private static IEnumerable<ControlCandidate> FocusedInputTargets(ControlSnapshot snapshot) =>
        snapshot.Candidates.Where(c => c.Focused && c.Enabled && c.Focusable && c.Role != "MenuBar");

    private static string? SnapshotError(Frame frame, ControlSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Status is not (ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial))
            return "REPLACEMENT_CONTROLS_UNAVAILABLE";
        try { snapshot.Validate(frame); }
        catch (ArgumentException) { return "REPLACEMENT_STALE_CONTROL_REFERENCE"; }
        return null;
    }
}
