using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;

namespace DesktopAgent.Core.Tests.Domain;

public sealed class StructuredTaskPlanTests
{
    private static string Plan(object steps, string completionCheck = "文件名、内容和保存状态符合原始要求") => JsonSerializer.Serialize(new
    { goal = "创建文件 325.txt，写入 325，保存并关闭", reply = "我会创建并核对文件，保存后关闭。", steps, completionCheck });

    [Fact]
    public void ParsesOrderedOutcomePlanWithoutGrantingCompletionOrExposingItsTextInDiagnostics()
    {
        var value = TaskInterpretation.Parse(Plan(new[]
        {
            new { id = "s1", title = "创建并打开目标文件", completionCheck = "目标文件名为 325.txt，已在编辑器打开" },
            new { id = "s2", title = "写入、保存并关闭", completionCheck = "正文为 325，保存完成，目标编辑器已关闭" }
        }));
        value.Validate();
        Assert.Equal(2, value.Steps.Length); Assert.Equal("s2", value.Steps[1].Id);
        Assert.Contains("保存", value.Steps[1].CompletionCheck); Assert.NotNull(value.CompletionCheck);
        Assert.DoesNotContain("325", value.ToString()); Assert.DoesNotContain("325", value.Steps[0].ToString());
        Assert.DoesNotContain("Completed", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void OldInterpretationRemainsValidButHasNoInventedPlan()
    {
        var value = TaskInterpretation.Parse("""{"goal":"打开计算器","reply":"我会打开计算器。"}""");
        value.Validate(); Assert.Empty(value.Steps); Assert.Null(value.CompletionCheck);
    }

    [Theory]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",\"steps\":[]}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",\"completionCheck\":\"done\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",\"steps\":null,\"completionCheck\":\"done\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",\"steps\":[],\"completionCheck\":\"done\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",\"steps\":[],\"steps\":[],\"completionCheck\":\"done\"}")]
    public void RejectsPartialOrEmptyStructuredPlanInsteadOfSilentlyDroppingIt(string json)
        => Assert.Equal("INVALID_TASK_INTERPRETATION", Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(json)).Code);

    [Theory]
    [InlineData("s0")]
    [InlineData("s2")]
    [InlineData("S1")]
    [InlineData("s01")]
    public void StepIdsAreStableSequentialLocalPlanReferences(string id)
        => Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(Plan(new[] { new { id, title = "目标", completionCheck = "可见结果" } })));

    [Fact]
    public void RejectsDuplicateIdsAuthorityFieldsAndUnknownStepProperties()
    {
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(Plan(new[]
        {
            new { id = "s1", title = "one", completionCheck = "one visible" },
            new { id = "s1", title = "two", completionCheck = "two visible" }
        })));
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(Plan(new[] { new
        { id = "s1", title = "one", completionCheck = "visible", status = "completed" } })));
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(Plan(new[] { new
        { id = "s1", title = "one", completionCheck = "visible", action = new { type = "click" } } })));
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse("""{"goal":"x","reply":"y","steps":[{"id":"s1","title":"x","title":"changed","completionCheck":"visible"}],"completionCheck":"visible"}"""));
    }

    [Fact]
    public void EnforcesStepAndTextLimitsAtParsingAndLocalConstructionBoundaries()
    {
        var steps = Enumerable.Range(1, TaskInterpretation.MaximumSteps).Select(i => new TaskPlanStep("s" + i,
            new string('t', TaskInterpretation.MaximumStepTitleLength), new string('c', TaskInterpretation.MaximumStepCheckLength))).ToImmutableArray();
        var value = new TaskInterpretation("goal", "reply") { Steps = steps, CompletionCheck = new string('f', TaskInterpretation.MaximumCompletionCheckLength) };
        value.Validate();
        var parsed = TaskInterpretation.Parse(Plan(steps.Select(s => new { id = s.Id, title = s.Title, completionCheck = s.CompletionCheck }), value.CompletionCheck));
        Assert.Equal(8, parsed.Steps.Length);
        Assert.Throws<TaskInterpretationException>(() => (value with { Steps = steps.Add(new("s9", "title", "check")) }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (value with { Steps = steps.SetItem(0, steps[0] with { Title = new string('x', 161) }) }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (value with { Steps = steps.SetItem(0, steps[0] with { CompletionCheck = new string('x', 301) }) }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (value with { CompletionCheck = new string('x', 601) }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (value with { Steps = [] }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (value with { CompletionCheck = null }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (value with { Steps = default }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (value with { Steps = steps.SetItem(0, null!) }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (value with { Steps = steps.SetItem(0, steps[0] with { CompletionCheck = "bad\ud800" }) }).Validate());
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(Plan(new[] { new { id = "s1", title = " ", completionCheck = "check" } })));
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(Plan(new[] { new { id = "s1", title = "title", completionCheck = new string('x', 301) } })));
    }
}
