using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Safety;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Configuration;
using DesktopAgent.Windows.Observation;
using DesktopAgent.Windows.Diagnostics;

namespace DesktopAgent.Windows;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly InputSafetyGate _gate = new();
    private EmergencyHotkeyService? _hotkeys;
    private bool _recoveringUiFailure;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Presentation.ThemeManager.Initialize();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ProductLifecycleLog.Default.Startup(e.Args.Length != 0);
        DispatcherUnhandledException += OnDispatcherFailure;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _gate.Trip(InputStopReason.Shutdown);
            if (args.ExceptionObject is Exception exception) ProductLifecycleLog.Default.Fault("UNHANDLED_TERMINATING", exception);
        };
        try
        {
            if (e.Args.Length != 0)
            {
                if (e.Args.Length == 3 && e.Args[0] == "--hybrid-fixture" && int.TryParse(e.Args[2], out int layout) && layout is 0 or 1)
                {
                    var fixture = new HybridFixtureWindow(e.Args[1], layout);
                    MainWindow = fixture;
                    fixture.Show();
                }
                else if (e.Args.Length == 2 && e.Args[0] == "--text-replacement-fixture")
                {
                    var fixture = new TextReplacementFixtureWindow(e.Args[1]);
                    MainWindow = fixture;
                    fixture.Show();
                }
                else if (e.Args.Length == 2 && e.Args[0] == "--check-text-replacement")
                    Shutdown(await TextReplacementDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-hybrid-observation")
                    Shutdown(await HybridControlDiagnostic.RunOfflineAsync(e.Args[1], CancellationToken.None));
                else if (e.Args.Length == 2 && e.Args[0] == "--disable-thinking-and-probe")
                    Shutdown(await SpeedProfileDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 3 && e.Args[0] is "--check-assisted-loop" or "--check-assisted-loop-ready" or "--run-assisted-loop" or "--run-assisted-loop-second-layout")
                    Shutdown(await AssistedLoopDiagnostic.RunAsync(e.Args[1], e.Args[2], offlineOnly: e.Args[0].StartsWith("--check-", StringComparison.Ordinal),
                        preflightHotkeys: e.Args[0] == "--check-assisted-loop-ready", onlyLayout: e.Args[0] == "--run-assisted-loop-second-layout" ? 1 : null));
                else if (e.Args.Length == 2 && e.Args[0] is "--check-notepad-loop" or "--run-notepad-loop")
                    Shutdown(await NotepadLoopDiagnostic.RunAsync(e.Args[1], offlineOnly: e.Args[0] == "--check-notepad-loop"));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-notepad-preparation")
                    Shutdown(await NotepadTestSession.InspectOpenPreparationAsync(e.Args[1]));
                else if (e.Args.Length == 3 && e.Args[0] is "--check-prepared-notepad-loop" or "--run-prepared-notepad-loop")
                    Shutdown(await NotepadLoopDiagnostic.RunAsync(e.Args[2], offlineOnly: e.Args[0] == "--check-prepared-notepad-loop", preparedFrom: e.Args[1]));
                else if (e.Args.Length == 3 && e.Args[0] == "--run-checked-prepared-notepad-loop")
                {
                    int prepared = await NotepadLoopDiagnostic.RunAsync(System.IO.Path.Combine(e.Args[2], "preflight"), true, e.Args[1]);
                    Shutdown(prepared == 0 ? await NotepadLoopDiagnostic.RunAsync(System.IO.Path.Combine(e.Args[2], "real"), false, e.Args[1]) : prepared);
                }
                else if (e.Args.Length == 4 && e.Args[0] == "--check-notepad-native-text")
                    Shutdown(await NotepadNativeTextDiagnostic.RunAsync(e.Args[1], e.Args[2], e.Args[3]));
                else if (e.Args.Length == 4 && e.Args[0] == "--check-notepad-native-mixed")
                    Shutdown(await NotepadNativeTextDiagnostic.RunAsync(e.Args[1], e.Args[2], e.Args[3], mixedOnly: true));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-runtime")
                    Shutdown(await RuntimeDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-shell-surface")
                    Shutdown(await ShellSurfaceDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-desktop-rename")
                    Shutdown(await DesktopRenameReadDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-shell-rename")
                    Shutdown(await ShellRenameDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-wechat-launch")
                    Shutdown(await WeChatLaunchDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-wechat-launch-high-risk")
                    Shutdown(await WeChatLaunchDiagnostic.RunAsync(e.Args[1], true));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-desktop-background")
                    Shutdown(await DesktopBackgroundDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-recovery-provider")
                    Shutdown(await RecoveryProviderDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-intent-provider")
                    Shutdown(await IntentDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 3 && e.Args[0] == "--run-bounded-desktop-task")
                    Shutdown(await BoundedDesktopTaskDiagnostic.RunAsync(e.Args[1], e.Args[2]));
                else if (e.Args.Length == 2 && e.Args[0] == "--prepare-settings-original")
                    Shutdown(await SettingsPreparationDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 3 && e.Args[0] == "--check-desktop-observation")
                    Shutdown(await BoundedObservationDiagnostic.RunAsync(e.Args[1], e.Args[2]));
                else if (e.Args.Length == 4 && e.Args[0] == "--check-desktop-control")
                    Shutdown(await BoundedObservationDiagnostic.RunAsync(e.Args[1], e.Args[2], e.Args[3]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-hotkeys")
                    Shutdown(await HotkeyDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 3 && e.Args[0] == "--check-input")
                    Shutdown(await NativeInputDiagnostic.RunAsync(e.Args[1], e.Args[2]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-configuration")
                    Shutdown(await ConfigurationDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-probe-images")
                    Shutdown(await ProbeImageDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 3 && e.Args[0] == "--check-capture-input")
                    Shutdown(await CaptureDiagnostic.RunAsync(e.Args[1], e.Args[2]));
                else if (e.Args.Length == 3 && e.Args[0] == "--check-uia-input")
                    Shutdown(await CaptureDiagnostic.RunAsync(e.Args[1], e.Args[2], useControls: true));
                else if (e.Args.Length == 2 && e.Args[0] == "--check-interface")
                    Shutdown(await Overlay.InterfaceDiagnostic.RunAsync(e.Args[1]));
                else if (e.Args.Length == 3 && e.Args[0] == "--check-overlay-input")
                    Shutdown(await CaptureDiagnostic.RunAsync(e.Args[1], e.Args[2], useControls: true, useOverlay: true));
                else Shutdown(2);
                return;
            }
            _hotkeys = new EmergencyHotkeyService(_gate);
            var registered = await _hotkeys.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var window = new MainWindow(_gate, registered);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception error)
        {
            ProductLifecycleLog.Default.Fault("STARTUP_FAILED", error);
            _gate.Trip(InputStopReason.Shutdown);
            Shutdown(1);
        }
    }

    private void OnDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _gate.Trip(InputStopReason.InputFault);
        ProductLifecycleLog.Default.Fault("DISPATCHER_UNHANDLED", e.Exception);
        if (_recoveringUiFailure || e.Exception is OutOfMemoryException || MainWindow is not MainWindow window) return;
        e.Handled = true; _recoveringUiFailure = true;
        _ = RecoverUiFailureAsync(window);
    }
    private async Task RecoverUiFailureAsync(MainWindow window)
    {
        try { await window.HandleUnexpectedUiFailureAsync(); }
        catch (Exception error)
        {
            ProductLifecycleLog.Default.Fault("FAILURE_PRESENTATION_FAILED", error);
            _gate.Trip(InputStopReason.Shutdown);
            Shutdown(1); // A broken error surface cannot safely continue normal execution.
        }
        finally { _recoveringUiFailure = false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _gate.Trip(InputStopReason.Shutdown);
        if (_hotkeys is not null)
        {
            try { _hotkeys.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { e.ApplicationExitCode = 1; }
        }
        ProductLifecycleLog.Default.Exit(e.ApplicationExitCode);
        base.OnExit(e);
    }
}

