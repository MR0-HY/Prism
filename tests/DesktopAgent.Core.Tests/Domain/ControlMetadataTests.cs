using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;

namespace DesktopAgent.Core.Tests.Domain;

public sealed class ControlMetadataTests
{
    private static readonly PhysicalRect Bounds = new(0, 0, 1000, 800);
    private static Frame Frame() => new("frame", new(Guid.NewGuid(), 0), DateTimeOffset.UtcNow, 1, "display", Bounds,
        new(1000, 800, "image/png", [1]), FrameViewKind.Overview, new("1", 100, "fixture", Bounds), []);
    private static ControlCandidate Edit() => new("c1", null, "File name", "Edit", new(10, 10, 300, 30), true, true, true, null, null);
    private static void Validate(ControlCandidate candidate)
    {
        var frame = Frame();
        new ControlSnapshot(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow,
            ControlSnapshotStatus.Available, [candidate]).Validate(frame);
    }

    [Fact]
    public void LegacyCandidatesRemainValidAndUnknownMetadataIsNotInvented()
    {
        var candidate = Edit(); Validate(candidate);
        Assert.Null(candidate.CurrentValue); Assert.Null(candidate.SelectionStart); Assert.Null(candidate.ValueTruncated);
        Assert.Null(candidate.HasSubmenu);
        var restored = JsonSerializer.Deserialize<ControlCandidate>(JsonSerializer.Serialize(candidate));
        Assert.NotNull(restored);
        Assert.Null(restored.CurrentValue); Assert.Null(restored.ValueTruncated);
        Assert.Null(restored.SelectionStart); Assert.Null(restored.SelectionLength);
        Assert.Null(restored.ExpandCollapseState); Assert.Null(restored.HasSubmenu);
    }

    [Theory]
    [InlineData("325.txt", 0, 7)]
    [InlineData("325.txt", 0, 3)]
    [InlineData("", 0, 0)]
    [InlineData("😀.txt", 0, 6)]
    public void ExactValuesAndUtf16SelectionsSurviveModelContextSerialization(string text, int start, int length)
    {
        var candidate = Edit() with { CurrentValue = text, ValueTruncated = false, SelectionStart = start, SelectionLength = length };
        Validate(candidate);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(candidate, options));
        Assert.Equal(text, json.RootElement.GetProperty("currentValue").GetString());
        Assert.False(json.RootElement.GetProperty("valueTruncated").GetBoolean());
        Assert.Equal(start, json.RootElement.GetProperty("selectionStart").GetInt32());
        Assert.Equal(length, json.RootElement.GetProperty("selectionLength").GetInt32());
    }

    [Fact]
    public void TruncatedValueCannotClaimExactValueOrSelection()
    {
        Validate(Edit() with { ValueTruncated = true });
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { CurrentValue = "325", ValueTruncated = true }));
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { ValueTruncated = true, SelectionStart = 0, SelectionLength = 3 }));
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { CurrentValue = new string('x', 513), ValueTruncated = false }));
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { CurrentValue = "325" }));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 8)]
    [InlineData(6, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void SelectionOutsideExactValueIsRejected(int start, int length) =>
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { CurrentValue = "325.txt", ValueTruncated = false, SelectionStart = start, SelectionLength = length }));

    [Fact]
    public void MissingPairNullValueDocumentContentAndMalformedTextAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { SelectionStart = 0, SelectionLength = 0 }));
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { CurrentValue = "325", ValueTruncated = false, SelectionStart = 0 }));
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { Role = "Document", CurrentValue = "private body", ValueTruncated = false }));
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { CurrentValue = "a\0b", ValueTruncated = false }));
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { CurrentValue = "\uD800", ValueTruncated = false }));
        Assert.Throws<ArgumentException>(() => Validate(Edit() with { CurrentValue = "\uDC00", ValueTruncated = false }));
    }

    [Theory]
    [InlineData("collapsed", true)]
    [InlineData("expanded", true)]
    [InlineData("partiallyExpanded", true)]
    [InlineData("leaf", false)]
    public void SubmenuStateIsExplicitAndConsistent(string state, bool hasSubmenu)
    {
        var candidate = Edit() with { Role = "MenuItem", ExpandCollapseState = state, HasSubmenu = hasSubmenu };
        Validate(candidate);
        Assert.Throws<ArgumentException>(() => Validate(candidate with { HasSubmenu = !hasSubmenu }));
        Assert.Throws<ArgumentException>(() => Validate(candidate with { Role = "Edit" }));
        Assert.Throws<ArgumentException>(() => Validate(candidate with { ExpandCollapseState = "unknown-from-model" }));
    }

    [Fact]
    public void ValuesShareTheSnapshotCharacterBudget()
    {
        var frame = Frame();
        var candidates = Enumerable.Range(0, 24).Select(i => Edit() with { Id = "c" + i, CurrentValue = new string('x', 512), ValueTruncated = false }).ToImmutableArray();
        Assert.Throws<ArgumentException>(() => new ControlSnapshot(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow,
            ControlSnapshotStatus.Available, candidates).Validate(frame));
    }
}
