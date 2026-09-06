using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Tests.Protocol;

public sealed class ReplacementAndReviewProtocolTests
{
    private static readonly Guid TaskId = Guid.NewGuid();
    private static ProposalScope Scope(int version = 2) => new(TaskId, 0, "current", SchemaVersion: version);
    private static JsonObject Document(bool control = false, int version = 2)
    {
        var decision = control
            ? JsonSerializer.SerializeToNode(new { kind = "act_control", snapshotId = "snapshot", controlId = "edit", button = "left", clickCount = 1, target = "名称", expected = "已聚焦" })!
            : JsonSerializer.SerializeToNode(new { kind = "act", action = new { type = "replace_existing", text = "325.txt", expectedCurrent = "新建 文本文档.txt", snapshotId = "snapshot", controlId = "edit" }, target = "名称", expected = "325.txt" })!;
        return new() { ["schemaVersion"] = version, ["proposalId"] = "p1", ["taskId"] = TaskId.ToString("D"), ["epoch"] = 0,
            ["frameId"] = "current", ["current"] = "核对字段", ["next"] = "核对结果", ["decision"] = decision };
    }
    private static JsonObject Review() => new() { ["recipient"] = "用户指定联系人", ["message"] = "当前已核对完整消息" };

    [Fact]
    public void ReplacementRetainsExactBindingAndDoesNotBecomePlainText()
    {
        var proposal = ProposalParser.Parse(Document().ToJsonString(), Scope());
        var action = Assert.IsType<ReplaceTextAction>(Assert.IsType<ActDecision>(proposal.Decision).Action);
        Assert.Equal(new ReplaceTextAction("325.txt", "新建 文本文档.txt", "snapshot", "edit"), action);
    }

    [Theory]
    [InlineData("snapshotId")]
    [InlineData("controlId")]
    [InlineData("expectedCurrent")]
    [InlineData("text")]
    public void ReplacementRequiresEveryBindingField(string missing)
    {
        var root = Document(); root["decision"]!["action"]!.AsObject().Remove(missing);
        Assert.Equal("MISSING_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
    }

    [Theory]
    [InlineData("text", 513)]
    [InlineData("expectedCurrent", 513)]
    [InlineData("controlId", 33)]
    [InlineData("snapshotId", 81)]
    public void ReplacementFieldsAreBounded(string field, int length)
    {
        var root = Document(); root["decision"]!["action"]![field] = new string('x', length);
        Assert.Equal("INVALID_STRING_LENGTH", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
    }

    [Fact]
    public void ReplacementRequiresV2AndPreservesLegacyText()
    {
        var root = Document(version: 1);
        Assert.Equal("VERSION_MISMATCH", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(1))).Code);
        root["decision"]!["action"] = new JsonObject { ["type"] = "text", ["text"] = "ordinary insertion" };
        Assert.IsType<TextAction>(Assert.IsType<ActDecision>(ProposalParser.Parse(root.ToJsonString(), Scope(1)).Decision).Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalMessageReviewTranscribesButDoesNotApprove(bool control)
    {
        var root = Document(control); root["decision"]!["messageReview"] = Review();
        var proposal = ProposalParser.Parse(root.ToJsonString(), Scope());
        var review = proposal.Decision is ControlActDecision click ? click.MessageReview : Assert.IsType<ActDecision>(proposal.Decision).MessageReview;
        Assert.Equal(new MessageReview("用户指定联系人", "当前已核对完整消息"), review);
        Assert.DoesNotContain("完整消息", review!.ToString());
    }

    [Theory]
    [InlineData("recipient", 201)]
    [InlineData("message", 1001)]
    [InlineData("recipient", 0)]
    [InlineData("message", 0)]
    public void ReviewRequiresCompleteBoundedNonemptyFields(string field, int length)
    {
        var root = Document(); var review = Review(); review[field] = new string('x', length); root["decision"]!["messageReview"] = review;
        Assert.Equal("INVALID_STRING_LENGTH", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
    }

    [Fact]
    public void ReviewRejectsUnknownFieldNullAndDuplicateRecipient()
    {
        var root = Document(); var review = Review(); review["approved"] = true; root["decision"]!["messageReview"] = review;
        Assert.Equal("UNKNOWN_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
        root["decision"]!["messageReview"] = null;
        Assert.Equal("NULL_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
        root["decision"]!["messageReview"] = Review();
        var duplicate = root.ToJsonString().Replace("\"recipient\":", "\"recipient\":\"first\",\"recipient\":", StringComparison.Ordinal);
        Assert.Equal("DUPLICATE_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(duplicate, Scope())).Code);
    }

    [Fact]
    public void ReviewCannotAppearInLegacyOrNonActionDecisions()
    {
        var root = Document(version: 1);
        root["decision"]!["action"] = new JsonObject { ["type"] = "hotkey", ["keys"] = new JsonArray("ENTER") };
        root["decision"]!["messageReview"] = Review();
        Assert.Equal("VERSION_MISMATCH", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(1))).Code);
        root["schemaVersion"] = 2;
        root["decision"] = new JsonObject { ["kind"] = "wait", ["milliseconds"] = 300, ["reason"] = "观察", ["messageReview"] = Review() };
        Assert.Equal("UNKNOWN_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
    }
}
