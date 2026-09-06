using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Tests.Domain;

public sealed class ModelRequestTests
{
    private static readonly Lease Lease = new(Guid.Parse("aa836e73-07d8-4aab-8c2f-efb724b6e217"), 3);

    [Fact]
    public void RequestRetainsTheCurrentCropAndOptionalMatchingOverviewWithImageBytes()
    {
        var overview = Frame("overview", new PhysicalRect(-1920, 0, 1920, 1080));
        var crop = Frame("crop", new PhysicalRect(-1800, 100, 200, 200), FrameViewKind.Crop, "overview");
        var request = Request(crop, overview);
        Assert.Same(crop, request.CurrentFrame);
        Assert.Same(overview, request.OverviewContext);
        Assert.Equal("crop", request.Scope.FrameId);
        Assert.False(request.CurrentFrame.Image.Bytes.IsEmpty);
    }

    [Fact]
    public void RequestRejectsMixedTaskEpochFrameAndOverviewMetadata()
    {
        var current = Frame("current", new PhysicalRect(0, 0, 100, 100));
        Assert.Throws<ArgumentException>(() => new ModelRequest(Snapshot(), current, [], "prompt",
            new ProposalScope(Guid.NewGuid(), Lease.Epoch, current.Id)));
        Assert.Throws<ArgumentException>(() => new ModelRequest(Snapshot(), current, [], "prompt",
            new ProposalScope(Lease.TaskId, Lease.Epoch + 1, current.Id)));
        Assert.Throws<ArgumentException>(() => new ModelRequest(Snapshot(), current, [], "prompt",
            new ProposalScope(Lease.TaskId, Lease.Epoch, "old-frame")));
        Assert.Throws<ArgumentException>(() => Request(current, current));
        var crop = Frame("crop", new PhysicalRect(0, 0, 100, 100), FrameViewKind.Crop, "other");
        Assert.Throws<ArgumentException>(() => Request(crop, Frame("offscreen-overview", new PhysicalRect(-300, 0, 100, 100))));
    }

    [Fact]
    public void FrameOwnsItsBytesAndDoesNotSerializeOrPrintThem()
    {
        byte[] input = [21, 22, 23, 24];
        var image = new FrameImage(2, 2, "image/png", input);
        input[0] = 99;
        Assert.Equal(21, image.Bytes.Span[0]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(image));
        Assert.False(json.RootElement.TryGetProperty("Bytes", out _));
        Assert.DoesNotContain(Convert.ToBase64String(image.Bytes.Span), image.ToString());
        Assert.Throws<ArgumentException>(() => new FrameImage(1, 1, "image/png", []));
    }

    [Fact]
    public void HistoryIsBoundedToEightResultsFromTheSameTask()
    {
        var frame = Frame("current", new PhysicalRect(0, 0, 100, 100));
        var result = new ActionResult("p1", Lease.TaskId, 2, ActionStatus.Observed, null,
            DateTimeOffset.UtcNow, 0, 0, 0, "synthetic", null);
        Assert.Throws<ArgumentException>(() => Request(frame, history: Enumerable.Repeat(result, 9).ToImmutableArray()));
        Assert.Throws<ArgumentException>(() => Request(frame, history: [result with { TaskId = Guid.NewGuid() }]));
        Assert.Throws<ArgumentException>(() => Request(frame, history: [result with { Epoch = 4 }]));
        Assert.Equal(8, Request(frame, history: Enumerable.Repeat(result, 8).ToImmutableArray()).RecentResults.Length);
    }

    [Fact]
    public void UntrustedObservationsAcceptOnlyBoundedSameTaskReportsFromCurrentOrEarlierEpochs()
    {
        var frame = Frame("current", new PhysicalRect(0, 0, 100, 100));
        var observation = new UntrustedModelObservation(new(Lease.TaskId, 2), "historical-frame", "reported-original", "proposed-stage");
        var request = Request(frame, observations: [observation]);
        Assert.False(request.UntrustedModelObservations[0].Verified); Assert.False(request.UntrustedModelObservations[0].InputAuthority);
        Assert.Equal("current", request.Scope.FrameId); Assert.DoesNotContain("reported-original", observation.ToString());
        Assert.Throws<ArgumentException>(() => Request(frame, observations: Enumerable.Repeat(observation, 13).ToImmutableArray()));
        Assert.Throws<ArgumentException>(() => Request(frame, observations: [observation with { Lease = new(Guid.NewGuid(), 0) }]));
        Assert.Throws<ArgumentException>(() => Request(frame, observations: [observation with { Lease = new(Lease.TaskId, 4) }]));
        Assert.Throws<ArgumentException>(() => Request(frame, observations: [observation with { Lease = new(Lease.TaskId, -1) }]));
        Assert.Throws<ArgumentException>(() => Request(frame, observations: [observation with { FrameId = "" }]));
        Assert.Throws<ArgumentException>(() => Request(frame, observations: [observation with { Current = new string('x', 201) }]));
        Assert.Throws<ArgumentException>(() => Request(frame, observations: [observation with { Next = new string('x', 201) }]));
        Assert.Throws<ArgumentException>(() => Request(frame, observations: [observation with { Current = "bad\nreport" }]));
        Assert.Equal(12, Request(frame, observations: Enumerable.Repeat(observation with { Current = new string('x', 200), Next = new string('y', 200) }, 12)
            .ToImmutableArray()).UntrustedModelObservations.Length);
    }

    [Fact]
    public void SecretPortValueIsRedactedAndOwnedMemoryIsClearedOnDispose()
    {
        using var secret = new SecretValue("synthetic-test-secret");
        var borrowedMemory = secret.Value;
        Assert.DoesNotContain("synthetic-test-secret", secret.ToString());
        Assert.Equal("{}", JsonSerializer.Serialize(secret));
        secret.Dispose();
        Assert.All(borrowedMemory.ToArray(), c => Assert.Equal('\0', c));
        Assert.Throws<ObjectDisposedException>(() => secret.Value);
    }

    private static Frame Frame(string id, PhysicalRect region, FrameViewKind kind = FrameViewKind.Overview, string? parent = null)
        => new(id, Lease, DateTimeOffset.UtcNow, 1, "monitor-test", region, new FrameImage(1, 1, "image/png", [1]),
            kind, new ForegroundIdentity("0x123", 42, "SyntheticFixture", new PhysicalRect(0, 0, 1000, 700)), [], parent);
    private static ModelTaskSnapshot Snapshot() => new(Lease, "synthetic goal", TaskState.Running, "test-fingerprint",
        "monitor-test", TaskBudget.Default, new TaskUsage(0, 0, 0, null, null));
    private static ModelRequest Request(Frame current, Frame? overview = null, ImmutableArray<ActionResult> history = default,
        ImmutableArray<UntrustedModelObservation> observations = default)
        => new(Snapshot(), current, history.IsDefault ? [] : history, "prompt",
            new ProposalScope(Lease.TaskId, Lease.Epoch, current.Id), overview, untrustedModelObservations: observations);
}
