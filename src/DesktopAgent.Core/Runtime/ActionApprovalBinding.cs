using System.Security.Cryptography;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Runtime;

// The model sees only the public review. The grant, expiry and observed binding stay local.
internal sealed class ActionApprovalBinding
{
    internal required PendingActionApproval Review { get; init; }
    internal required Guid TaskId { get; init; }
    internal required ForegroundIdentity Foreground { get; init; }
    internal required long DisplayGeneration { get; init; }
    internal required string MonitorId { get; init; }
    internal required PhysicalRect Region { get; init; }
    internal required string ActionDigest { get; init; }
    internal required string ControlsDigest { get; init; }
    internal DateTimeOffset? ApprovedAt { get; set; }
    internal bool Used { get; set; }

    internal static ActionApprovalBinding Create(TaskContext task, Frame frame, ControlSnapshot controls, ActDecision act) => new()
    {
        TaskId = task.Id, Foreground = frame.Foreground, DisplayGeneration = frame.DisplayGeneration,
        MonitorId = frame.MonitorId, Region = frame.PhysicalRegion,
        ActionDigest = DigestAction(act), ControlsDigest = DigestControls(controls),
        Review = new(Guid.NewGuid(), "请核对这一步的具体目标和内容；批准仅对本次动作有效。", act.Target, act.Expected, Describe(act.Action))
        { OriginalGoal = task.Goal, Recipient = act.MessageReview?.Recipient, Message = act.MessageReview?.Message }
    };

    internal bool Matches(TaskContext task, Frame frame, ControlSnapshot controls, ActDecision act, DateTimeOffset now) =>
        !Used && ApprovedAt is { } at && now >= at && now - at <= TimeSpan.FromSeconds(60) && task.Id == TaskId && task.Goal == Review.OriginalGoal && task.HighRiskEnabled &&
        FrameChecks.SameForeground(Foreground, frame.Foreground) && DisplayGeneration == frame.DisplayGeneration &&
        MonitorId == frame.MonitorId && Region == frame.PhysicalRegion && DigestAction(act) == ActionDigest && DigestControls(controls) == ControlsDigest;

    internal static string Describe(AgentAction action) => action switch
    {
        TextAction text => "输入文字：" + text.Text,
        ReplaceTextAction text => "替换为：" + text.Text + "\n原文字：" + text.ExpectedCurrent,
        HotkeyAction hotkey => "按键：" + string.Join("+", hotkey.Keys),
        ClickAction click => (click.Button == MouseButton.Right ? "右键" : "点击") + (click.ClickCount == 2 ? "两次" : "一次"),
        _ => "操作：" + action.GetType().Name
    };
    private static string DigestAction(ActDecision act) => Hash(JsonSerializer.Serialize(new
    { action = JsonSerializer.SerializeToElement(act.Action, act.Action.GetType()), act.Target, act.Expected, act.MessageReview }));
    // IDs/keyboard focus change after the approval HUD receives focus. Visible content, values and geometry must not.
    private static string DigestControls(ControlSnapshot controls) => Hash(JsonSerializer.Serialize(controls.Candidates
        .Select(c => new { c.Name, c.Role, c.Bounds, c.Enabled, c.ToggleState, c.Selected, c.CurrentValue, c.ValueTruncated, c.ExpandCollapseState })
        .OrderBy(c => c.Bounds.Top).ThenBy(c => c.Bounds.Left).ThenBy(c => c.Role).ThenBy(c => c.Name)));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
