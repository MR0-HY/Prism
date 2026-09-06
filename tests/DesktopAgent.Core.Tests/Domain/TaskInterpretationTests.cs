using System.Text.Json;
using DesktopAgent.Core.Domain;

namespace DesktopAgent.Core.Tests.Domain;

public sealed class TaskInterpretationTests
{
    [Fact]
    public void ParsesBoundedPublicExplanationAndPreservesConcreteDefaultsWithoutPrintingThem()
    {
        var interpretation = TaskInterpretation.Parse("""{"goal":"  用计算器计算 17 × 23，保持两个数不变。 ","reply":"我先选 1–100 内的 17 和 23，再用计算器相乘。"}""");
        Assert.Equal("用计算器计算 17 × 23，保持两个数不变。", interpretation.Goal);
        Assert.Contains("17 和 23", interpretation.Reply);
        Assert.DoesNotContain("17", interpretation.ToString());
        interpretation.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"goal\":\"x\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":null}")]
    [InlineData("{\"goal\":3,\"reply\":\"y\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",\"action\":\"click\"}")]
    [InlineData("{\"goal\":\"x\",\"goal\":\"changed\",\"reply\":\"y\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",\"reply\":\"changed\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",\"Goal\":\"changed\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\",}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"y\"} trailing")]
    [InlineData("```json\n{\"goal\":\"x\",\"reply\":\"y\"}\n```")]
    [InlineData("{\"goal\":\"   \",\"reply\":\"y\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"\\u0000\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"\\u001b\"}")]
    [InlineData("{\"goal\":\"x\",\"reply\":\"\\ud800\"}")]
    public void RejectsUnexpectedAuthorityFieldsMalformedOrUnboundedText(string json)
    {
        var error = Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(json));
        Assert.Equal("INVALID_TASK_INTERPRETATION", error.Code);
        Assert.DoesNotContain("changed", error.Message);
    }

    [Fact]
    public void EnforcesExactLimitsAndValidUnicodeEvenWhenConstructedLocally()
    {
        var interpretation = new TaskInterpretation(new string('中', 4000), new string('文', 1000));
        interpretation.Validate();
        Assert.Equal(interpretation, TaskInterpretation.Parse(JsonSerializer.Serialize(new { goal = interpretation.Goal, reply = interpretation.Reply })));
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(JsonSerializer.Serialize(new { goal = new string('x', 4001), reply = "ok" })));
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(JsonSerializer.Serialize(new { goal = "ok", reply = new string('x', 1001) })));
        Assert.Throws<TaskInterpretationException>(() => TaskInterpretation.Parse(new string(' ', 32769)));
        Assert.Throws<TaskInterpretationException>(() => (interpretation with { Reply = "invalid\ud800" }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (interpretation with { Goal = "invalid\udc00" }).Validate());
        Assert.Throws<TaskInterpretationException>(() => (interpretation with { Reply = "escape\u001b" }).Validate());
        var valid = TaskInterpretation.Parse("""{"goal":"目标 🌈","reply":"第一行\n第二行\t普通说明"}""");
        valid.Validate();
        Assert.Contains("🌈", valid.Goal);
        Assert.Contains('\n', valid.Reply);
    }
}
