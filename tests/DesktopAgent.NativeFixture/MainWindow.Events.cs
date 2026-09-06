using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DesktopAgent.NativeFixture;

public partial class MainWindow
{
    private readonly List<FixtureContact> _contacts =
    [
        new("test-student", "顾言", "虚构同学"),
        new("test-colleague", "顾言", "虚构同事", "保留的测试草稿（虚构）"),
        new("test-family", "林小雨", "虚构家人")
    ];
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private FixtureContact? _selectedContact;
    private bool _changingContact;
    private bool _dragActive;
    private Point _dragOrigin;
    private int _layoutSeed = Random.Shared.Next(1, int.MaxValue);
    private int _layoutVersion;
    private int _sentCount;
    private long _lastMoveLogTick;

    public ObservableCollection<string> Logs { get; } = [];

    private void InitializeFixture()
    {
        DataContext = this;
        ContactPicker.ItemsSource = _contacts;
        ContactPicker.SelectedIndex = 0;
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Window_PreviewKeyDown), true);
        AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(Window_PreviewTextInput), true);
        Loaded += (_, _) =>
        {
            ArrangeTargets();
            UpdateCounters();
            Log("READY: 独立原生测试夹具；无联网、无真实收件人。坐标单位 WPF DIP。");
        };
    }

    private int CountOf(string name) => _counts.GetValueOrDefault(name);

    private void Count(string name, int delta = 1)
    {
        _counts[name] = CountOf(name) + delta;
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        if (CountersText is null || DropZoneText is null)
        {
            return;
        }
        CountersText.Text = $"按钮 Click {CountOf("button_click")} · 双击 {CountOf("double_click")} · 右击 {CountOf("right_down")} · 滚轮 {CountOf("wheel")} · 拖拽 {CountOf("drag_completed")} / 命中 {CountOf("drop_hit")}\n" +
            $"左键↓/↑ {CountOf("left_down")}/{CountOf("left_up")} · 移动 {CountOf("mouse_move")} · 按键 {CountOf("key_down")} · 文本事件 {CountOf("text_input")} · 文本变更 {CountOf("plain_text_changed") + CountOf("draft_text_changed")}";
        DropZoneText.Text = $"拖拽靶区\n命中 {CountOf("drop_hit")}";
    }

    private void Log(string message)
    {
        Logs.Add($"{DateTime.Now:HH:mm:ss.fff} {message}");
        while (Logs.Count > 160)
        {
            Logs.RemoveAt(0);
        }
        if (EventLog?.IsLoaded == true)
        {
            EventLog.ScrollIntoView(Logs[^1]);
        }
    }

    private static string FormatPoint(Point point) => string.Create(CultureInfo.InvariantCulture, $"({point.X:0.0}, {point.Y:0.0}) DIP");

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _layoutSeed = Random.Shared.Next(1, int.MaxValue);
        ArrangeTargets();
        Log($"LAYOUT seed={_layoutSeed}; version={_layoutVersion}");
    }

    private void Playground_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TargetButton is not null && PlainTextBox is not null && DragTarget is not null && DropZone is not null)
        {
            if (DragTarget.IsDragging)
            {
                DragTarget.CancelDrag();
            }
            ArrangeTargets();
        }
    }

    private void ArrangeTargets()
    {
        double width = Playground.ActualWidth;
        double height = Playground.ActualHeight;
        if (width < 80 || height < 80)
        {
            return;
        }
        // One target per shuffled cell guarantees separation even at minimum size.
        // Random offsets within each cell prevent a fixed coordinate layout.
        var random = new Random(_layoutSeed);
        int[] cells = [0, 1, 2, 3];
        random.Shuffle(cells);
        const double gap = 12;
        double cellWidth = (width - gap * 3) / 2;
        double cellHeight = (height - gap * 3) / 2;
        (FrameworkElement Element, double Width, double Height)[] elements =
        [
            (TargetButton, 126, 42), (PlainTextBox, 184, 42),
            (DragTarget, 94, 48), (DropZone, 126, 72)
        ];
        for (int i = 0; i < elements.Length; i++)
        {
            var item = elements[i];
            item.Element.Width = Math.Min(item.Width, cellWidth);
            item.Element.Height = Math.Min(item.Height, cellHeight);
            double left = gap + cells[i] % 2 * (cellWidth + gap) + random.NextDouble() * (cellWidth - item.Element.Width);
            double top = gap + cells[i] / 2 * (cellHeight + gap) + random.NextDouble() * (cellHeight - item.Element.Height);
            Canvas.SetLeft(item.Element, left);
            Canvas.SetTop(item.Element, top);
        }
        _layoutVersion++;
        LayoutText.Text = $"布局 {_layoutVersion} · seed {_layoutSeed} · {width:0} × {height:0} DIP";
    }

    private void TargetButton_Click(object sender, RoutedEventArgs e)
    {
        Count("button_click");
        Log($"BUTTON Click #{CountOf("button_click")}; target={FormatPoint(new Point(Canvas.GetLeft(TargetButton), Canvas.GetTop(TargetButton)))}");
    }

    private void TargetButton_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            Count("double_click");
            Log($"DOUBLE_CLICK {FormatPoint(e.GetPosition(Playground))}");
        }
    }

    private void Playground_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) Count("left_down");
        else if (e.ChangedButton == MouseButton.Right) Count("right_down");
        Log($"MOUSE_DOWN {e.ChangedButton} clicks={e.ClickCount} {FormatPoint(e.GetPosition(Playground))}");
    }

    private void Playground_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) Count("left_up");
        Log($"MOUSE_UP {e.ChangedButton} {FormatPoint(e.GetPosition(Playground))}");
    }

    private void Playground_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        Count("mouse_move");
        long now = Environment.TickCount64;
        if (now - _lastMoveLogTick >= 120)
        {
            _lastMoveLogTick = now;
            Log($"MOUSE_MOVE {FormatPoint(e.GetPosition(Playground))}");
        }
    }

    private void Playground_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Count("wheel");
        Count("wheel_delta", e.Delta);
        Log($"WHEEL delta={e.Delta} total={CountOf("wheel_delta")} {FormatPoint(e.GetPosition(Playground))}");
        e.Handled = true;
    }

    private void DragTarget_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragOrigin = new Point(Canvas.GetLeft(DragTarget), Canvas.GetTop(DragTarget));
        _dragActive = true;
        Count("drag_started");
        Log($"DRAG_START {FormatPoint(_dragOrigin)}");
    }

    private void DragTarget_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_dragActive) return;
        Canvas.SetLeft(DragTarget, Math.Clamp(Canvas.GetLeft(DragTarget) + e.HorizontalChange, 0, Playground.ActualWidth - DragTarget.Width));
        Canvas.SetTop(DragTarget, Math.Clamp(Canvas.GetTop(DragTarget) + e.VerticalChange, 0, Playground.ActualHeight - DragTarget.Height));
    }

    private void DragTarget_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_dragActive) return;
        _dragActive = false;
        Point center = new(Canvas.GetLeft(DragTarget) + DragTarget.Width / 2, Canvas.GetTop(DragTarget) + DragTarget.Height / 2);
        bool hit = !e.Canceled && BoundsOf(DropZone).Contains(center);
        Count("drag_completed");
        if (hit) Count("drop_hit");
        Log($"DRAG_END hit={hit}; cancelled={e.Canceled}; {FormatPoint(center)}; source returns to origin");
        Canvas.SetLeft(DragTarget, _dragOrigin.X);
        Canvas.SetTop(DragTarget, _dragOrigin.Y);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Count("key_down");
        Log($"KEY_DOWN {EffectiveKey(e)}; modifiers={EffectiveModifiers(e)}; source={(e.OriginalSource as FrameworkElement)?.Name}");
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        Count("text_input");
        Log($"TEXT_INPUT length={e.Text.Length}; source={(e.OriginalSource as FrameworkElement)?.Name}");
    }

    private void PlainTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Count("plain_text_changed");
        Log($"PLAIN_TEXT_CHANGED length={PlainTextBox.Text.Length}");
    }

    private void ContactPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactPicker.SelectedItem is not FixtureContact contact || DraftBox is null) return;
        if (_selectedContact is not null) _selectedContact.Draft = DraftBox.Text;
        _selectedContact = contact;
        _changingContact = true;
        DraftBox.Text = contact.Draft;
        _changingContact = false;
        ChatHistory.ItemsSource = contact.VisibleHistory;
        RecipientText.Text = $"当前：{contact.DisplayLabel}";
        UpdateSendCount();
        Log($"CONTACT {contact.Id}; restoredDraftLength={contact.Draft.Length}");
    }

    private void DraftBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_changingContact && _selectedContact is not null) _selectedContact.Draft = DraftBox.Text;
        Count("draft_text_changed");
        Log($"DRAFT_CHANGED contact={_selectedContact?.Id}; length={DraftBox.Text.Length}; restoring={_changingContact}");
    }

    private static Key EffectiveKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;
    private static ModifierKeys EffectiveModifiers(KeyEventArgs e) => e is SelfCheckKeyEventArgs synthetic ? synthetic.TestModifiers : Keyboard.Modifiers;

    private void DraftBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Attached only to the draft, never to the Window. Self-check supplies explicit
        // synthetic modifier metadata instead of pressing operating-system keys.
        string? trigger = (EffectiveKey(e), EffectiveModifiers(e)) switch
        {
            (Key.Enter, ModifierKeys.None) => "ENTER",
            (Key.Enter, ModifierKeys.Control) => "CTRL+ENTER",
            (Key.S, ModifierKeys.Alt) => "ALT+S",
            _ => null
        };
        if (trigger is null) return;
        e.Handled = true;
        bool isRepeat = e is SelfCheckKeyEventArgs synthetic ? synthetic.TestRepeat : e.IsRepeat;
        if (isRepeat)
        {
            Log($"SEND_KEY_REPEAT_IGNORED {trigger}");
            return;
        }
        SendDraft(trigger);
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendDraft("BUTTON");

    private void SendDraft(string trigger)
    {
        if (_selectedContact is null || string.IsNullOrWhiteSpace(DraftBox.Text))
        {
            Log($"EMPTY_SEND_IGNORED {trigger}");
            return;
        }
        var message = new FixtureMessage(++_sentCount, _selectedContact.Id, DraftBox.Text, trigger, DateTimeOffset.UtcNow);
        _selectedContact.Messages.Add(message);
        _selectedContact.VisibleHistory.Add($"虚构发送 #{message.Sequence} · {trigger}\n{message.Text}");
        DraftBox.Clear();
        UpdateSendCount();
        ChatHistory.ScrollIntoView(_selectedContact.VisibleHistory[^1]);
        Log($"MOCK_SEND count={_sentCount}; recipient={message.RecipientId}; length={message.Text.Length}; trigger={trigger}; NO_NETWORK");
    }

    private void UpdateSendCount() => SendCountText.Text = $"总发送 {_sentCount} · 此联系人 {_selectedContact?.Messages.Count ?? 0}";

    private void ClearCounters_Click(object sender, RoutedEventArgs e)
    {
        _counts.Clear();
        Logs.Clear();
        UpdateCounters();
        Log("计数已清空；虚构聊天记录和各联系人草稿保留。");
    }

    private static Rect BoundsOf(FrameworkElement element) => new(Canvas.GetLeft(element), Canvas.GetTop(element), element.Width, element.Height);

    internal FixtureSnapshot Snapshot()
    {
        var elements = new FrameworkElement[] { TargetButton, PlainTextBox, DragTarget, DropZone };
        return new FixtureSnapshot(
            "WPF_DIP_RELATIVE_TO_PLAYGROUND", _layoutSeed, _layoutVersion, Playground.ActualWidth, Playground.ActualHeight,
            elements.Select(element => new FixtureBounds(element.Name, BoundsOf(element).X, BoundsOf(element).Y, element.Width, element.Height)).ToArray(),
            new Dictionary<string, int>(_counts), PlainTextBox.Text, _selectedContact?.Id, _sentCount,
            _contacts.Select(contact => new FixtureContactSnapshot(contact.Id, contact.Name, contact.Identity, contact.Draft, contact.Messages.ToArray())).ToArray(),
            Logs.ToArray());
    }
}
