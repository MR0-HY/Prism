using System.Collections.Immutable;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Tests.Domain;

public sealed class TaskPlanTrackingTests
{
    private static readonly TaskInterpretation Plan = new("创建并保存文件", "我会先创建文件，再写入并保存。")
    {
        Steps = [new("s1", "创建文件", "文件名正确"), new("s2", "写入并保存", "正文正确且已保存")],
        CompletionCheck = "指定文件名与内容均正确，已保存"
    };
    private static Frame Frame(Lease lease, string id = "current-frame")
    {
        var bounds = new PhysicalRect(0, 0, 100, 100);
        return new(id, lease, DateTimeOffset.UtcNow, 1, "display", bounds, new(100, 100, "image/png", [1]),
            FrameViewKind.Overview, new("1", 100, "fixture", bounds), []);
    }

    [Fact]
    public void NewUpdatesCarryCurrentObservationIdentityAndCanCorrectCompletedBackToPending()
    {
        var lease = new Lease(Guid.NewGuid(), 2); var frame = Frame(lease);
        ImmutableArray<TaskStepReport> older = [new("s1", "completed", "旧画面曾显示文件", new(lease.TaskId, 1), "older-frame")];
        TaskPlanTracking.Validate(Plan, older, lease); Assert.False(older[0].Verified);
        var reports = TaskPlanTracking.Apply(Plan, older, [new("s2", "completed", "当前正文和保存状态正确")], frame);
        Assert.True(TaskPlanTracking.AllCompleted(Plan, reports)); Assert.Equal("older-frame", reports[0].FrameId);
        Assert.Equal(frame.Id, reports[1].FrameId); Assert.Equal(lease, reports[1].Lease); Assert.False(reports[1].Verified);
        var correctedFrame = Frame(new(lease.TaskId, 3), "newer-frame");
        var corrected = TaskPlanTracking.Apply(Plan, reports, [new("s1", "pending", "新画面发现文件名不符")], correctedFrame);
        Assert.False(TaskPlanTracking.AllCompleted(Plan, corrected)); Assert.Equal("pending", corrected[0].Status);
        Assert.Equal("newer-frame", corrected[0].FrameId); Assert.Equal(correctedFrame.Lease, corrected[0].Lease);
        Assert.Equal("completed", reports[0].Status); // Previous immutable history was not rewritten.
    }

    [Fact]
    public void AllCompletedRequiresEveryPlannedOutcomeAndDoesNotInventPlanForLegacyInterpretations()
    {
        var lease = new Lease(Guid.NewGuid(), 0);
        Assert.False(TaskPlanTracking.AllCompleted(null, []));
        Assert.False(TaskPlanTracking.AllCompleted(new("old goal", "old reply"), []));
        Assert.False(TaskPlanTracking.AllCompleted(Plan, []));
        Assert.False(TaskPlanTracking.AllCompleted(Plan, [new("s1", "completed", "visible", lease, "frame")]));
        Assert.False(TaskPlanTracking.AllCompleted(Plan, [new("s1", "completed", "visible", lease, "frame"), new("s2", "active", "remaining", lease, "frame")]));
    }

    [Fact]
    public void RejectsForeignTaskFutureEpochUnknownDuplicateAndUnboundedReports()
    {
        var lease = new Lease(Guid.NewGuid(), 2); var report = new TaskStepReport("s1", "active", "visible", lease, "frame");
        foreach (var invalid in new[]
        {
            report with { Lease = new(Guid.NewGuid(), 2) }, report with { Lease = new(lease.TaskId, 3) }, report with { Lease = new(lease.TaskId, -1) },
            report with { StepId = "s8" }, report with { Status = "done" }, report with { Observation = "" }, report with { Observation = new string('x', 301) },
            report with { FrameId = "" }, report with { FrameId = new string('f', 81) }
        }) Assert.Throws<ArgumentException>(() => TaskPlanTracking.Validate(Plan, [invalid], lease));
        Assert.Throws<ArgumentException>(() => TaskPlanTracking.Validate(Plan, [report, report], lease));
        Assert.Throws<ArgumentException>(() => TaskPlanTracking.Validate(null, [report], lease));
        Assert.Throws<ArgumentException>(() => TaskPlanTracking.Validate(Plan, default, lease));
        Assert.Throws<ArgumentException>(() => TaskPlanTracking.Validate(Plan, [null!], lease));
    }

    [Fact]
    public void ApplyingUnknownOrDuplicateUpdatesCannotMutateValidProgress()
    {
        var lease = new Lease(Guid.NewGuid(), 0); var frame = Frame(lease);
        ImmutableArray<TaskStepReport> initial = [new("s1", "active", "visible", lease, frame.Id)];
        foreach (var update in new[] { new PlanStepUpdate("s8", "completed", "unknown"), new("s1", "done", "visible"), new("s1", "active", ""), new("s1", "pending", new string('x', 301)) })
            Assert.Throws<ArgumentException>(() => TaskPlanTracking.Apply(Plan, initial, [update], frame));
        Assert.Throws<ArgumentException>(() => TaskPlanTracking.Apply(Plan, initial, [new("s1", "active", "one"), new("s1", "completed", "two")], frame));
        Assert.Throws<ArgumentException>(() => TaskPlanTracking.Apply(Plan, initial, default, frame));
        Assert.Throws<ArgumentException>(() => TaskPlanTracking.Apply(Plan, initial, [null!], frame));
        Assert.Equal("active", initial[0].Status);
    }

    [Fact]
    public void LocallyConstructedReportsAndUpdatesStillRejectInvalidSingleLineUnicode()
    {
        var lease = new Lease(Guid.NewGuid(), 0); var frame = Frame(lease);
        foreach (string text in new[] { "bad\nline", "bad\u001b", "bad\u2028", "bad\ud800", "bad\udc00" })
        {
            Assert.Throws<ArgumentException>(() => TaskPlanTracking.Validate(Plan, [new("s1", "active", text, lease, "frame")], lease));
            Assert.Throws<ArgumentException>(() => TaskPlanTracking.Validate(Plan, [new("s1", "active", "visible", lease, text)], lease));
            Assert.Throws<ArgumentException>(() => TaskPlanTracking.Apply(Plan, [], [new("s1", "active", text)], frame));
        }
        var valid = TaskPlanTracking.Apply(Plan, [], [new("s1", "active", "中文与 🌈 都有效")], frame);
        TaskPlanTracking.Validate(Plan, valid, lease);
    }

    [Fact]
    public void DescriptionShowsPlanOrderActualFeedbackAndUnfinishedSteps()
    {
        var lease = new Lease(Guid.NewGuid(), 0);
        ImmutableArray<TaskStepReport> reports = [new("s2", "active", "保存对话框仍打开", lease, "frame")];
        string description = TaskPlanTracking.Describe(Plan, reports);
        Assert.Contains(Plan.Reply, description); Assert.Contains("1. [待完成] 创建文件", description);
        Assert.Contains("2. [进行中] 写入并保存", description); Assert.Contains("保存对话框仍打开", description);
        Assert.Equal("", TaskPlanTracking.Describe(null, []));
        Assert.Equal("旧回复", TaskPlanTracking.Describe(new("旧目标", "旧回复"), []));
        Assert.DoesNotContain("保存对话框", reports[0].ToString());
    }
}
