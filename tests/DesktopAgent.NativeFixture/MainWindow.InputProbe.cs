using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopAgent.NativeFixture;

public partial class MainWindow
{
    /// <summary>Independent test oracle only. Never consumed by the production observer/model.</summary>
    internal void StartInputProbe(string reportPath)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        var expires = DateTimeOffset.UtcNow.AddSeconds(60);
        void Write()
        {
            var controls = new FrameworkElement[] { TargetButton, PlainTextBox, DragTarget, DropZone };
            var report = new
            {
                evidenceMode = "INDEPENDENT_NATIVE_INPUT_TARGET", atUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId, hwnd = new WindowInteropHelper(this).Handle.ToInt64(),
                coordinateSpace = "SCREEN_PHYSICAL_PIXELS_FROM_POINT_TO_SCREEN",
                targets = controls.Select(c =>
                {
                    var center = c.PointToScreen(new Point(c.ActualWidth / 2, c.ActualHeight / 2));
                    return new { name = c.Name, x = center.X, y = center.Y };
                }).ToArray(),
                snapshot = Snapshot()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            string temporary = reportPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            File.Move(temporary, reportPath, overwrite: true);
        }
        timer.Tick += (_, _) =>
        {
            try { Write(); if (DateTimeOffset.UtcNow >= expires) Close(); }
            catch { timer.Stop(); Close(); }
        };
        Closed += (_, _) => { timer.Stop(); try { Write(); } catch { /* Caller detects missing/stale report. */ } };
        Write();
        timer.Start();
        Activate();
    }
}
