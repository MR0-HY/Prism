using System.Text.RegularExpressions;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Runtime;

internal static class HighRiskActions
{
    private static readonly HashSet<string> Messaging = new(StringComparer.OrdinalIgnoreCase)
    { "WeChat", "Weixin", "WXWork", "DingTalk", "Telegram", "WhatsApp", "OUTLOOK", "olk", "Teams", "ms-teams", "Discord", "Slack" };
    private static readonly Regex MessageNavigationName = new(
        @"搜索|通讯录|联系人|返回|取消|聊天|会话|\b(?:search|contacts?|back|cancel|chats?|conversations?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    internal static bool IsMessaging(ForegroundIdentity foreground) => Messaging.Contains(foreground.ProcessName);
    internal static bool IsAccountEntryCandidate(ForegroundIdentity foreground, ControlCandidate candidate) =>
        foreground.ProcessName is "WeChat" or "Weixin" && candidate.Enabled && candidate.Role == "Button" &&
        candidate.Name.Trim() == "进入微信";
    // Only a freshly resolved native candidate can mark a click as navigation. Model target prose has no authority.
    internal static bool IsMessageNavigationCandidate(ControlCandidate candidate) =>
        candidate.Enabled && !string.IsNullOrWhiteSpace(candidate.Name) && !DesktopPolicyValidator.NeedsManualHandling(candidate.Name) &&
        (candidate.Role is "ListItem" or "TreeItem" || candidate.Role is "Button" or "TabItem" && MessageNavigationName.IsMatch(candidate.Name));
    internal static bool SafeMessagePreparation(ActDecision act)
    {
        if (DesktopNavigation.IsGlobalNavigation(act.Action)) return true;
        if (act.VerifiedEditorPreparation) return true;
        // Unicode text contains no control/newline keys. Existing content replacement is a separate reviewed operation.
        if (act.Action is TextAction) return true;
        if (act.Action is HotkeyAction hotkey && (hotkey.Keys is [AgentKey.ESC] or [AgentKey.TAB] or [AgentKey.UP] or [AgentKey.DOWN] or [AgentKey.LEFT] or [AgentKey.RIGHT] or [AgentKey.CTRL, AgentKey.F])) return true;
        return act.VerifiedMessageNavigation && act.Action is ClickAction { Button: MouseButton.Left, ClickCount: 1 };
    }
    internal static bool RequiresMessageReview(Frame frame, ActDecision act) => IsMessaging(frame.Foreground) && !act.VerifiedAccountEntry && !SafeMessagePreparation(act);
    internal static bool RequiresApproval(TaskContext task, Frame frame, ActDecision act)
    {
        if (!task.HighRiskEnabled || DesktopNavigation.IsGlobalNavigation(act.Action)) return false;
        if (IsMessaging(frame.Foreground)) return !SafeMessagePreparation(act);
        return DesktopPolicyValidator.NeedsManualHandling(act.Target + " " + act.Expected) ||
            DesktopPolicyValidator.GoalNeedsManualHandling(task.Goal) || task.Clarifications.Any(c => DesktopPolicyValidator.GoalNeedsManualHandling(c.Answer)) ||
            act.Action is HotkeyAction keys && keys.Keys.Contains(AgentKey.DELETE);
    }
}
