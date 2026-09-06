using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class TextReplacementTests
{
    private static readonly Lease Lease = new(Guid.NewGuid(), 0);
    private static readonly PhysicalRect Window = new(0, 0, 1000, 800);
    private static Frame Frame(string id = "before", long epoch = 0, int processId = 100, long generation = 1) =>
        new(id, Lease with { Epoch = epoch }, DateTimeOffset.UtcNow, generation, "display", Window,
            new(1000, 800, "image/png", [1]), FrameViewKind.Overview,
            new("1", processId, "explorer", Window) { WindowClass = "CabinetWClass" }, []);
    private static ControlCandidate Edit(string value = "新建 文本文档.txt") =>
        new("edit", null, "名称", "Edit", new(100, 100, 240, 30), true, true, true, null, null)
        { CurrentValue = value, ValueTruncated = false };
    private static ControlSnapshot Snapshot(Frame frame, params ControlCandidate[] controls) =>
        new(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow, ControlSnapshotStatus.Available, [.. controls]);
    private static ReplaceTextAction Action(ControlSnapshot snapshot, string expected = "新建 文本文档.txt", string text = "325.txt") =>
        new(text, expected, snapshot.Id, "edit");
    private static TextReplacementPlan Plan(string expected = "新建 文本文档.txt", string text = "325.txt")
    {
        var frame = Frame();
        var snapshot = Snapshot(frame, Edit(expected));
        Assert.Null(TextReplacement.Prepare(frame, snapshot, Action(snapshot, expected, text), out var plan));
        return Assert.IsType<TextReplacementPlan>(plan);
    }

    [Fact]
    public void ReplacementBindsExactCurrentValueAndVerifiesFullSelectionThenResult()
    {
        var plan = Plan();
        var selected = Frame("selected");
        var fullSelection = Edit() with { SelectionStart = 0, SelectionLength = plan.ExpectedCurrent.Length };
        Assert.Null(TextReplacement.ValidateSelection(plan, selected, Snapshot(selected, fullSelection)));
        var result = Frame("result");
        Assert.Null(TextReplacement.VerifyResult(plan, result, Snapshot(result, Edit("325.txt") with { Name = "325.txt" })));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public void UnknownOrPartialSelectionCannotBecomeAppend(int? start, int? length)
    {
        var plan = Plan(); var frame = Frame("selected");
        Assert.Equal("REPLACEMENT_FULL_SELECTION_UNVERIFIED", TextReplacement.ValidateSelection(plan, frame,
            Snapshot(frame, Edit() with { SelectionStart = start, SelectionLength = length })));
    }

    [Fact]
    public void SelectionLengthUsesUtf16RatherThanUnicodeScalarCount()
    {
        const string value = "😀.txt";
        var plan = Plan(value); var frame = Frame("selected");
        Assert.Equal("REPLACEMENT_FULL_SELECTION_UNVERIFIED", TextReplacement.ValidateSelection(plan, frame,
            Snapshot(frame, Edit(value) with { SelectionStart = 0, SelectionLength = 5 })));
        Assert.Null(TextReplacement.ValidateSelection(plan, frame,
            Snapshot(frame, Edit(value) with { SelectionStart = 0, SelectionLength = 6 })));
    }

    [Fact]
    public void EmptyFieldStillRequiresKnownFocusAndZeroLengthSelection()
    {
        var plan = Plan(""); var frame = Frame("selected");
        Assert.Equal("REPLACEMENT_FULL_SELECTION_UNVERIFIED", TextReplacement.ValidateSelection(plan, frame, Snapshot(frame, Edit(""))));
        Assert.Null(TextReplacement.ValidateSelection(plan, frame,
            Snapshot(frame, Edit("") with { SelectionStart = 0, SelectionLength = 0 })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownOrTruncatedValueCannotBeReplaced(bool truncated)
    {
        var frame = Frame();
        var snapshot = Snapshot(frame, Edit() with { CurrentValue = null, ValueTruncated = truncated ? true : null });
        Assert.Equal("REPLACEMENT_CURRENT_VALUE_UNAVAILABLE", TextReplacement.Prepare(frame, snapshot, Action(snapshot), out var plan));
        Assert.Null(plan);
    }

    [Fact]
    public void OnlyTheIntendedCurrentEditCanBeSelected()
    {
        var frame = Frame(); var snapshot = Snapshot(frame, Edit());
        Assert.Equal("REPLACEMENT_STALE_CONTROL_REFERENCE", TextReplacement.Prepare(frame, snapshot,
            Action(snapshot) with { SnapshotId = "old-snapshot" }, out _));
        Assert.Equal("REPLACEMENT_FOCUS_NOT_UNIQUE", TextReplacement.Prepare(frame, snapshot,
            Action(snapshot) with { ControlId = "other-control" }, out _));
        Assert.Equal("REPLACEMENT_CURRENT_VALUE_CHANGED", TextReplacement.Prepare(frame, snapshot,
            Action(snapshot, "different original"), out _));
        Assert.Equal("REPLACEMENT_ALREADY_MATCHES", TextReplacement.Prepare(frame, snapshot,
            Action(snapshot, text: "新建 文本文档.txt"), out _));
    }

    [Fact]
    public void FocusedNonFocusableRootDoesNotHideTheRealEdit()
    {
        var frame = Frame(); var root = new ControlCandidate("root", null, "文件夹", "Window", Window, true, true, false, null, null);
        var snapshot = Snapshot(frame, root, Edit());
        Assert.Null(TextReplacement.Prepare(frame, snapshot, Action(snapshot), out _));
    }

    private static ControlCandidate SystemMenuBar() =>
        new("system-menu", null, "系统菜单栏", "MenuBar", new(0, 0, 24, 24), true, true, true, null, null);

    [Fact]
    public void NativeSystemMenuBarFocusClaimDoesNotHideTheUniqueRealEdit()
    {
        var before = Frame(); var snapshot = Snapshot(before, SystemMenuBar(), Edit());
        Assert.Null(TextReplacement.Prepare(before, snapshot, Action(snapshot), out var plan));
        var selected = Frame("selected");
        Assert.Null(TextReplacement.ValidateSelection(plan!, selected, Snapshot(selected, SystemMenuBar(),
            Edit() with { SelectionStart = 0, SelectionLength = plan!.ExpectedCurrent.Length })));
        var after = Frame("after");
        Assert.Null(TextReplacement.VerifyResult(plan!, after, Snapshot(after, SystemMenuBar(), Edit("325.txt"))));
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("Edit")]
    [InlineData("MenuItem")]
    [InlineData("Document")]
    public void MenuBarExceptionDoesNotHideASecondRealFocusTarget(string role)
    {
        var before = Frame();
        var other = new ControlCandidate("other", null, "另一个目标", role, new(300, 200, 80, 40), true, true, true, null, null);
        var snapshot = Snapshot(before, SystemMenuBar(), Edit(), other);
        Assert.Equal("REPLACEMENT_FOCUS_NOT_UNIQUE", TextReplacement.Prepare(before, snapshot, Action(snapshot), out _));
        var selected = Frame("selected");
        Assert.Equal("REPLACEMENT_FOCUS_NOT_UNIQUE", TextReplacement.ValidateSelection(Plan(), selected,
            Snapshot(selected, SystemMenuBar(), Edit() with { SelectionStart = 0, SelectionLength = "新建 文本文档.txt".Length }, other)));
    }

    [Fact]
    public void MenuBarAloneCannotAuthorizeTextAndDoesNotExcuseMissingSelection()
    {
        var before = Frame(); var snapshot = Snapshot(before, SystemMenuBar());
        Assert.Equal("REPLACEMENT_FOCUS_NOT_UNIQUE", TextReplacement.Prepare(before, snapshot, Action(snapshot), out _));
        var selected = Frame("selected");
        Assert.Equal("REPLACEMENT_FULL_SELECTION_UNVERIFIED", TextReplacement.ValidateSelection(Plan(), selected,
            Snapshot(selected, SystemMenuBar(), Edit())));
    }

    [Fact]
    public void MultipleFocusableFocusClaimsAreRejected()
    {
        var frame = Frame(); var snapshot = Snapshot(frame, Edit(), Edit() with { Id = "other", Bounds = new(100, 200, 240, 30) });
        Assert.Equal("REPLACEMENT_FOCUS_NOT_UNIQUE", TextReplacement.Prepare(frame, snapshot, Action(snapshot), out _));
    }

    [Fact]
    public void DocumentBodyIsNotAReplaceableEdit()
    {
        var frame = Frame(); var snapshot = Snapshot(frame, Edit() with { Role = "Document", CurrentValue = null, ValueTruncated = null });
        Assert.Equal("REPLACEMENT_EDIT_REQUIRED", TextReplacement.Prepare(frame, snapshot, Action(snapshot), out _));
    }

    [Theory]
    [InlineData("before", 0, 100, 1)]
    [InlineData("after", 1, 100, 1)]
    [InlineData("after", 0, 101, 1)]
    [InlineData("after", 0, 100, 2)]
    public void OldFrameEpochWindowOrDisplayCannotAuthorizeReplacement(string id, long epoch, int pid, long generation)
    {
        var frame = Frame(id, epoch, pid, generation);
        Assert.Equal("REPLACEMENT_FRAME_CHANGED_OR_NOT_FRESH", TextReplacement.ValidateSelection(Plan(), frame, Snapshot(frame, Edit())));
    }

    [Fact]
    public void CurrentValueChangeAfterSelectAllRequiresReplanning()
    {
        var frame = Frame("selected");
        Assert.Equal("REPLACEMENT_CURRENT_VALUE_CHANGED", TextReplacement.ValidateSelection(Plan(), frame,
            Snapshot(frame, Edit("user changed") with { SelectionStart = 0, SelectionLength = 12 })));
    }

    [Fact]
    public void MovedEditorOrChangedFocusCannotReceiveText()
    {
        var frame = Frame("selected");
        Assert.Equal("REPLACEMENT_EDIT_CHANGED", TextReplacement.ValidateSelection(Plan(), frame,
            Snapshot(frame, Edit() with { Bounds = new(101, 100, 240, 30) })));
        Assert.Equal("REPLACEMENT_FOCUS_NOT_UNIQUE", TextReplacement.ValidateSelection(Plan(), frame,
            Snapshot(frame, Edit() with { Focused = false })));
    }

    [Theory]
    [InlineData("新建 文本文档.txt325.txt")]
    [InlineData("325.txt.txt")]
    [InlineData("325")]
    public void AppendedOrWrongSuffixResultIsExplicitMismatch(string actual)
    {
        var frame = Frame("result");
        Assert.Equal("REPLACEMENT_RESULT_MISMATCH", TextReplacement.VerifyResult(Plan(), frame, Snapshot(frame, Edit(actual))));
    }
}
