using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Tests.Protocol;

public sealed class PlanStepUpdateProtocolTests
{
    private static ProposalScope Scope(int version = 2, ProposalRequestKind kind = ProposalRequestKind.General) =>
        new(Guid.Parse("4ce8bfe9-1f86-48cb-bba4-9874d9d975bb"), 3, "frame-current", kind, version);
    private static JsonObject Envelope(ProposalScope scope, object? decision = null) => JsonSerializer.SerializeToNode(new
    {
        schemaVersion = scope.SchemaVersion, proposalId = "test-proposal", taskId = scope.TaskId, epoch = scope.Epoch,
        frameId = scope.FrameId, current = "实际观察", next = "下一步", decision = decision ?? new { kind = "wait", milliseconds = 100, reason = "菜单正在展开" }
    })!.AsObject();
    private static JsonObject WithUpdates(params object[] updates)
    { var root = Envelope(Scope()); root["planUpdates"] = JsonSerializer.SerializeToNode(updates); return root; }

    [Fact]
    public void ParsesOptionalTopLevelUpdatesAndKeepsOldEnvelopesCompatible()
    {
        Assert.Empty(ProposalParser.Parse(Envelope(Scope()).ToJsonString(), Scope()).PlanUpdates);
        var root = WithUpdates(new { stepId = "s1", status = "completed", observation = "当前菜单已经显示" },
            new { stepId = "s2", status = "active", observation = "子菜单尚未展开" });
        var result = ProposalParser.Parse(root.ToJsonString(), Scope());
        Assert.Equal(2, result.PlanUpdates.Length); Assert.Equal("completed", result.PlanUpdates[0].Status);
        Assert.IsType<WaitDecision>(result.Decision);
        Assert.Empty(ProposalParser.Parse(WithUpdates().ToJsonString(), Scope()).PlanUpdates);
    }

    [Theory]
    [InlineData("s0")]
    [InlineData("s9")]
    [InlineData("S1")]
    [InlineData("s10")]
    [InlineData("unknown")]
    public void RejectsOutOfSchemaStepReferences(string id)
        => Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(WithUpdates(new { stepId = id, status = "active", observation = "visible" }).ToJsonString(), Scope()));

    [Theory]
    [InlineData("done")]
    [InlineData("Completed")]
    [InlineData("blocked")]
    [InlineData("")]
    public void StatusIsExactlyOneOfTheThreeProtocolStates(string status)
        => Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(WithUpdates(new { stepId = "s1", status, observation = "visible" }).ToJsonString(), Scope()));

    [Fact]
    public void RejectsDuplicateMissingNestedAndAuthorityFields()
    {
        var item = new { stepId = "s1", status = "completed", observation = "visible" };
        Assert.Equal("INVALID_PLAN_STEP", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(WithUpdates(item, item).ToJsonString(), Scope())).Code);
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(WithUpdates(new { stepId = "s1", status = "active" }).ToJsonString(), Scope()));
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(WithUpdates(new { stepId = "s1", status = "active", observation = "visible", verified = true }).ToJsonString(), Scope()));
        var nested = Envelope(Scope()); nested["decision"]!["planUpdates"] = JsonSerializer.SerializeToNode(new[] { item });
        Assert.Equal("UNKNOWN_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(nested.ToJsonString(), Scope())).Code);
        var repeatedField = WithUpdates(item).ToJsonString().Replace("\"stepId\":\"s1\"", "\"stepId\":\"s1\",\"stepId\":\"s2\"", StringComparison.Ordinal);
        Assert.Equal("DUPLICATE_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(repeatedField, Scope())).Code);
    }

    [Fact]
    public void EnforcesBoundedNonemptySingleLineObservationsAndUpdateCount()
    {
        var valid = Enumerable.Range(1, 8).Select(i => (object)new { stepId = "s" + i, status = "pending", observation = new string('x', 300) }).ToArray();
        Assert.Equal(8, ProposalParser.Parse(WithUpdates(valid).ToJsonString(), Scope()).PlanUpdates.Length);
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(WithUpdates(valid.Append(valid[0]).ToArray()).ToJsonString(), Scope()));
        foreach (string observation in new[] { "", "  ", new string('x', 301), "bad\nline", "bad\u001b", "bad\u2028" })
            Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(WithUpdates(new { stepId = "s1", status = "active", observation }).ToJsonString(), Scope()));
        var invalidUnicode = WithUpdates(new { stepId = "s1", status = "active", observation = "placeholder" }).ToJsonString().Replace("placeholder", "\\ud800", StringComparison.Ordinal);
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(invalidUnicode, Scope()));
    }

    [Theory]
    [InlineData(1, ProposalRequestKind.General)]
    [InlineData(2, ProposalRequestKind.DraftFocusCheck)]
    [InlineData(2, ProposalRequestKind.DraftCheck)]
    [InlineData(2, ProposalRequestKind.CommitCheck)]
    [InlineData(2, ProposalRequestKind.CommitResult)]
    public void UpdatesCannotEnterV1OrMessageTransactionStages(int version, ProposalRequestKind kind)
    {
        var scope = Scope(version, kind);
        object decision = kind switch
        {
            ProposalRequestKind.DraftFocusCheck => new { kind = "draft_focus_check", recipientMatches = false, draftEmpty = false, focusInDraft = false, reason = "unverified" },
            ProposalRequestKind.DraftCheck => new { kind = "draft_check", recipientMatches = false, draftMatches = false, reason = "unverified" },
            ProposalRequestKind.CommitCheck => new { kind = "commit_check", recipientMatches = false, draftMatches = false, reason = "unverified" },
            ProposalRequestKind.CommitResult => new { kind = "commit_result", status = "uncertain", evidence = new[]
                { new { frameId = scope.FrameId, appName = "fixture", observedText = "unverified", interpretation = "uncertain" } }, reason = "unverified" },
            _ => new { kind = "wait", milliseconds = 100, reason = "waiting" }
        };
        var root = Envelope(scope, decision); root["planUpdates"] = new JsonArray();
        Assert.Equal("PLAN_UPDATES_NOT_ALLOWED", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), scope)).Code);
    }

    [Fact]
    public void EnvelopeStillRejectsForeignTaskFutureEpochAndOldFrameBeforeReportsCanBeApplied()
    {
        foreach (string field in new[] { "taskId", "epoch", "frameId" })
        {
            var root = WithUpdates(new { stepId = "s1", status = "completed", observation = "claimed visible" });
            root[field] = field switch { "taskId" => JsonValue.Create(Guid.NewGuid()), "epoch" => JsonValue.Create(4), _ => JsonValue.Create("old-frame") };
            Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope()));
        }
    }
}
