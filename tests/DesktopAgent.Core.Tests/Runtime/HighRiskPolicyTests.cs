using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class HighRiskPolicyTests
{
    private sealed record Scenario(TaskContext Task, Frame Frame, DesktopEnvironment Environment, ControlSnapshot Controls, ActDecision Act)
    {
        internal PolicyResult Validate(TaskContext? task = null, ActDecision? act = null) =>
            new DesktopPolicyValidator(TimeSpan.FromSeconds(90)).Validate(task ?? Task, Frame,
                new(2, "proposal", (task ?? Task).Id, (task ?? Task).Epoch, Frame.Id, "观察", "下一步", act ?? Act), Environment);
    }
    private static Scenario Case(string process = "SystemSettings", string goal = "删除测试文件", bool enabled = false)
    {
        var rect = new PhysicalRect(0, 0, 1000, 800);
        var lease = new Lease(Guid.NewGuid(), 0);
        var foreground = new ForegroundIdentity("1", 100, process, rect) { WindowClass = "FixtureWindow" };
        var frame = new Frame("frame", lease, DateTimeOffset.UtcNow, 1, "display", rect,
            new(1000, 800, "image/png", [1]), FrameViewKind.Overview, foreground, []);
        var task = new TaskContext(lease.TaskId, 0, goal, DateTimeOffset.UtcNow, TaskState.Running, false, "profile", "display",
            TaskBudget.Default, new(0, 0, 0, null, null), [], null, null) { HighRiskEnabled = enabled };
        var controls = new ControlSnapshot(Guid.NewGuid().ToString("N"), lease, frame.Id, DateTimeOffset.UtcNow, ControlSnapshotStatus.Available,
            [new ControlCandidate("recipient", null, "张三", "Text", new(10, 10, 200, 30), true, false, false, null, null),
             new ControlCandidate("draft", null, "草稿", "Edit", new(10, 100, 600, 100), true, true, true, null, null)
                 { CurrentValue = "你好", ValueTruncated = false, SelectionStart = 2, SelectionLength = 0 }]);
        return new(task, frame, new(1, [new("display", rect, rect, 120, 120, true)], foreground, DesktopSessionState.Available),
            controls, new(new ClickAction(new(500, 500), MouseButton.Left, 1), "删除按钮", "测试文件移除"));
    }
    private static ActDecision Send(bool click = false) => new(click ? new ClickAction(new(500, 500), MouseButton.Left, 1) : new HotkeyAction([AgentKey.ENTER]),
        "发送消息", "消息出现在当前会话") { MessageReview = new("张三", "你好") };
    private static ActionApprovalBinding Approved(Scenario c, ActDecision? act = null)
    {
        var binding = ActionApprovalBinding.Create(c.Task, c.Frame, c.Controls, act ?? c.Act);
        binding.ApprovedAt = DateTimeOffset.UtcNow; return binding;
    }
    private static Frame Renew(Frame frame, ForegroundIdentity? foreground = null, long? generation = null, string? monitor = null, PhysicalRect? region = null) =>
        new("fresh-frame", frame.Lease, DateTimeOffset.UtcNow, generation ?? frame.DisplayGeneration, monitor ?? frame.MonitorId,
            region ?? frame.PhysicalRegion, frame.Image, FrameViewKind.Overview, foreground ?? frame.Foreground, []);

    [Fact]
    public void HighImpactIsDisabledByDefaultAndAGrantCannotOverrideTheSwitch()
    {
        var c = Case(); Assert.False(c.Task.HighRiskEnabled);
        Assert.Equal("HIGH_IMPACT_MANUAL", c.Validate().Code);
        Assert.Equal("HIGH_IMPACT_MANUAL", c.Validate(c.Task with { ActionApprovalGranted = true }).Code);
    }

    [Fact]
    public void OpeningChatForReadingDoesNotRequireTheHighRiskSwitch()
    {
        var c = Case("WeChat", "打开微信，进入聊天页面，看看有没有新消息", false);
        var navigate = new ActDecision(new ClickAction(new(500, 500), MouseButton.Left, 1), "聊天页面", "查看会话列表")
            { VerifiedMessageNavigation = true };
        Assert.Equal(PolicyDisposition.Allow, c.Validate(act: navigate).Disposition);
        Assert.Equal("MESSAGE_COMMIT_DISABLED", c.Validate(act: navigate with { VerifiedMessageNavigation = false }).Code);
        Assert.Equal(PolicyDisposition.Allow, c.Validate(act: new(new HotkeyAction([AgentKey.CTRL, AgentKey.F]), "搜索", "搜索框获得焦点")).Disposition);
        Assert.NotEqual(PolicyDisposition.Allow, c.Validate(act: Send()).Disposition);
        Assert.Equal("MESSAGE_COMMIT_DISABLED", c.Validate(act: new(new HotkeyAction([AgentKey.ENTER]), "继续查看", "显示结果")).Code);
    }

    [Fact]
    public void ExplicitNoSendingStillAllowsVerifiedReadOnlyWeChatNavigationAndBlocksCommit()
    {
        var c = Case("WeChat", "打开微信，查看今天的新消息。不要发送任何消息。", false);
        var navigate = new ActDecision(new ClickAction(new(500, 500), MouseButton.Left, 1), "聊天页面", "查看会话列表")
            { VerifiedMessageNavigation = true };
        Assert.Equal(PolicyDisposition.Allow, c.Validate(act: navigate).Disposition);
        Assert.Equal("HIGH_IMPACT_MANUAL", c.Validate(act: Send()).Code);
        Assert.Equal("MESSAGE_COMMIT_DISABLED", c.Validate(act: new(new HotkeyAction([AgentKey.ENTER]), "继续查看", "显示结果")).Code);
    }

    [Fact]
    public void EnabledTaskCanNavigateAndPrepareWithoutApprovingTheConsequentialStep()
    {
        var c = Case(enabled: true);
        var navigation = new ActDecision(new HotkeyAction([AgentKey.WIN]), "打开开始菜单", "找到程序");
        Assert.Equal(PolicyDisposition.Allow, c.Validate(act: navigation).Disposition);
        Assert.False(HighRiskActions.RequiresApproval(c.Task, c.Frame, navigation));
        Assert.Equal("ACTION_APPROVAL_REQUIRED", c.Validate().Code);
    }

    [Fact]
    public void MessagingDraftTextDoesNotSendAndDoesNotRequireApproval()
    {
        var c = Case("WeChat", "给张三发送你好", true);
        var draft = new ActDecision(new TextAction("你好"), "聊天输入框", "形成待核对草稿");
        Assert.True(HighRiskActions.SafeMessagePreparation(draft));
        Assert.False(HighRiskActions.RequiresMessageReview(c.Frame, draft));
        Assert.Equal(PolicyDisposition.Allow, c.Validate(act: draft).Disposition);
        Assert.Equal(PolicyDisposition.Wait, c.Validate(c.Task with { HighRiskEnabled = false }, draft).Disposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MessagingSendNeedsReviewEvenIfAnInternalGrantExists(bool click)
    {
        var c = Case("WeChat", "给张三发送你好", true);
        var send = Send(click) with { MessageReview = null };
        Assert.False(HighRiskActions.SafeMessagePreparation(send));
        Assert.Equal("MESSAGE_REVIEW_REQUIRED", c.Validate(act: send).Code);
        Assert.Equal("MESSAGE_REVIEW_REQUIRED", c.Validate(c.Task with { ActionApprovalGranted = true }, send).Code);
    }

    [Theory]
    [InlineData("搜索")]
    [InlineData("返回联系人")]
    [InlineData("search contacts")]
    public void ModelClaimedNavigationCannotBypassMessageReview(string target)
    {
        var c = Case("WeChat", "给张三发送你好", true);
        var click = new ActDecision(new ClickAction(new(500, 500), MouseButton.Left, 1), target, "导航完成");
        Assert.False(HighRiskActions.SafeMessagePreparation(click));
        Assert.Equal("MESSAGE_REVIEW_REQUIRED", c.Validate(act: click).Code);
        Assert.Equal("MESSAGE_REVIEW_REQUIRED", c.Validate(c.Task with { ActionApprovalGranted = true }, click).Code);
        Assert.Equal(PolicyDisposition.Allow, c.Validate(act: click with { VerifiedMessageNavigation = true }).Disposition);
    }

    [Theory]
    [InlineData("ListItem", "张三", true)]
    [InlineData("TreeItem", "项目联系人", true)]
    [InlineData("Button", "搜索", true)]
    [InlineData("Button", "取消", true)]
    [InlineData("Button", "返回", true)]
    [InlineData("Button", "Search contacts", true)]
    [InlineData("TabItem", "聊天 (2)", true)]
    [InlineData("TabItem", "Contacts", true)]
    [InlineData("Button", "", false)]
    [InlineData("Button", "发送", false)]
    [InlineData("Button", "搜索并发送", false)]
    [InlineData("Button", "Search and send", false)]
    [InlineData("Button", "Feedback", false)]
    [InlineData("Button", "提交", false)]
    [InlineData("ListItem", "删除联系人", false)]
    [InlineData("MenuItem", "搜索", false)]
    [InlineData("Text", "联系人", false)]
    public void OnlyNativeNavigationCandidatesCanSetTheLocalNavigationFlag(string role, string name, bool allowed)
    {
        var candidate = new ControlCandidate("candidate", null, name, role, new(10, 10, 100, 30), true, false, true, null, null);
        Assert.Equal(allowed, HighRiskActions.IsMessageNavigationCandidate(candidate));
        Assert.False(HighRiskActions.IsMessageNavigationCandidate(candidate with { Enabled = false }));
    }

    [Fact]
    public void NavigationVerificationCannotAuthorizeOtherGesturesOrLeakIntoModelJson()
    {
        var c = Case("WeChat", "给张三发送你好", true);
        foreach (var action in new AgentAction[] { new HotkeyAction([AgentKey.ENTER]), new ClickAction(new(500, 500), MouseButton.Right, 1), new ClickAction(new(500, 500), MouseButton.Left, 2) })
        {
            var act = new ActDecision(action, "搜索", "打开结果") { VerifiedMessageNavigation = true };
            Assert.False(HighRiskActions.SafeMessagePreparation(act));
            Assert.Equal("MESSAGE_REVIEW_REQUIRED", c.Validate(act: act).Code);
            Assert.DoesNotContain("VerifiedMessageNavigation", JsonSerializer.Serialize(act), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelReviewIsNotApprovalAndMatchingLocalApprovalAllowsOnlyOneUse(bool click)
    {
        var c = Case("WeChat", "给张三发送你好", true); var send = Send(click);
        Assert.Equal("ACTION_APPROVAL_REQUIRED", c.Validate(act: send).Code);
        var binding = Approved(c, send);
        Assert.Equal("张三", binding.Review.Recipient); Assert.Equal("你好", binding.Review.Message);
        bool grant = binding.Matches(c.Task, c.Frame, c.Controls, send, DateTimeOffset.UtcNow);
        Assert.True(grant);
        Assert.Equal(PolicyDisposition.Allow, c.Validate(c.Task with { ActionApprovalGranted = grant }, send).Disposition);
        binding.Used = true;
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, send, DateTimeOffset.UtcNow));
        Assert.Equal("ACTION_APPROVAL_REQUIRED", c.Validate(act: send).Code);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("WindowsTerminal")]
    [InlineData("regedit")]
    [InlineData("mmc")]
    [InlineData("Taskmgr")]
    [InlineData("msiexec")]
    [InlineData("SecHealthUI")]
    [InlineData("CredentialUIBroker")]
    [InlineData("consent")]
    public void CommandAndPrivilegedAppsRemainRestrictedWithOrWithoutApproval(string process)
    {
        var c = Case(process, "打开界面", true);
        var navigation = new ActDecision(new HotkeyAction([AgentKey.WIN]), "打开开始菜单", "找到程序");
        foreach (bool enabled in new[] { false, true })
        foreach (bool grant in new[] { false, true })
            Assert.Equal("APPLICATION_REQUIRES_MANUAL", c.Validate(c.Task with { HighRiskEnabled = enabled, ActionApprovalGranted = grant }, navigation).Code);
    }

    [Fact]
    public void ExecutableTextRemainsBlockedEvenWithAHighImpactGrant()
    {
        var c = Case("Notepad", "输入文字", true);
        Assert.Equal("EXECUTABLE_TEXT_DISABLED", c.Validate(c.Task with { ActionApprovalGranted = true },
            new(new TextAction("powershell command"), "输入框", "出现内容")).Code);
    }

    [Fact]
    public void ApprovalHasNoAuthorityBeforeApprovalAfterExpiryOrAfterUse()
    {
        var c = Case(enabled: true);
        var binding = ActionApprovalBinding.Create(c.Task, c.Frame, c.Controls, c.Act);
        var now = DateTimeOffset.UtcNow;
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, c.Act, now));
        binding.ApprovedAt = now;
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, c.Act, now.AddTicks(-1)));
        Assert.True(binding.Matches(c.Task, c.Frame, c.Controls, c.Act, now.AddSeconds(60)));
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, c.Act, now.AddSeconds(60).AddTicks(1)));
        Assert.False(binding.Matches(c.Task with { HighRiskEnabled = false }, c.Frame, c.Controls, c.Act, now));
        Assert.False(binding.Matches(c.Task with { Id = Guid.NewGuid() }, c.Frame, c.Controls, c.Act, now));
        binding.Used = true;
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, c.Act, now));
    }

    [Fact]
    public void WindowIdentityGeometryDisplayAndMonitorChangesInvalidateApproval()
    {
        var c = Case(enabled: true); var binding = Approved(c); var now = DateTimeOffset.UtcNow;
        foreach (var foreground in new[]
        {
            c.Frame.Foreground with { HwndHex = "2" }, c.Frame.Foreground with { ProcessId = 101 },
            c.Frame.Foreground with { ProcessName = "another-app" }, c.Frame.Foreground with { WindowClass = "another-class" },
            c.Frame.Foreground with { WindowRect = new(1, 0, 1000, 800) }
        }) Assert.False(binding.Matches(c.Task, Renew(c.Frame, foreground), c.Controls, c.Act, now));
        Assert.False(binding.Matches(c.Task, Renew(c.Frame, generation: 2), c.Controls, c.Act, now));
        Assert.False(binding.Matches(c.Task, Renew(c.Frame, monitor: "other-display"), c.Controls, c.Act, now));
        Assert.False(binding.Matches(c.Task, Renew(c.Frame, region: new(0, 0, 500, 400)), c.Controls, c.Act, now));
    }

    [Fact]
    public void CurrentValueRecipientAndVisibleControlChangesInvalidateApproval()
    {
        var c = Case("WeChat", "给张三发送你好", true); var act = Send(); var binding = Approved(c, act); var now = DateTimeOffset.UtcNow;
        ControlSnapshot Changed(ControlCandidate candidate) => c.Controls with { Candidates = c.Controls.Candidates.SetItem(1, candidate) };
        var draft = c.Controls.Candidates[1];
        foreach (var candidate in new[]
        {
            draft with { CurrentValue = "另一段草稿" }, draft with { CurrentValue = null, ValueTruncated = true },
            draft with { Bounds = new(11, 100, 600, 100) }, draft with { Enabled = false }, draft with { Name = "另一个输入框" }
        }) Assert.False(binding.Matches(c.Task, c.Frame, Changed(candidate), act, now));
        var otherRecipient = c.Controls with { Candidates = c.Controls.Candidates.SetItem(0, c.Controls.Candidates[0] with { Name = "李四" }) };
        Assert.False(binding.Matches(c.Task, c.Frame, otherRecipient, act, now));
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls with { Candidates = [] }, act, now));
    }

    [Fact]
    public void ActionAndModelTranscribedMessageCannotBeSubstitutedAfterApproval()
    {
        var c = Case("WeChat", "给张三发送你好", true); var act = Send(); var binding = Approved(c, act); var now = DateTimeOffset.UtcNow;
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, Send(true), now));
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, act with { MessageReview = new("李四", "你好") }, now));
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, act with { MessageReview = new("张三", "另一段消息") }, now));
        Assert.False(binding.Matches(c.Task, c.Frame, c.Controls, act with { MessageReview = null }, now));
    }

    [Fact]
    public void ReobservationIdsAndFocusMayChangeWithoutChangingTheReviewedContent()
    {
        var c = Case(enabled: true); var binding = Approved(c); var fresh = Renew(c.Frame);
        var controls = c.Controls with { Id = Guid.NewGuid().ToString("N"), FrameId = fresh.Id,
            Candidates = c.Controls.Candidates.Select((candidate, i) => candidate with { Id = "fresh-" + i, Focused = !candidate.Focused }).ToImmutableArray() };
        Assert.True(binding.Matches(c.Task, fresh, controls, c.Act, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void InternalApprovalGrantIsNeverSerializedIntoModelTaskData()
    {
        var c = Case(enabled: true);
        string json = JsonSerializer.Serialize(c.Task with { ActionApprovalGranted = true });
        Assert.DoesNotContain("ActionApprovalGranted", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApprovedAt", json, StringComparison.OrdinalIgnoreCase);
    }
}
