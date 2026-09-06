using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace DesktopAgent.NativeFixture;

internal sealed class FixtureContact(string id, string name, string identity, string draft = "")
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Identity { get; } = identity;
    public string DisplayLabel => $"{Name} · {Identity} · {Id}";
    public string Draft { get; set; } = draft;
    public List<FixtureMessage> Messages { get; } = [];
    public ObservableCollection<string> VisibleHistory { get; } = ["虚构会话：没有真实联系人，消息仅保存在此进程内。"];
}

internal sealed record FixtureMessage(int Sequence, string RecipientId, string Text, string Trigger, DateTimeOffset AtUtc);
internal sealed record FixtureContactSnapshot(string Id, string Name, string Identity, string Draft, IReadOnlyList<FixtureMessage> Messages);
internal sealed record FixtureBounds(string Name, double X, double Y, double Width, double Height)
{
    public Rect ToRect() => new(X, Y, Width, Height);
}
internal sealed record FixtureSnapshot(
    string CoordinateSpace, int Seed, int LayoutVersion, double CanvasWidth, double CanvasHeight,
    IReadOnlyList<FixtureBounds> Targets, IReadOnlyDictionary<string, int> Counters,
    string PlainText, string? SelectedContactId, int TotalSent,
    IReadOnlyList<FixtureContactSnapshot> Contacts, IReadOnlyList<string> VisibleLogs);

/// <summary>Used only by --self-check. Synthetic arguments do not prove physical keyboard input.</summary>
internal sealed class SelfCheckKeyEventArgs(PresentationSource source, Key key, ModifierKeys modifiers, bool repeat = false)
    : KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
{
    public ModifierKeys TestModifiers { get; } = modifiers;
    public bool TestRepeat { get; } = repeat;
}
