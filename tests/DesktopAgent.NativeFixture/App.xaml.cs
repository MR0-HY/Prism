using System.Configuration;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DesktopAgent.NativeFixture;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        FixtureOptions options;
        try
        {
            options = FixtureOptions.Parse(e.Args);
        }
        catch (ArgumentException error)
        {
            MessageBox.Show(error.Message, "原生测试夹具 · 参数错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (options.AssistedLoopPath is not null)
        {
            try
            {
                var assisted = new AssistedLoopFixtureWindow(options.AssistedLoopPath, options.AssistedLoopLayout);
                MainWindow = assisted;
                assisted.Show();
            }
            catch { Shutdown(2); }
            return;
        }
        if (options.CaptureProbePath is not null)
        {
            var capture = new CaptureFixtureWindow(options.CaptureProbePath);
            MainWindow = capture;
            capture.Show();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        if (options.InputProbePath is not null)
        {
            bool probeStarted = false;
            window.ContentRendered += (_, _) =>
            {
                if (probeStarted) return;
                probeStarted = true;
                window.StartInputProbe(options.InputProbePath);
            };
        }
        if (options.ReportOnExitPath is not null)
        {
            window.Closed += (_, _) =>
            {
                try
                {
                    // This mode never raises synthetic events. Capture only this
                    // fixture's state; event provenance is not inferred as human.
                    var report = new
                    {
                        schemaVersion = 1,
                        evidenceMode = "INTERACTIVE_NATIVE_FIXTURE",
                        atUtc = DateTimeOffset.UtcNow,
                        systemInputInjected = false,
                        networkUsed = false,
                        modelCalls = 0,
                        manualAcceptance = "NOT_ASSERTED",
                        limitations = "Only records events received by this fixture. Human or external-input provenance is not verified; no production input or model acceptance is implied.",
                        snapshot = window.Snapshot()
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(options.ReportOnExitPath)!);
                    File.WriteAllText(options.ReportOnExitPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                }
                catch (Exception)
                {
                    Shutdown(2);
                }
            };
        }
        if (options.SelfCheck)
        {
            bool started = false;
            window.ContentRendered += async (_, _) =>
            {
                if (started) return;
                started = true;
                try
                {
                    bool passed = await window.RunSelfCheckAsync(options.ReportPath!, options.RenderPath);
                    Shutdown(passed ? 0 : 1);
                }
                catch (Exception)
                {
                    // Output-path failures return nonzero; no modal dialog can stall a self-check run.
                    Shutdown(2);
                }
            };
        }
        window.Show();
    }
}

