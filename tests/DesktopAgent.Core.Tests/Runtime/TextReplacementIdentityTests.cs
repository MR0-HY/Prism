using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class TextReplacementIdentityTests
{
    private static readonly Lease Lease = new(Guid.NewGuid(), 0);
    private static readonly PhysicalRect Window = new(0, 0, 1000, 800);
    private const string Identity = "native-runtime-private-identity-123";
    private const string Original = "新建 文本文档.txt";
    private static Frame Frame(string id) => new(id, Lease, DateTimeOffset.UtcNow, 1, "display", Window,
        new(1000, 800, "image/png", [1]), FrameViewKind.Overview, new("1", 100, "explorer", Window) { WindowClass = "CabinetWClass" }, []);
    private static ControlCandidate Edit(string value = Original) =>
        new("edit", null, "名称", "Edit", new(100, 100, 240, 30), true, true, true, null, null)
        { LocalIdentity = Identity, CurrentValue = value, ValueTruncated = false, SelectionStart = 0, SelectionLength = value.Length };
    private static ControlSnapshot Snapshot(Frame frame, ControlCandidate editor) =>
        new(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow, ControlSnapshotStatus.Available, [editor]);
    private static TextReplacementPlan Plan(string? identity = Identity)
    {
        var frame = Frame("before"); var controls = Snapshot(frame, Edit() with { LocalIdentity = identity });
        Assert.Null(TextReplacement.Prepare(frame, controls, new("325.txt", Original, controls.Id, "edit"), out var plan));
        return Assert.IsType<TextReplacementPlan>(plan);
    }

    [Fact]
    public void ALocallyIdentifiedEditMayResizeDuringSelectionAndAfterReplacement()
    {
        var plan = Plan(); Assert.Equal(Identity, plan.EditorIdentity);
        var selected = Frame("selected");
        var wider = Edit() with { Id = "fresh-control", Bounds = new(100, 100, 380, 30) };
        Assert.Null(TextReplacement.ValidateSelection(plan, selected, Snapshot(selected, wider)));
        var after = Frame("after");
        var resizedResult = Edit("325.txt") with { Id = "another-fresh-control", Name = "325.txt", Bounds = new(100, 100, 100, 30) };
        Assert.Null(TextReplacement.VerifyResult(plan, after, Snapshot(after, resizedResult)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("another-native-runtime")]
    public void AChangedOrMissingNativeIdentityCannotReuseTheSameBounds(string? identity)
    {
        var plan = Plan(); var selected = Frame("selected");
        Assert.Equal("REPLACEMENT_EDIT_CHANGED", TextReplacement.ValidateSelection(plan, selected,
            Snapshot(selected, Edit() with { LocalIdentity = identity })));
        var after = Frame("after");
        Assert.Equal("REPLACEMENT_EDIT_CHANGED", TextReplacement.VerifyResult(plan, after,
            Snapshot(after, Edit("325.txt") with { LocalIdentity = identity })));
    }

    [Fact]
    public void MatchingIdentityNeverBypassesFocusValueOrFullSelectionChecks()
    {
        var plan = Plan(); var selected = Frame("selected");
        Assert.Equal("REPLACEMENT_FOCUS_NOT_UNIQUE", TextReplacement.ValidateSelection(plan, selected, Snapshot(selected, Edit() with { Focused = false })));
        Assert.Equal("REPLACEMENT_CURRENT_VALUE_CHANGED", TextReplacement.ValidateSelection(plan, selected, Snapshot(selected, Edit("另一个正文"))));
        Assert.Equal("REPLACEMENT_FULL_SELECTION_UNVERIFIED", TextReplacement.ValidateSelection(plan, selected, Snapshot(selected, Edit() with { SelectionLength = 2 })));
        Assert.Equal("REPLACEMENT_RESULT_MISMATCH", TextReplacement.VerifyResult(plan, selected, Snapshot(selected, Edit("325.txt.txt"))));
    }

    [Fact]
    public void LegacyObservationWithoutIdentityStillRequiresUnchangedBounds()
    {
        var plan = Plan(null); var selected = Frame("selected");
        Assert.Null(TextReplacement.ValidateSelection(plan, selected, Snapshot(selected, Edit() with { LocalIdentity = null })));
        Assert.Equal("REPLACEMENT_EDIT_CHANGED", TextReplacement.ValidateSelection(plan, selected,
            Snapshot(selected, Edit() with { LocalIdentity = null, Bounds = new(100, 100, 380, 30) })));
    }

    [Fact]
    public void NativeIdentityIsNeverSerializedToTheModelOrAcceptedFromJson()
    {
        var frame = Frame("model-frame"); var snapshot = Snapshot(frame, Edit());
        var task = new ModelTaskSnapshot(Lease, "重命名测试文件", TaskState.Running, "fixture-profile", "display", TaskBudget.Default, new(0, 0, 0, null, null));
        var request = new ModelRequest(task, frame, [], "fixture protocol", new(Lease.TaskId, Lease.Epoch, frame.Id, SchemaVersion: 2), controls: snapshot);
        string json = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("LocalIdentity", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(Identity, json);
        string candidateJson = JsonSerializer.Serialize(Edit()).TrimEnd('}') + ",\"LocalIdentity\":\"injected-identity\"}";
        var restored = JsonSerializer.Deserialize<ControlCandidate>(candidateJson);
        Assert.NotNull(restored); Assert.Null(restored.LocalIdentity);
        Assert.DoesNotContain(Identity, Edit().ToString()); Assert.DoesNotContain(Identity, Plan().ToString());
    }
}
