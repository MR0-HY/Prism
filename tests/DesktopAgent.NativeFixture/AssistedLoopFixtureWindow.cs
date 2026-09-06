using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopAgent.NativeFixture;

/// <summary>Independent G4 fixture. Records received clicks; it never raises events or injects input.</summary>
internal sealed class AssistedLoopFixtureWindow : Window
{
    private const double CanvasWidth = 760, CanvasHeight = 540;
    private readonly string _reportPath;
    private readonly int _layout;
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _expires = DateTimeOffset.UtcNow.AddSeconds(150);
    private readonly LoopCanvas _canvas;
    private readonly List<Option> _options = [];
    private readonly List<ClickRecord> _events = [];
    private readonly int _expectedIndex;
    private readonly DispatcherTimer _heartbeat;
    private readonly System.Threading.Timer _hardDeadline;
    private PixelBounds? _canvasBounds;
    private long _hwnd, _sequence, _eventSequence;
    private bool _ready, _closed;

    public AssistedLoopFixtureWindow(string reportPath, int layout)
    {
        if (layout is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(layout));
        _reportPath = Path.GetFullPath(reportPath);
        if (File.Exists(_reportPath)) throw new InvalidOperationException("ASSISTED_FIXTURE_REPORT_ALREADY_EXISTS");
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        _layout = layout;
        _expectedIndex = RandomNumberGenerator.GetInt32(8);
        Title = "原生外观设置夹具";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Background = Brushes.White;
        _canvas = new LoopCanvas(_options) { Width = CanvasWidth, Height = CanvasHeight, Background = Brushes.White };
        Content = _canvas;

        int[] positions = Enumerable.Range(0, 8).ToArray();
        for (int i = positions.Length - 1; i > 0; i--)
        {
            int other = RandomNumberGenerator.GetInt32(i + 1);
            (positions[i], positions[other]) = (positions[other], positions[i]);
        }
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 8; i++)
        {
            string name;
            do { name = RandomName(); } while (!usedNames.Add(name));
            int cell = positions[i];
            double x = layout == 0 ? 43 + cell % 4 * 178 + RandomNumberGenerator.GetInt32(-4, 5)
                : 142 + RandomNumberGenerator.GetInt32(-24, 25);
            double y = layout == 0 ? 146 + cell / 4 * 194 + RandomNumberGenerator.GetInt32(-15, 16)
                : 95 + cell * 51 + RandomNumberGenerator.GetInt32(-3, 4);
            ButtonBase control = layout == 0
                ? new Button { Width = 138, Height = 46, Padding = new Thickness(0), FontSize = 18, Content = name }
                : new CheckBox { Width = 340, Height = 34, FontSize = 18, Content = name, VerticalContentAlignment = VerticalAlignment.Center, IsChecked = false };
            AutomationProperties.SetName(control, name);
            AutomationProperties.SetAutomationId(control, "appearance-" + Guid.NewGuid().ToString("N"));
            Canvas.SetLeft(control, x); Canvas.SetTop(control, y);
            var option = new Option(i, name, control, i == _expectedIndex, x, y, layout);
            _options.Add(option);
            _canvas.Children.Add(control);
            control.Click += (_, _) => ReceivedClick(option);
        }

        SourceInitialized += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _canvas.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
            _hwnd = new WindowInteropHelper(this).Handle.ToInt64();
        };
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _heartbeat.Tick += (_, _) =>
        {
            if (DateTimeOffset.UtcNow >= _expires) { Close(); return; }
            TryWrite();
        };
        // This deadline does not depend on a responsive WPF dispatcher or the driver process.
        _hardDeadline = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        ContentRendered += (_, _) =>
        {
            if (_ready) return;
            UpdateLayout();
            _ready = true;
            TryWrite();
            _heartbeat.Start();
            Activate();
        };
        Closing += (_, _) =>
        {
            _closed = true;
            _heartbeat.Stop();
            if (_ready) { try { Write(); } catch { } }
        };
        Closed += (_, _) => { _heartbeat.Stop(); _hardDeadline.Dispose(); };
    }

    private void ReceivedClick(Option option)
    {
        option.Clicks++;
        option.Checked = option.Control is CheckBox checkbox ? checkbox.IsChecked == true : !option.Checked;
        if (option.Control is Button button) button.Background = option.Checked ? Brushes.PaleGreen : SystemColors.ControlBrush;
        _events.Add(new(++_eventSequence, option.Index, option.Name, option.Checked, DateTimeOffset.UtcNow));
        if (_events.Count > 64) _events.RemoveAt(0);
        _canvas.InvalidateVisual();
        if (_ready) TryWrite();
    }

    private void TryWrite()
    {
        try { Write(); }
        catch { Application.Current.Shutdown(2); }
    }

    private void Write()
    {
        if (_ready && !_closed)
        {
            _canvasBounds = BoundsOf(_canvas);
            foreach (var option in _options) option.Bounds = BoundsOf(option.Control);
        }
        if (_canvasBounds is null || _options.Any(o => o.Bounds is null)) return;
        var expected = _options[_expectedIndex];
        var report = new
        {
            schemaVersion = 1, mode = "INDEPENDENT_ASSISTED_LOOP_FIXTURE", processId = Environment.ProcessId,
            hwnd = _hwnd, layout = _layout, nonce = _nonce, atUtc = DateTimeOffset.UtcNow, sequence = ++_sequence,
            closed = _closed, canvasBounds = _canvasBounds,
            expectedTargetIndex = _expectedIndex, expectedTargetName = expected.Name, targetBounds = expected.Bounds,
            targetClicks = expected.Clicks, targetChecked = expected.Checked,
            nonTargetClicks = _options.Where(o => o.Index != _expectedIndex).Sum(o => o.Clicks), totalClicks = _options.Sum(o => o.Clicks),
            targets = _options.Select(o => new { index = o.Index, name = o.Name, bounds = o.Bounds, initialChecked = false, clicks = o.Clicks, @checked = o.Checked }).ToArray(),
            events = _events.ToArray(), totalEvents = _eventSequence,
            inputGeneratedByFixture = false, modelCalls = 0, userSettingsChanged = false,
            eventProvenance = "RECEIVED_WPF_EVENTS_ONLY_DRIVER_MUST_VERIFY_SYSTEM_INPUT"
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        string temporary = _reportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, _reportPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static PixelBounds BoundsOf(FrameworkElement control)
    {
        Point origin = control.PointToScreen(new Point());
        Point end = control.PointToScreen(new Point(control.ActualWidth, control.ActualHeight));
        int left = (int)Math.Floor(origin.X), top = (int)Math.Floor(origin.Y);
        return new(left, top, (int)Math.Ceiling(end.X) - left, (int)Math.Ceiling(end.Y) - top);
    }

    private static string RandomName()
    {
        string[] first = ["月光", "山林", "晨雾", "晚霞", "海风", "松林", "星河", "云影", "清泉", "秋叶", "远山", "晴空"];
        string[] second = ["主题", "预览", "布局", "配色", "面板", "细节", "外观", "界面"];
        return first[RandomNumberGenerator.GetInt32(first.Length)] + second[RandomNumberGenerator.GetInt32(second.Length)];
    }

    private sealed record PixelBounds(int Left, int Top, int Width, int Height);
    private sealed record ClickRecord(long Sequence, int Index, string Name, bool Checked, DateTimeOffset AtUtc);
    private sealed class Option(int index, string name, ButtonBase control, bool target, double x, double y, int layout)
    {
        public int Index { get; } = index;
        public string Name { get; } = name;
        public ButtonBase Control { get; } = control;
        public bool Target { get; } = target;
        public double X { get; } = x;
        public double Y { get; } = y;
        public int Layout { get; } = layout;
        public bool Checked;
        public int Clicks;
        public PixelBounds? Bounds;
    }

    private sealed class LoopCanvas(List<Option> options) : Canvas
    {
        // The selection mark and per-option status are drawn pixels, not accessibility hints.
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            Text(drawing, "外观选项", 25, 23, 27, Brushes.Black);
            Text(drawing, "找到带星号的选项，开启后核对状态。", 26, 64, 17, Brushes.DimGray);
            foreach (var option in options)
            {
                if (option.Target) Text(drawing, "★", option.X - 27, option.Y + 6, 22, Brushes.DarkOrange);
                double x = option.Layout == 0 ? option.X + 26 : option.X + 382;
                double y = option.Layout == 0 ? option.Y + 57 : option.Y + 5;
                Text(drawing, option.Checked ? "已开启" : "未开启", x, y, 18, option.Checked ? Brushes.ForestGreen : Brushes.SlateGray);
            }
        }
        private void Text(DrawingContext drawing, string text, double x, double y, double size, Brush color)
            => drawing.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    }
}
