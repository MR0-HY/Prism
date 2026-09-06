using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopAgent.NativeFixture;

/// <summary>Independent random targets. Records actual WPF events; never injects or raises clicks.</summary>
internal sealed class CaptureFixtureWindow : Window
{
    public CaptureFixtureWindow(string path)
    {
        Title = "Desktop Agent · 12 个随机原生目标 · 55 秒后自动关闭";
        Width = 960; Height = 700; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var canvas = new Canvas { Width = 880, Height = 580, Background = Brushes.White };
        Content = canvas;
        var buttons = new List<Button>();
        int[] counts = new int[12];
        Color[] colors = [Colors.RoyalBlue, Colors.SeaGreen, Colors.DarkViolet, Colors.DarkOrange];
        for (int i = 0; i < 12; i++)
        {
            int index = i;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var button = new Button { Width = 108, Height = 54, FontSize = 20, Foreground = Brushes.White,
                Background = new SolidColorBrush(colors[i % colors.Length]), Content = (i + 1).ToString("00"),
                Template = new ControlTemplate(typeof(Button)) { VisualTree = border } };
            Canvas.SetLeft(button, (i % 4) * 215 + RandomNumberGenerator.GetInt32(12, 72));
            Canvas.SetTop(button, (i / 4) * 185 + RandomNumberGenerator.GetInt32(15, 85));
            button.Click += (_, _) => { counts[index]++; button.Background = Brushes.Crimson; };
            canvas.Children.Add(button); buttons.Add(button);
        }
        var password = new PasswordBox { Width = 120, Height = 25, Password = "invented-only" };
        System.Windows.Automation.AutomationProperties.SetName(password, "PRIVATE_PASSWORD_FIELD");
        Canvas.SetLeft(password, 20); Canvas.SetTop(password, 545); canvas.Children.Add(password);
        var toggle = new CheckBox { Content = "Fixture toggle", IsChecked = true, Width = 140, Height = 25 };
        Canvas.SetLeft(toggle, 200); Canvas.SetTop(toggle, 545); canvas.Children.Add(toggle);
        var disabled = new Button { Content = "Disabled fixture", IsEnabled = false, Width = 145, Height = 25 };
        Canvas.SetLeft(disabled, 380); Canvas.SetTop(disabled, 545); canvas.Children.Add(disabled);
        var hidden = new Button { Content = "OFFSCREEN_FIXTURE", Width = 100, Height = 25 };
        Canvas.SetLeft(hidden, 9000); Canvas.SetTop(hidden, 0); canvas.Children.Add(hidden);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        var expires = DateTimeOffset.UtcNow.AddSeconds(55);
        void Write()
        {
            var canvasOrigin = canvas.PointToScreen(new Point());
            var canvasEnd = canvas.PointToScreen(new Point(canvas.ActualWidth, canvas.ActualHeight));
            var report = new
            {
                processId = Environment.ProcessId, hwnd = new WindowInteropHelper(this).Handle.ToInt64(),
                atUtc = DateTimeOffset.UtcNow, mode = "INDEPENDENT_CAPTURE_INPUT_FIXTURE",
                bounds = new { left = (int)canvasOrigin.X, top = (int)canvasOrigin.Y, width = (int)(canvasEnd.X - canvasOrigin.X), height = (int)(canvasEnd.Y - canvasOrigin.Y) },
                targets = buttons.Select((b, i) =>
                {
                    var start = b.PointToScreen(new Point());
                    var end = b.PointToScreen(new Point(b.ActualWidth, b.ActualHeight));
                    var color = colors[i % colors.Length];
                    return new { index = i, x = (start.X + end.X) / 2, y = (start.Y + end.Y) / 2,
                        left = start.X, top = start.Y, width = end.X - start.X, height = end.Y - start.Y,
                        red = color.R, green = color.G, blue = color.B, clicks = counts[i] };
                }).ToArray()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        timer.Tick += (_, _) => { try { Write(); if (DateTimeOffset.UtcNow >= expires) Close(); } catch { Close(); } };
        ContentRendered += (_, _) => { Write(); timer.Start(); Activate(); };
        Closed += (_, _) => { timer.Stop(); try { Write(); } catch { } };
    }
}
