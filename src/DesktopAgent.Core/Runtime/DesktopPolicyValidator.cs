using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using System.Text.RegularExpressions;

namespace DesktopAgent.Core.Runtime;

/// <summary>High-impact actions require an enabled task and a separately bound, explicit one-action grant.</summary>
public sealed class DesktopPolicyValidator(TimeSpan frameTtl, int ownProcessId = 0) : IPolicyValidator
{
    private static readonly string[] RiskTerms = ["付款", "支付", "购买", "下单", "转账", "交易", "删除", "格式化", "恢复出厂", "重置", "卸载", "安装",
        "密码", "账户", "账号", "帐号", "权限", "管理员", "防火墙", "安全中心", "隐私", "杀毒", "网络", "代理", "飞行模式", "发送", "发出", "提交消息",
        "payment", "purchase", "buy now", "checkout", "transfer money", "delete", "format drive", "reset", "uninstall", "install", "password", "account",
        "permission", "administrator", "firewall", "antivirus", "privacy", "network", "proxy", "wi-fi", "wifi", "vpn", "airplane mode", "send", "submit message"];
    private static readonly HashSet<string> RestrictedProcesses = new(StringComparer.OrdinalIgnoreCase)
    { "cmd", "powershell", "pwsh", "WindowsTerminal", "regedit", "mmc", "Taskmgr", "msiexec", "SystemSettingsAdminFlows",
      "SecurityHealthSystray", "SecHealthUI", "CredentialUIBroker", "consent", "LogonUI" };
    public static bool NeedsManualHandling(string text) => RiskTerms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    // Only complete user prohibition clauses are excluded. Proposed actions remain strict.
    // This intentionally does not attempt to infer arbitrary natural-language negation.
    private static readonly Regex FileDeletionProhibition = new(
        @"(^|[，,。.;；!！?？\r\n])\s*(?:请)?(?:不要|不|禁止)(?:编辑或|编辑和)?删除(?:任何|这些|该|测试)?文件\s*(?=$|[，,。.;；!！?？\r\n])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex MessageSendingProhibition = new(
        @"(^|[，,。.;；!！?？、\r\n])\s*(?:请)?(?:不要|不|禁止)发送(?:任何)?(?:内容|消息)\s*(?=$|[，,。.;；!！?？、\r\n])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    internal static bool GoalNeedsManualHandling(string goal) =>
        NeedsManualHandling(MessageSendingProhibition.Replace(FileDeletionProhibition.Replace(goal, "$1"), "$1"));
    // A close verb and its object must belong to the same clause. Otherwise "关闭自动换行，查看窗口"
    // incorrectly grants closing the window merely because a later clause mentions one.
    private const string CloseClauseCharacter = @"[^，,。.;；!！?？\r\n]";
    private static readonly Regex CloseRequested = UserPattern(
        @"(?:关闭|关掉|关上|退出)" + CloseClauseCharacter + @"{0,12}(?:窗口|程序|应用|设置|计算器|记事本|浏览器|编辑器|文档|标签)|(?:窗口|程序|应用|设置|计算器|记事本|浏览器|编辑器|文档|标签)(?:给|先)?(?:关闭|关掉|关上)|(?:完成|保存|存盘|做完)" + CloseClauseCharacter + @"{0,6}(?:关闭|关掉|退出)|(?:^|[，,。；;])\s*(?:然后|再|最后)?\s*退出(?:$|[，,。；;])|\b(?:close|exit|quit)\b" + CloseClauseCharacter + @"{0,24}\b(?:window|app|application|program|settings|calculator|notepad|editor|document|file|tab)\b|\b(?:save|finish|done)\b" + CloseClauseCharacter + @"{0,24}\b(?:close|exit|quit)\b|\b(?:and|then)\s+(?:exit|quit)\s*[.!。]?$|^\s*(?:exit|quit)\s*[.!。]?$");
    private static readonly Regex SaveRequested = UserPattern(
        @"保存|另存为|存盘|(?:写入|创建|新建|编辑|修改).{0,24}(?:文件(?!夹)|文档|文本|txt\b)|\b(?:save|write|create|edit)\b.{0,40}\b(?:file|document|text|txt|notepad)\b|\bsave\b");
    private static readonly Regex CloseProhibited = UserPattern(
        @"(?:不要|别|禁止|无需|勿|不必|暂不|不能|不).{0,8}(?:关闭|关掉|关上|退出)|保持.{0,12}(?:窗口|程序|应用).{0,6}(?:打开|开启)|\b(?:not|never|without|avoid|don['’]t|do\s+not)\b.{0,16}\b(?:clos\w*|exit\w*|quit\w*)\b|\b(?:keep|leave)\b.{0,24}\b(?:window|app|application|program)\b.{0,12}\bopen\b");
    private static readonly Regex SaveProhibited = UserPattern(
        @"(?:不要|别|禁止|无需|勿|不必|暂不|不能|不).{0,8}(?:保存|另存为|存盘|写入|创建|新建|编辑|修改)|\b(?:not|never|without|avoid|don['’]t|do\s+not)\b.{0,16}\b(?:sav\w*|writ\w*|creat\w*|edit\w*)\b");
    private static Regex UserPattern(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static bool UserRequested(TaskContext context, Regex requested, Regex prohibited)
    {
        // Only actual user text can authorize these shortcuts. A model's interpretation never does.
        string[] userText = [context.Goal, .. context.Clarifications.Select(c => c.Answer)];
        return !userText.Any(prohibited.IsMatch) && userText.Any(requested.IsMatch);
    }
    public PolicyResult Validate(TaskContext context, Frame frame, Proposal proposal, DesktopEnvironment currentEnvironment)
    {
        PolicyResult Reject(string code, string message) => new(PolicyDisposition.Reject, code, message, null);
        PolicyResult Wait(string code, string message) => new(PolicyDisposition.Wait, code, message, null);
        if (context.State != TaskState.Running || context.CleanupComplete || context.Lease != frame.Lease ||
            proposal.TaskId != context.Id || proposal.Epoch != context.Epoch || proposal.FrameId != frame.Id)
            return Reject("STALE_PROPOSAL", "提议已过期，需重新观察。");
        string? stale = FrameChecks.Validate(frame, context.Lease, currentEnvironment, DateTimeOffset.UtcNow, frameTtl);
        if (stale is not null) return Reject(stale, "当前桌面已变化，需重新观察。");
        if (ownProcessId > 0 && frame.Foreground.ProcessId == ownProcessId) return Wait("SELF_FOREGROUND", "请切换到需要操作的软件，再继续。");
        if (proposal.Decision is PrepareMessageDecision or DraftFocusCheckDecision or DraftCheckDecision or CommitCheckDecision or CommitResultDecision)
            return Wait("MESSAGE_AUTOMATION_DISABLED", "消息确认事务尚未启用，请手动处理发送。");
        if (proposal.Decision is ControlActDecision) return Reject("CONTROL_NOT_RESOLVED", "控件必须先重新定位再执行。");
        if (proposal.Decision is not ActDecision act)
        {
            if (proposal.Decision is FinishDecision finish && (finish.Evidence.IsDefaultOrEmpty || finish.Evidence.Any(e => e.FrameId != frame.Id)))
                return Reject("MISSING_CURRENT_EVIDENCE", "缺少当前画面的完成证据。");
            return new(PolicyDisposition.Allow, "OBSERVATION_ONLY", "观察提议通过。", null);
        }
        if (!context.HighRiskEnabled && (GoalNeedsManualHandling(context.Goal) || context.Interpretation is { } interpretation && GoalNeedsManualHandling(interpretation.Goal) ||
            context.Clarifications.Any(c => GoalNeedsManualHandling(c.Answer)) || NeedsManualHandling(act.Target + " " + act.Expected)))
            return Wait("HIGH_IMPACT_MANUAL", "当前操作需要额外的任务权限，尚未执行。可启用高风险操作后继续，具体提交仍需最终确认。");
        if (RestrictedProcesses.Contains(frame.Foreground.ProcessName))
            return Wait("APPLICATION_REQUIRES_MANUAL", "此应用涉及命令、管理或消息操作，当前核心版请手动处理。");
        string? inputText = act.Action switch { TextAction text => text.Text, ReplaceTextAction text => text.Text, _ => null };
        if (inputText is not null)
        {
            string[] executableText = ["powershell", "pwsh", "cmd.exe", "reg.exe", "mshta", "wscript", "cscript", "rundll32", "schtasks", "javascript:", "data:text/html"];
            if (executableText.Any(t => inputText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return Wait("EXECUTABLE_TEXT_DISABLED", "不执行命令或脚本，请手动处理。");
        }
        if (act.Action is HotkeyAction keys)
        {
            bool closeChord = keys.Keys.Contains(AgentKey.ALT) && keys.Keys.Contains(AgentKey.F4) ||
                keys.Keys.Contains(AgentKey.CTRL) && keys.Keys.Contains(AgentKey.W);
            bool saveChord = keys.Keys.Contains(AgentKey.CTRL) && keys.Keys.Contains(AgentKey.S);
            bool ordinaryWindow = DesktopNavigation.IsOrdinaryWindow(frame.Foreground);
            if (keys.Keys.Contains(AgentKey.DELETE) && !context.HighRiskEnabled ||
                keys.Keys.Contains(AgentKey.WIN) && !DesktopNavigation.IsGlobalNavigation(keys) ||
                closeChord && !(DesktopNavigation.IsWindowClose(keys) && ordinaryWindow && UserRequested(context, CloseRequested, CloseProhibited)) ||
                saveChord && !(DesktopNavigation.IsSave(keys) && ordinaryWindow && UserRequested(context, SaveRequested, SaveProhibited)))
                return Wait("SHORTCUT_REQUIRES_MANUAL", "该快捷键缺少明确的保存/关闭目标，或当前是系统桌面等不适用窗口；请核对后手动处理。");
        }
        try
        {
            NormalizedPoint[] points = act.Action switch
            {
                MoveAction m => [m.Point], ClickAction c => [c.Point], ScrollAction s => [s.Point], DragAction d => [d.From, d.To], _ => []
            };
            foreach (var p in points)
            {
                var point = InputCoordinates.ToPhysical(p, frame.PhysicalRegion);
                if (!currentEnvironment.Displays.Any(d => FrameChecks.Contains(d.Bounds, point))) return Reject("DISPLAY_GAP", "目标不在可操作屏幕内。");
                if (frame.OwnWindowRects.Any(r => FrameChecks.Contains(r, point))) return Reject("OWN_WINDOW_TARGET", "助手窗口挡住目标，需避让后重新观察。");
            }
        }
        catch (ArgumentException) { return Reject("INVALID_ACTION_COORDINATES", "坐标无效。"); }
        if (!context.HighRiskEnabled && act.VerifiedAccountEntry)
            return Wait("ACCOUNT_ENTRY_PERMISSION_REQUIRED", "当前是微信账户进入页面，尚未点击“进入微信”。可在此任务启用高风险操作并继续；具体进入操作仍会单独确认，密码或扫码需你处理。");
        if (!context.HighRiskEnabled && HighRiskActions.IsMessaging(frame.Foreground) && !HighRiskActions.SafeMessagePreparation(act))
            return Wait("MESSAGE_COMMIT_DISABLED", "可以打开并查看消息；发送或其他提交需要启用本任务的高风险操作，并对具体操作最终确认。");
        if (context.HighRiskEnabled && HighRiskActions.RequiresMessageReview(frame, act) && act.MessageReview is null)
            return Wait("MESSAGE_REVIEW_REQUIRED", "发送前需要从当前界面核对真实联系人和完整草稿，生成确认卡后再发送。");
        if (HighRiskActions.RequiresApproval(context, frame, act) && !context.ActionApprovalGranted)
            return Wait("ACTION_APPROVAL_REQUIRED", "请在确认卡核对目标和内容，批准后只执行这一步。");
        return new(PolicyDisposition.Allow, "ACTION_VALIDATED", "动作通过本地基础检查。",
            new ValidatedAction(context.Lease, frame.Id, proposal.ProposalId, act.Action));
    }
}
