using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopAgent.NativeFixture;

public partial class MainWindow
{
    internal async Task<bool> RunSelfCheckAsync(string reportPath, string? renderPath)
    {
        var checks = new List<SelfCheckResult>();
        var layouts = new List<FixtureLayoutSample>();
        string? failure = null;
        EvidenceModeText.Text = "自检：IN_PROCESS_ROUTED_EVENTS；程序内合成 WPF 事件，未注入系统键鼠，未调用模型。";
        void Check(string name, bool pass, string observed)
        {
            checks.Add(new SelfCheckResult(name, pass, observed));
            Log($"SELF_CHECK {(pass ? "PASS" : "FAIL")} {name}: {observed}");
        }

        try
        {
            await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.ApplicationIdle);
            Check("visible_native_window", IsVisible && IsLoaded && TargetButton.ActualWidth > 0, $"visible={IsVisible}; loaded={IsLoaded}; buttonWidth={TargetButton.ActualWidth:0.0}");
            ClearCounters_Click(this, new RoutedEventArgs());

            // Resize and arrange the actual WPF controls, not a parallel layout model.
            foreach (var size in new[] { (Width: 940d, Height: 720d), (Width: 1120d, Height: 820d), (Width: 1280d, Height: 900d) })
            {
                Width = size.Width;
                Height = size.Height;
                UpdateLayout();
                bool valid = true;
                var fingerprints = new HashSet<string>(StringComparer.Ordinal);
                for (int seed = 1; seed <= 32; seed++)
                {
                    _layoutSeed = seed;
                    ArrangeTargets();
                    UpdateLayout();
                    FixtureSnapshot snapshot = Snapshot();
                    valid &= LayoutIsValid(snapshot);
                    fingerprints.Add(string.Join(";", snapshot.Targets.Select(item => $"{item.Name}:{item.X:R}:{item.Y:R}")));
                    if (seed is 1 or 32)
                    {
                        layouts.Add(new FixtureLayoutSample(size.Width, size.Height, snapshot.CanvasWidth, snapshot.CanvasHeight, seed, snapshot.Targets));
                    }
                }
                Check($"random_layout_{size.Width:0}x{size.Height:0}", valid && fingerprints.Count == 32,
                    $"32 seeds; unique={fingerprints.Count}; all targets bounded and non-overlapping={valid}");
            }
            Width = 1120;
            Height = 820;
            UpdateLayout();
            _layoutSeed = 20260905;
            ArrangeTargets();
            UpdateLayout();

            TargetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check("button_click_routed", CountOf("button_click") == 1, $"button_click={CountOf("button_click")}");
            TargetButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            Check("double_click_routed", CountOf("double_click") == 1, $"double_click={CountOf("double_click")}; no OS double-click timing tested");
            TargetButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check("right_button_routed", CountOf("right_down") == 1, $"right_down={CountOf("right_down")}");
            TargetButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            TargetButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Check("left_button_down_up_routed", CountOf("left_down") == 1 && CountOf("left_up") == 1, $"left_down={CountOf("left_down")}; left_up={CountOf("left_up")}");
            TargetButton.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            TargetButton.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check("wheel_routed", CountOf("wheel") == 2 && CountOf("wheel_delta") == 0, $"events={CountOf("wheel")}; delta={CountOf("wheel_delta")}");

            Point original = new(Canvas.GetLeft(DragTarget), Canvas.GetTop(DragTarget));
            double dx = Canvas.GetLeft(DropZone) + DropZone.Width / 2 - (original.X + DragTarget.Width / 2);
            double dy = Canvas.GetTop(DropZone) + DropZone.Height / 2 - (original.Y + DragTarget.Height / 2);
            DragTarget.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            DragTarget.RaiseEvent(new DragDeltaEventArgs(dx, dy) { RoutedEvent = Thumb.DragDeltaEvent });
            DragTarget.RaiseEvent(new DragCompletedEventArgs(dx, dy, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Check("drag_drop_routed", CountOf("drag_started") == 1 && CountOf("drag_completed") == 1 && CountOf("drop_hit") == 1,
                $"started={CountOf("drag_started")}; completed={CountOf("drag_completed")}; hits={CountOf("drop_hit")}");
            Check("drag_returns_to_original_layout", Canvas.GetLeft(DragTarget) == original.X && Canvas.GetTop(DragTarget) == original.Y && LayoutIsValid(Snapshot()),
                "drag source returns to its layout slot after completion");

            const string plainText = "中文输入 🧪 / Unicode";
            PlainTextBox.Text = plainText;
            var composition = new TextComposition(InputManager.Current, PlainTextBox, plainText);
            PlainTextBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            Check("ordinary_text_control_and_routed_event", PlainTextBox.Text == plainText && CountOf("plain_text_changed") > 0 && CountOf("text_input") == 1,
                $"property retains Unicode; text_changed={CountOf("plain_text_changed")}; text_input={CountOf("text_input")}; no OS text injection tested");

            Check("same_name_distinct_contacts", _contacts[0].Name == _contacts[1].Name && _contacts[0].Id != _contacts[1].Id && _contacts[0].Identity != _contacts[1].Identity,
                $"{_contacts[0].DisplayLabel} / {_contacts[1].DisplayLabel}");
            ContactPicker.SelectedIndex = 0;
            DraftBox.Text = "同学草稿：稍后见 🧪";
            ContactPicker.SelectedIndex = 1;
            Check("preexisting_draft_restored", DraftBox.Text == "保留的测试草稿（虚构）", $"draft={DraftBox.Text}");
            DraftBox.Text = "同事草稿：请查收";
            ContactPicker.SelectedIndex = 0;
            Check("contact_drafts_isolated", DraftBox.Text == "同学草稿：稍后见 🧪" && _contacts[1].Draft == "同事草稿：请查收",
                $"student={_contacts[0].Draft}; colleague={_contacts[1].Draft}");

            var source = PresentationSource.FromVisual(this) ?? throw new InvalidOperationException("Visible WPF window has no PresentationSource.");
            void RaiseKey(UIElement target, Key key, ModifierKeys modifiers, bool repeat = false)
            {
                target.RaiseEvent(new SelfCheckKeyEventArgs(source, key, modifiers, repeat) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }
            var sendKeys = new[]
            {
                (Key: Key.Enter, Modifiers: ModifierKeys.None, Trigger: "ENTER"),
                (Key: Key.Enter, Modifiers: ModifierKeys.Control, Trigger: "CTRL+ENTER"),
                (Key: Key.S, Modifiers: ModifierKeys.Alt, Trigger: "ALT+S")
            };

            PlainTextBox.Focus();
            int sentBeforeOutsideKeys = _sentCount;
            foreach (var item in sendKeys) RaiseKey(PlainTextBox, item.Key, item.Modifiers);
            Check("send_shortcuts_scoped_to_draft", _sentCount == sentBeforeOutsideKeys && DraftBox.Text == "同学草稿：稍后见 🧪",
                $"raising all three shortcuts on ordinary input sent {_sentCount - sentBeforeOutsideKeys} messages");

            DraftBox.Focus();
            foreach (var item in sendKeys)
            {
                string message = $"仅一次发送：{item.Trigger} 中文 🧪";
                DraftBox.Text = message;
                int before = _sentCount;
                RaiseKey(DraftBox, item.Key, item.Modifiers);
                Check($"send_once_{item.Trigger}", _sentCount == before + 1 && DraftBox.Text.Length == 0 && _contacts[0].Messages[^1].Text == message && _contacts[0].Messages[^1].Trigger == item.Trigger,
                    $"send delta={_sentCount - before}; draft length={DraftBox.Text.Length}; exact text retained");
                DraftBox.Text = "按住快捷键不可再发送";
                before = _sentCount;
                RaiseKey(DraftBox, item.Key, item.Modifiers, repeat: true);
                Check($"repeat_suppressed_{item.Trigger}", _sentCount == before && DraftBox.Text == "按住快捷键不可再发送",
                    $"repeat send delta={_sentCount - before}; nonempty draft retained");
            }

            int beforeOtherModifiers = _sentCount;
            RaiseKey(DraftBox, Key.Enter, ModifierKeys.Shift);
            RaiseKey(DraftBox, Key.S, ModifierKeys.None);
            RaiseKey(DraftBox, Key.S, ModifierKeys.Control | ModifierKeys.Alt);
            Check("other_modifiers_do_not_send", _sentCount == beforeOtherModifiers, $"send delta={_sentCount - beforeOtherModifiers}");

            DraftBox.Text = "按钮只发送一次（虚构）";
            int beforeButton = _sentCount;
            SendButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            SendButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check("send_button_and_empty_duplicate", _sentCount == beforeButton + 1 && DraftBox.Text.Length == 0,
                $"two Click events with one initial draft produced {_sentCount - beforeButton} message");

            ContactPicker.SelectedIndex = 1;
            Check("other_contact_unchanged_after_sends", DraftBox.Text == "同事草稿：请查收" && _contacts[1].Messages.Count == 0,
                $"colleague messages={_contacts[1].Messages.Count}; draft={DraftBox.Text}");
            ContactPicker.SelectedIndex = 0;
            Check("visible_history_matches_sent_count", _contacts.Sum(contact => contact.Messages.Count) == _sentCount && _sentCount == 4 && ChatHistory.Items.Count == 5,
                $"total={_sentCount}; selected visible rows={ChatHistory.Items.Count}; rows include one fixture notice");
            Check("key_events_reached_window", CountOf("key_down") >= 12, $"window preview key_down={CountOf("key_down")}");
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.ApplicationIdle);
            if (renderPath is not null)
            {
                RenderOwnWindow(renderPath);
                Check("wpf_self_render_written", File.Exists(renderPath) && new FileInfo(renderPath).Length > 0, "WPF RenderTargetBitmap of fixture client content; not a desktop capture");
            }
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            checks.Add(new SelfCheckResult("unhandled_self_check_exception", false, failure));
        }

        bool passed = checks.Count > 0 && checks.All(check => check.Passed);
        var report = new FixtureSelfCheckReport(
            1, "IN_PROCESS_ROUTED_EVENTS", DateTimeOffset.UtcNow, passed,
            false, false, 0, "NOT_RUN", "NOT_RUN", "NOT_RUN",
            "Modifiers are explicit synthetic event metadata. Text is assigned through WPF TextBox.Text, with a separate routed text event. Mouse events do not validate OS timing or screen coordinates.",
            renderPath, renderPath is null ? null : "WPF_SELF_RENDER_NOT_DESKTOP_CAPTURE",
            checks, layouts, Snapshot(), failure);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return passed;
    }

    private static bool LayoutIsValid(FixtureSnapshot snapshot)
    {
        Rect canvas = new(0, 0, snapshot.CanvasWidth, snapshot.CanvasHeight);
        for (int i = 0; i < snapshot.Targets.Count; i++)
        {
            Rect current = snapshot.Targets[i].ToRect();
            if (!canvas.Contains(current) || current.Width <= 0 || current.Height <= 0) return false;
            for (int j = i + 1; j < snapshot.Targets.Count; j++)
            {
                if (current.IntersectsWith(snapshot.Targets[j].ToRect())) return false;
            }
        }
        return true;
    }

    private void RenderOwnWindow(string path)
    {
        var content = (FrameworkElement)Content;
        content.UpdateLayout();
        // Render(content) retains its layout offset, but ActualWidth/Height exclude
        // the root Margin. Include that margin in the client image dimensions.
        // The Window owns the background, so paint it before rendering its child.
        // This remains a WPF visual render; no desktop or other window is captured.
        Thickness margin = content.Margin;
        double clientWidth = content.ActualWidth + margin.Left + margin.Right;
        double clientHeight = content.ActualHeight + margin.Top + margin.Bottom;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(clientWidth), (int)Math.Ceiling(clientHeight), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen())
        {
            drawing.DrawRectangle(Background ?? Brushes.White, null, new Rect(0, 0, clientWidth, clientHeight));
        }
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}

internal sealed record SelfCheckResult(string Name, bool Passed, string Observed);
internal sealed record FixtureLayoutSample(double WindowWidth, double WindowHeight, double CanvasWidth, double CanvasHeight, int Seed, IReadOnlyList<FixtureBounds> Targets);
internal sealed record FixtureSelfCheckReport(
    int SchemaVersion, string EvidenceMode, DateTimeOffset AtUtc, bool Passed,
    bool SystemInputInjected, bool NetworkUsed, int ModelCalls,
    string PhysicalInputAcceptance, string ManualAcceptance, string ModelAcceptance,
    string Limitations, string? RenderPath, string? RenderEvidenceMode,
    IReadOnlyList<SelfCheckResult> Checks, IReadOnlyList<FixtureLayoutSample> LayoutSamples,
    FixtureSnapshot FinalSnapshot, string? Failure);
