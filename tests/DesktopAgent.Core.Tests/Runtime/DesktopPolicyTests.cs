using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class DesktopPolicyTests
{
    private static (TaskContext Context, Frame Frame, DesktopEnvironment Environment, Proposal Proposal) Case(string goal = "将界面改成深色")
    {
        var rect = new PhysicalRect(0, 0, 1000, 800);
        var lease = new Lease(Guid.NewGuid(), 0);
        var foreground = new ForegroundIdentity("1", 100, "SystemSettings", rect);
        var frame = new Frame("frame", lease, DateTimeOffset.UtcNow, 1, "display", rect, new(1000, 800, "image/png", [1]), FrameViewKind.Overview, foreground, []);
        var task = new TaskContext(lease.TaskId, 0, goal, DateTimeOffset.UtcNow, TaskState.Running, false, "profile", "display",
            TaskBudget.Default, new(0, 0, 0, null, null), [], null, null);
        var env = new DesktopEnvironment(1, [new("display", rect, rect, 120, 120, true)], foreground, DesktopSessionState.Available);
        var proposal = new Proposal(2, "p1", lease.TaskId, 0, frame.Id, "找到开关", "核对结果", new ActDecision(new ClickAction(new(500, 500), MouseButton.Left, 1), "深色模式", "外观变成深色"));
        return (task, frame, env, proposal);
    }
    [Fact]
    public void OrdinarySettingMintsBoundedActionButStaleAndUnresolvedControlsDoNot()
    {
        var c = Case(); var policy = new DesktopPolicyValidator(TimeSpan.FromSeconds(90));
        var result = policy.Validate(c.Context, c.Frame, c.Proposal, c.Environment);
        Assert.Equal(PolicyDisposition.Allow, result.Disposition);
        Assert.Equal(c.Frame.Id, result.Action!.FrameId);
        Assert.Null(policy.Validate(c.Context with { Epoch = 1 }, c.Frame, c.Proposal, c.Environment).Action);
        Assert.Null(policy.Validate(c.Context, c.Frame, c.Proposal, c.Environment with { DisplayGeneration = 2 }).Action);
        Assert.Equal("CONTROL_NOT_RESOLVED", policy.Validate(c.Context, c.Frame, c.Proposal with
        { Decision = new ControlActDecision("s", "c1", MouseButton.Left, 1, "设置", "变化") }, c.Environment).Code);
    }
    [Theory]
    [InlineData("关闭防火墙")]
    [InlineData("修改网络配置")]
    [InlineData("给张三发送消息")]
    [InlineData("delete files")]
    public void HighImpactAndMessagingStayManual(string goal)
    {
        var c = Case(goal);
        Assert.Equal(PolicyDisposition.Wait, new DesktopPolicyValidator(TimeSpan.FromSeconds(90)).Validate(c.Context, c.Frame, c.Proposal, c.Environment).Disposition);
    }
    [Fact]
    public void RiskHiddenFromGoalStillBlocksNamedActionAndShellShortcuts()
    {
        var c = Case(); var policy = new DesktopPolicyValidator(TimeSpan.FromSeconds(90));
        var act = (ActDecision)c.Proposal.Decision;
        Assert.Equal("HIGH_IMPACT_MANUAL", policy.Validate(c.Context, c.Frame, c.Proposal with { Decision = act with { Target = "支付按钮" } }, c.Environment).Code);
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", policy.Validate(c.Context, c.Frame, c.Proposal with { Decision = act with { Action = new HotkeyAction([AgentKey.WIN, AgentKey.R]) } }, c.Environment).Code);
        Assert.Equal("EXECUTABLE_TEXT_DISABLED", policy.Validate(c.Context, c.Frame, c.Proposal with { Decision = act with { Action = new TextAction("powershell command") } }, c.Environment).Code);
        Assert.Equal("SELF_FOREGROUND", new DesktopPolicyValidator(TimeSpan.FromSeconds(90), 100).Validate(c.Context, c.Frame, c.Proposal, c.Environment).Code);
    }
    [Theory]
    [InlineData("进入测试目录，只导航，不改设置，不编辑或删除文件。", false)]
    [InlineData("打开目录。请不要删除任何文件。", false)]
    [InlineData("不要删除文件，但删除备份", true)]
    [InlineData("不要忘记删除文件", true)]
    [InlineData("不是不要删除文件", true)]
    [InlineData("不删除文件才怪", true)]
    [InlineData("删除文件。不要删除这些文件。", true)]
    [InlineData("打开目录，不要删除文件。然后支付订单", true)]
    public void OnlyExplicitStandaloneFileProhibitionsAreExcludedFromGoal(string goal, bool manual)
    {
        var c = Case(goal);
        var policy = new DesktopPolicyValidator(TimeSpan.FromSeconds(90));
        Assert.Equal(manual ? PolicyDisposition.Wait : PolicyDisposition.Allow,
            policy.Validate(c.Context, c.Frame, c.Proposal, c.Environment).Disposition);
        var action = (ActDecision)c.Proposal.Decision;
        Assert.Equal(PolicyDisposition.Wait, policy.Validate(c.Context, c.Frame,
            c.Proposal with { Decision = action with { Target = "删除文件" } }, c.Environment).Disposition);
    }

    [Theory]
    [InlineData("通过开始菜单搜索并打开微信。微信窗口打开后立即结束，不进入聊天、不读取消息、不发送任何内容。", false)]
    [InlineData("打开微信，不要发送消息。", false)]
    [InlineData("只查看消息。请不要发送任何消息。", false)]
    [InlineData("禁止发送任何内容", false)]
    [InlineData("不发送内容；不要发送任何消息！", false)]
    [InlineData("打开微信\n禁止发送消息\n", false)]
    [InlineData("不要发送消息，但发送你好", true)]
    [InlineData("不发送任何内容。然后发送消息", true)]
    [InlineData("发送你好，不要发送任何内容", true)]
    [InlineData("不发送消息、发送内容", true)]
    [InlineData("不用确认发送消息", true)]
    [InlineData("不要确认发送消息", true)]
    [InlineData("不要忘记发送消息", true)]
    [InlineData("不是不要发送消息", true)]
    [InlineData("不发送消息才怪", true)]
    [InlineData("不要发送任何内容，但支付订单", true)]
    public void StandaloneSendProhibitionAllowsNavigationWithoutDiscardingAffirmativeInstructions(string goal, bool manual)
    {
        var c = Case(goal);
        var navigate = new ActDecision(new HotkeyAction([AgentKey.WIN]), "开始菜单", "显示应用搜索");
        var policy = new DesktopPolicyValidator(TimeSpan.FromSeconds(90));
        var result = policy.Validate(c.Context, c.Frame, c.Proposal with { Decision = navigate }, c.Environment);
        Assert.Equal(manual ? PolicyDisposition.Wait : PolicyDisposition.Allow, result.Disposition);
        if (manual) Assert.Equal("HIGH_IMPACT_MANUAL", result.Code);
        Assert.Equal("HIGH_IMPACT_MANUAL", policy.Validate(c.Context, c.Frame,
            c.Proposal with { Decision = navigate with { Target = "发送消息" } }, c.Environment).Code);
        Assert.Equal("HIGH_IMPACT_MANUAL", policy.Validate(c.Context, c.Frame,
            c.Proposal with { Decision = navigate with { Expected = "不发送任何内容" } }, c.Environment).Code);
    }

    [Fact]
    public void SendProhibitionDoesNotHideRiskInInterpretationOrActualClarification()
    {
        var c = Case("打开微信，不要发送任何消息。");
        var navigate = c.Proposal with { Decision = new ActDecision(new HotkeyAction([AgentKey.WIN]), "开始菜单", "显示应用搜索") };
        var policy = new DesktopPolicyValidator(TimeSpan.FromSeconds(90));
        var understood = c.Context with
        {
            Interpretation = new("打开微信。禁止发送消息。", "只查看"),
            Clarifications = [new("如何继续？", "打开微信，不发送任何内容。")]
        };
        Assert.Equal(PolicyDisposition.Allow, policy.Validate(understood, c.Frame, navigate, c.Environment).Disposition);
        Assert.Equal("HIGH_IMPACT_MANUAL", policy.Validate(understood with
            { Interpretation = new("禁止发送消息。随后发送你好。", "继续") }, c.Frame, navigate, c.Environment).Code);
        Assert.Equal("HIGH_IMPACT_MANUAL", policy.Validate(understood with
            { Clarifications = [new("如何继续？", "不要发送消息，但发送你好。")] }, c.Frame, navigate, c.Environment).Code);
        Assert.Equal("HIGH_IMPACT_MANUAL", policy.Validate(understood with
            { Goal = "给张三发送你好。不要发送其他消息。" }, c.Frame, navigate, c.Environment).Code);
    }
}
