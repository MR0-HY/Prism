using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class DesktopNavigationPolicyTests
{
    private static PolicyResult Validate(string goal, AgentKey[] keys, string process = "Notepad", string? windowClass = null,
        string? inferredGoal = null, string? answer = null)
    {
        var rect = new PhysicalRect(0, 0, 1000, 800);
        var lease = new Lease(Guid.NewGuid(), 0);
        var foreground = new ForegroundIdentity("1", 100, process, rect) { WindowClass = windowClass };
        var frame = new Frame("frame", lease, DateTimeOffset.UtcNow, 1, "display", rect,
            new(1000, 800, "image/png", [1]), FrameViewKind.Overview, foreground, []);
        var task = new TaskContext(lease.TaskId, 0, goal, DateTimeOffset.UtcNow, TaskState.Running, false, "profile", "display",
            TaskBudget.Default, new(0, 0, 0, null, null), [], null, null)
        {
            Interpretation = inferredGoal is null ? null : new(inferredGoal, "模型推测"),
            Clarifications = answer is null ? [] : [new("如何继续？", answer)]
        };
        var env = new DesktopEnvironment(1, [new("display", rect, rect, 120, 120, true)], foreground, DesktopSessionState.Available);
        var proposal = new Proposal(2, "p1", lease.TaskId, 0, frame.Id, "当前普通窗口", "核对效果",
            new ActDecision(new HotkeyAction([.. keys]), "当前窗口", "观察窗口状态"));
        return new DesktopPolicyValidator(TimeSpan.FromSeconds(90)).Validate(task, frame, proposal, env);
    }

    [Theory]
    [InlineData(AgentKey.D)]
    [InlineData(AgentKey.M)]
    [InlineData(AgentKey.I)]
    [InlineData(AgentKey.E)]
    public void GlobalNavigationIsSharedAndAllowedOnDesktop(AgentKey key)
    {
        var keys = new[] { AgentKey.WIN, key };
        Assert.True(DesktopNavigation.IsGlobalNavigation(new HotkeyAction([.. keys])));
        Assert.Equal(PolicyDisposition.Allow, Validate("最小化所有窗口并打开桌面", keys, "explorer", "Progman").Disposition);
    }

    [Theory]
    [InlineData(AgentKey.R)]
    [InlineData(AgentKey.L)]
    [InlineData(AgentKey.X)]
    public void OtherWindowsChordsStayBlocked(AgentKey key)
    {
        var keys = new[] { AgentKey.WIN, key };
        Assert.False(DesktopNavigation.IsGlobalNavigation(new HotkeyAction([.. keys])));
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate("操作普通窗口", keys).Code);
        Assert.False(DesktopNavigation.IsGlobalNavigation(new HotkeyAction([AgentKey.WIN, AgentKey.SHIFT, AgentKey.D])));
    }

    [Theory]
    [InlineData("把设置关掉，然后最小化所有窗口", "SystemSettings", null)]
    [InlineData("关闭设置窗口", "SystemSettings", null)]
    [InlineData("写入文本325，保存后退出", "Notepad", null)]
    [InlineData("Close the current editor window", "Notepad", null)]
    [InlineData("Save this file and exit", "Notepad", null)]
    [InlineData("Save report.txt and exit", "Notepad", null)]
    [InlineData("保存文档，然后退出", "Notepad", null)]
    [InlineData("关闭文件夹窗口", "explorer", "CabinetWClass")]
    public void RequestedOrdinaryWindowCloseIsAllowed(string goal, string process, string? windowClass)
    {
        Assert.Equal(PolicyDisposition.Allow, Validate(goal, [AgentKey.ALT, AgentKey.F4], process, windowClass).Disposition);
        Assert.Equal(PolicyDisposition.Allow, Validate(goal, [AgentKey.CTRL, AgentKey.W], process, windowClass).Disposition);
    }

    [Theory]
    [InlineData("操作这个窗口")]
    [InlineData("关闭自动换行")]
    [InlineData("退出全屏模式")]
    [InlineData("Exit fullscreen mode")]
    [InlineData("不要关闭记事本窗口")]
    [InlineData("Close the window, but do not close it yet")]
    public void IncidentalOrNegatedCloseIsNotAuthorization(string goal) =>
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate(goal, [AgentKey.ALT, AgentKey.F4]).Code);

    [Theory]
    [InlineData("关闭自动换行，然后查看窗口")]
    [InlineData("关闭自动换行。查看窗口")]
    [InlineData("关闭自动换行；查看窗口")]
    [InlineData("关闭自动换行\r\n查看窗口")]
    [InlineData("Exit fullscreen, then inspect the window")]
    [InlineData("Exit fullscreen. View window")]
    [InlineData("Exit fullscreen; view window")]
    [InlineData("Exit fullscreen\nview window")]
    [InlineData("保存文件。关闭自动换行")]
    [InlineData("Save. Close popups")]
    public void SeparateClausesCannotCombineIntoWindowCloseAuthorization(string goal)
    {
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate(goal, [AgentKey.ALT, AgentKey.F4]).Code);
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate(goal, [AgentKey.CTRL, AgentKey.W]).Code);
    }

    [Theory]
    [InlineData("创建325.txt文件，输入325，保存后退出")]
    [InlineData("新建一个文本文件")]
    [InlineData("写入文件325.txt")]
    [InlineData("Save the current file")]
    [InlineData("Write 325 in a text file")]
    public void OrdinaryFileRequestAllowsExactSaveChord(string goal) =>
        Assert.Equal(PolicyDisposition.Allow, Validate(goal, [AgentKey.CTRL, AgentKey.S]).Disposition);

    [Theory]
    [InlineData("查看325.txt")]
    [InlineData("创建一个文件夹")]
    [InlineData("输入325但不要保存")]
    [InlineData("不要创建文本文件")]
    [InlineData("Write a file, but do not save it yet")]
    [InlineData("Do not create a text file")]
    public void SaveNeedsAnUnnegatedFileWriteGoal(string goal) =>
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate(goal, [AgentKey.CTRL, AgentKey.S]).Code);

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData(null)]
    [InlineData("UnknownShellWindow")]
    public void ShellOrUnknownExplorerCannotReceiveSaveOrClose(string? windowClass)
    {
        foreach (AgentKey[] keys in new[] { new[] { AgentKey.ALT, AgentKey.F4 }, [AgentKey.CTRL, AgentKey.W], [AgentKey.CTRL, AgentKey.S] })
            Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate("保存文本文件并关闭窗口", keys, "explorer", windowClass).Code);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("pwsh")]
    [InlineData("SecHealthUI")]
    [InlineData("consent")]
    public void RestrictedForegroundStaysBlocked(string process) =>
        Assert.Equal("APPLICATION_REQUIRES_MANUAL", Validate("保存文本文件并关闭窗口", [AgentKey.CTRL, AgentKey.S], process).Code);

    [Fact]
    public void MessagingReadNavigationDoesNotGrantOtherCommits() =>
        Assert.Equal("MESSAGE_COMMIT_DISABLED", Validate("保存文本文件并关闭窗口", [AgentKey.CTRL, AgentKey.S], "WeChat").Code);

    [Fact]
    public void ModelGuessCannotAuthorizeButActualUserAnswerCan()
    {
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate("看一下窗口", [AgentKey.ALT, AgentKey.F4], inferredGoal: "关闭窗口").Code);
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate("看一下窗口", [AgentKey.CTRL, AgentKey.S], inferredGoal: "保存文本文件").Code);
        Assert.Equal(PolicyDisposition.Allow, Validate("看一下窗口", [AgentKey.ALT, AgentKey.F4], answer: "关闭这个窗口").Disposition);
        Assert.Equal(PolicyDisposition.Allow, Validate("看一下窗口", [AgentKey.CTRL, AgentKey.S], answer: "保存这个文本文件").Disposition);
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate("关闭窗口", [AgentKey.ALT, AgentKey.F4], answer: "保持窗口打开").Code);
    }

    [Fact]
    public void ExtraModifiersAndHighImpactGoalsDoNotBorrowShortcutPermission()
    {
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate("保存文件并关闭窗口", [AgentKey.CTRL, AgentKey.SHIFT, AgentKey.S]).Code);
        Assert.Equal("SHORTCUT_REQUIRES_MANUAL", Validate("保存文件并关闭窗口", [AgentKey.CTRL, AgentKey.SHIFT, AgentKey.W]).Code);
        Assert.Equal("HIGH_IMPACT_MANUAL", Validate("删除文件后关闭窗口", [AgentKey.ALT, AgentKey.F4]).Code);
        Assert.False(DesktopNavigation.IsSave(new HotkeyAction([AgentKey.CTRL, AgentKey.SHIFT, AgentKey.S])));
        Assert.False(DesktopNavigation.IsWindowClose(new HotkeyAction([AgentKey.CTRL, AgentKey.SHIFT, AgentKey.W])));
    }
}
