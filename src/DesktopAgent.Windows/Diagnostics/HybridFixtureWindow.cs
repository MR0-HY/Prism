using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopAgent.Core.Domain;

namespace DesktopAgent.Windows.Diagnostics;

internal sealed record HybridFixtureTruth(int ProcessId, long Hwnd, int Layout, PhysicalRect CanvasBounds,
    string ExpectedCode, string ExpectedTargetName, PhysicalRect TargetBounds, string Nonce);

/// <summary>Explicit child-process test entry only. No model calls, synthetic input, or user settings.</summary>
internal sealed class HybridFixtureWindow : Window
{
    private readonly string _truthPath;
    private readonly int _layout;
    private readonly FixtureCanvas _canvas;
    private readonly FrameworkElement _target;
    private readonly string _targetName;
    private readonly string _code;
    private readonly System.Threading.Timer _hardDeadline;

    public HybridFixtureWindow(string truthPath, int layout = 0)
    {
        if (layout is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(layout));
        _truthPath = Path.GetFullPath(truthPath);
        if (File.Exists(_truthPath)) throw new InvalidOperationException("FIXTURE_TRUTH_ALREADY_EXISTS");
        _layout = layout;
        Title = "Desktop Agent · 只读定位测试";
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = true;
        Topmost = true;
        Background = Brushes.White;
        _canvas = new FixtureCanvas { Width = 740, Height = 510 };
        Content = _canvas;
        int targetIndex = RandomNumberGenerator.GetInt32(8);
        var names = new HashSet<string>(StringComparer.Ordinal);
        FrameworkElement? target = null;
        string targetName = "", targetCode = "";
        for (int i = 0; i < 8; i++)
        {
            string name;
            do { name = RandomName(); } while (!names.Add(name));
            string code = RandomCode();
            double x = layout == 0 ? 70 + RandomNumberGenerator.GetInt32(20) : 32 + i % 2 * 362;
            double y = layout == 0 ? 66 + i * 51 : 78 + i / 2 * 102;
            FrameworkElement control = layout == 0
                ? new Button { Content = name, Width = 280, Height = 35, FontSize = 17 }
                : new CheckBox { Content = name, Width = 190, Height = 36, FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(control, name);
            AutomationProperties.SetAutomationId(control, "option-" + Guid.NewGuid().ToString("N"));
            Canvas.SetLeft(control, x); Canvas.SetTop(control, y);
            _canvas.Children.Add(control);
            _canvas.Marks.Add(new(layout == 0 ? x + 314 : x + 202, y + 3, code, i == targetIndex));
            if (i == targetIndex) { target = control; targetName = name; targetCode = code; }
        }
        _target = target!; _targetName = targetName; _code = targetCode;
        // Independent process deadline also works if the WPF dispatcher gets stuck.
        _hardDeadline = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(70), Timeout.InfiniteTimeSpan);
        SourceInitialized += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            // Keep the whole physical canvas 740 × 510, below the model's documented image budget.
            _canvas.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
        };
        ContentRendered += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var truth = new HybridFixtureTruth(Environment.ProcessId, new WindowInteropHelper(this).Handle.ToInt64(),
                _layout, BoundsOf(_canvas), _code, _targetName, BoundsOf(_target), Guid.NewGuid().ToString("N"));
            try { await HybridDiagnosticEvidence.WriteAsync(_truthPath, truth, CancellationToken.None); }
            catch { Close(); }
        };
        Closed += (_, _) => { _hardDeadline.Dispose(); Application.Current.Shutdown(); };
    }

    private static PhysicalRect BoundsOf(FrameworkElement element)
    {
        var first = element.PointToScreen(new Point(0, 0));
        var last = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        int left = (int)Math.Floor(first.X), top = (int)Math.Floor(first.Y);
        return new(left, top, (int)Math.Ceiling(last.X) - left, (int)Math.Ceiling(last.Y) - top);
    }

    private static string RandomName()
    {
        string[] first = ["Amber", "Maple", "Cedar", "Silver", "Willow", "Ocean", "Coral", "Meadow", "Birch", "Cloud", "Moss", "River"];
        string[] second = ["panel", "theme", "preview", "accent", "layout", "display", "mode", "detail", "surface", "style"];
        return first[RandomNumberGenerator.GetInt32(first.Length)] + " " + second[RandomNumberGenerator.GetInt32(second.Length)];
    }

    private static string RandomCode()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        return new string(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    private sealed record VisualMark(double X, double Y, string Code, bool Target);
    private sealed class FixtureCanvas : Canvas
    {
        public List<VisualMark> Marks { get; } = [];
        // Codes and star exist only as drawn pixels, never as accessibility names or children.
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
            Draw(dc, "Display preferences", 27, 20, 22, Brushes.Black);
            foreach (var mark in Marks)
                Draw(dc, (mark.Target ? "★ " : "   ") + mark.Code, mark.X, mark.Y, 18, Brushes.DarkSlateGray);
        }
        private void Draw(DrawingContext dc, string text, double x, double y, double size, Brush brush)
            => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    }
}

internal static class HybridDiagnosticEvidence
{
    internal static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
        => await WriteBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }), ct);

    internal static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, ct);
                ct.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
