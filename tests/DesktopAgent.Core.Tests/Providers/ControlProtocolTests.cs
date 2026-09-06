using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Tests.Providers;

public sealed class ControlProtocolTests
{
    private static readonly Guid TaskId = Guid.Parse("c02e6c01-2db5-4044-8de8-9d27db1d3c15");
    private const string Json = """{"schemaVersion":2,"proposalId":"p1","taskId":"c02e6c01-2db5-4044-8de8-9d27db1d3c15","epoch":0,"frameId":"frame","current":"找到设置","next":"切换","decision":{"kind":"act_control","snapshotId":"snapshot","controlId":"c1","button":"left","clickCount":1,"target":"普通设置开关","expected":"开关状态变化"}}""";
    private static ProposalScope Scope => new(TaskId, 0, "frame", SchemaVersion: 2);
    [Fact]
    public void V2ControlTargetParsesOnlyInExplicitScope()
    {
        var parsed = ProposalParser.Parse(Json, Scope);
        Assert.IsType<ControlActDecision>(parsed.Decision);
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(Json, Scope with { SchemaVersion = 1 }));
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(Json.Replace("\"schemaVersion\":2", "\"schemaVersion\":1"), Scope with { SchemaVersion = 1 }));
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(Json, Scope with { RequestKind = ProposalRequestKind.CommitCheck }));
    }
    [Theory]
    [InlineData("\"clickCount\":1", "\"clickCount\":3")]
    [InlineData("\"controlId\":\"c1\"", "\"controlId\":\"c1\",\"controlId\":\"c2\"")]
    [InlineData("\"controlId\":\"c1\"", "\"controlId\":\"c1\",\"Invoke\":true")]
    [InlineData("\"controlId\":\"c1\"", "\"controlId\":\"\"")]
    [InlineData("\"frameId\":\"frame\"", "\"frameId\":\"old\"")]
    public void ExtraDuplicateInvalidAndStaleTargetsFail(string before, string after)
        => Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(Json.Replace(before, after), Scope));

    [Fact]
    public void SnapshotRejectsWrongObservationUnboundedOrOffscreenCandidates()
    {
        var rect = new PhysicalRect(0, 0, 1000, 800);
        var frame = new Frame("frame", new(TaskId, 0), DateTimeOffset.UtcNow, 1, "display", rect,
            new(1000, 800, "image/png", [1]), FrameViewKind.Overview, new("1", 100, "fixture", rect), []);
        var candidate = new ControlCandidate("c1", null, "设置", "Button", new(10, 10, 100, 30), true, false, true, null, null);
        var snapshot = new ControlSnapshot(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow, ControlSnapshotStatus.Available, [candidate]);
        snapshot.Validate(frame);
        var task = new ModelTaskSnapshot(frame.Lease, "Find a setting", TaskState.Running, "profile", "display", TaskBudget.Default, new(0, 0, 0, null, null));
        var request = new ModelRequest(task, frame, [], "Return JSON.", Scope, controls: snapshot);
        Assert.Same(snapshot, request.Controls);
        Assert.Throws<ArgumentException>(() => new ModelRequest(task, frame, [], "Return JSON.", Scope with { SchemaVersion = 1 }, controls: snapshot));
        Assert.Throws<ArgumentException>(() => new ModelRequest(task, frame, [], "Return JSON.", Scope, controls: snapshot with { FrameId = "old" }));
        Assert.Throws<ArgumentException>(() => (snapshot with { FrameId = "old" }).Validate(frame));
        Assert.Throws<ArgumentException>(() => (snapshot with { Candidates = [candidate, candidate] }).Validate(frame));
        Assert.Throws<ArgumentException>(() => (snapshot with { Candidates = [candidate with { Bounds = new(-1, 0, 30, 30) }] }).Validate(frame));
        Assert.Throws<ArgumentException>(() => (snapshot with { Candidates = [candidate with { Name = new string('a', 129) }] }).Validate(frame));
        Assert.Throws<ArgumentException>(() => (snapshot with { Status = ControlSnapshotStatus.TimedOut }).Validate(frame));
    }
}
