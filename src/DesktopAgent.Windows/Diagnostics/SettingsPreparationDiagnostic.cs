using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using DesktopAgent.Core.Domain;
using DesktopAgent.Windows.Observation;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit settings-home preparation only; no model requests or keyboard/mouse input.</summary>
internal static class SettingsPreparationDiagnostic
{
    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
        Directory.CreateDirectory(directory);
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(27)); // Reserve cleanup within the 30-second budget.
        var ct = deadline.Token;
        var desktop = new WindowsDesktopObserver(preferForegroundWindow: true);
        using var controls = new WindowsControlObserver(desktop);
        ForegroundIdentity? latest = null, bound = null;
        HostedWindowIdentity? hostedSettings = null;
        string stage = "OPENING_SETTINGS_HOME";
        string? failure = null;
        bool prepared = false, pixelsCleared = false;
        Task Write(string name, object value, CancellationToken token) =>
            HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, name), value, token);
        async Task<DesktopEnvironment> EnvironmentAsync(bool requireBound)
        {
            var environment = await desktop.GetEnvironmentAsync(ct).WaitAsync(ct);
            latest = environment.Foreground;
            if (requireBound && (environment.SessionState != DesktopSessionState.Available || latest is null || bound is null ||
                latest.ProcessId != bound.ProcessId || latest.HwndHex != bound.HwndHex ||
                (hostedSettings is null ? !string.Equals(latest.ProcessName, "SystemSettings", StringComparison.OrdinalIgnoreCase) :
                    !string.Equals(latest.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) || !hostedSettings.IsCurrent())))
                throw new InvalidOperationException("SETTINGS_FOREGROUND_CHANGED");
            return environment;
        }
        try
        {
            await Write("run.json", new { atUtc = DateTimeOffset.UtcNow, maximumMs = 30000,
                modelCalls = 0, inputInjected = false, changesSettings = false, uri = "ms-settings:" }, ct);
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var launched = Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
            }, ct).WaitAsync(ct);
            stage = "WAITING_FOR_SYSTEMSETTINGS_FOREGROUND";
            DesktopEnvironment environment;
            do
            {
                environment = await EnvironmentAsync(requireBound: false);
                if (environment.SessionState == DesktopSessionState.Available && latest is { ProcessId: > 0 })
                {
                    if (string.Equals(latest.ProcessName, "SystemSettings", StringComparison.OrdinalIgnoreCase)) break;
                    if (string.Equals(latest.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                    {
                        var identity = HostedWindowIdentity.TryCapture(latest);
                        if (identity?.ContentHwnd is not null && identity.ContentProcessId is > 0 &&
                            string.Equals(identity.ContentProcessName, "SystemSettings", StringComparison.OrdinalIgnoreCase) && identity.IsCurrent())
                        { hostedSettings = identity; break; }
                    }
                }
                if (clock.ElapsedMilliseconds >= 20000) throw new InvalidOperationException("SYSTEMSETTINGS_NOT_FOREGROUND");
                await Task.Delay(100, ct);
            } while (true);
            bound = latest!;
            await Write("bound-settings-identity.json", new { atUtc = DateTimeOffset.UtcNow, bound,
                hostedSettings = HostedEvidence(hostedSettings), preparationOnly = true, modelCalls = 0, inputInjected = false }, ct);
            var display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, bound.WindowRect)).First();
            int width = Math.Min(960, display.WorkArea.Width - 32), height = Math.Min(760, display.WorkArea.Height - 32);
            if (width < 320 || height < 240) throw new InvalidOperationException("SETTINGS_WORKAREA_TOO_SMALL");
            var requested = new PhysicalRect(display.WorkArea.Left + (display.WorkArea.Width - width) / 2,
                display.WorkArea.Top + (display.WorkArea.Height - height) / 2, width, height);
            stage = "PREPARING_WINDOW_BOUNDS";
            await Write("window-before.json", new { atUtc = DateTimeOffset.UtcNow, foreground = bound, requested,
                operation = "SetWindowPos_NOACTIVATE_NOZORDER_NOOWNERZORDER_ASYNC", preparationOnly = true }, ct);
            _ = await EnvironmentAsync(requireBound: true);
            ct.ThrowIfCancellationRequested();
            using (var dpi = new PhysicalDpiScope())
                if (!SetWindowPos((nint)long.Parse(bound.HwndHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture), 0,
                    requested.Left, requested.Top, width, height, 0x4214)) throw new InvalidOperationException("SETTINGS_RESIZE_FAILED");
            await Task.Delay(250, ct);
            environment = await EnvironmentAsync(requireBound: true);
            await Write("window-after.json", new { atUtc = DateTimeOffset.UtcNow, foreground = latest, requested,
                preparationOnly = true, inputInjected = false, changesSettings = false }, ct);
            display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, latest!.WindowRect)).First();
            stage = "OBSERVING_BOUND_SETTINGS_WINDOW";
            var frame = await desktop.CaptureAsync(new Lease(Guid.NewGuid(), 0), display.Id, null, ct).WaitAsync(ct);
            _ = await EnvironmentAsync(requireBound: true);
            if (frame.Foreground.ProcessId != bound.ProcessId || frame.Foreground.HwndHex != bound.HwndHex ||
                !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion)) throw new InvalidOperationException("SETTINGS_FRAME_SCOPE_MISMATCH");
            var snapshot = await controls.ObserveAsync(frame, ct).WaitAsync(ct);
            _ = await EnvironmentAsync(requireBound: true);
            if (snapshot.Status is not (ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial) || snapshot.Candidates.IsEmpty)
                throw new InvalidOperationException("SETTINGS_CONTROLS_UNAVAILABLE");
            await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(directory, "settings-home.png"), frame.Image.Bytes.ToArray(), ct);
            await Write("observation.json", new { atUtc = DateTimeOffset.UtcNow, frame, snapshot,
                hostedSettings = HostedEvidence(hostedSettings), preparationOnly = true }, ct);
            var target = snapshot.Candidates.FirstOrDefault(c => c.Enabled && c.Focused && c.Role == "Button")
                ?? snapshot.Candidates.FirstOrDefault(c => c.Enabled && c.Focusable && c.Role == "Button");
            if (target is null) throw new InvalidOperationException("SETTINGS_RESOLUTION_TARGET_UNAVAILABLE");
            var resolution = await controls.ResolveAsync(frame, snapshot, target.Id, ct).WaitAsync(ct);
            _ = await EnvironmentAsync(requireBound: true);
            await Write("readonly-resolution.json", new { atUtc = DateTimeOffset.UtcNow, target.Id, target.Name,
                resolution, modelCalls = 0, inputInjected = false, preparationOnly = true }, ct);
            if (resolution.Point is null || resolution.ErrorCode is not null)
                throw new InvalidOperationException("SETTINGS_READONLY_RESOLUTION_FAILED");
            await Write("plan.json", new
            {
                goal = "找到 Windows 的深色/浅色模式设置，先只查看并报告当前“选择模式”的值，留在这个设置页面。",
                expectedProcessName = bound.ProcessName, expectedHwndHex = bound.HwndHex,
                actionLimit = 10, requestLimit = 12, activeMsLimit = 150000
            }, ct);
            ct.ThrowIfCancellationRequested();
            prepared = true;
        }
        catch (Exception error) { failure = error is OperationCanceledException ? "PREPARATION_DEADLINE" : SafeError(error); }
        finally
        {
            var cleanup = desktop.DisposeAsync().AsTask();
            try { await cleanup.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(2000, 29500 - clock.ElapsedMilliseconds)))); pixelsCleared = true; }
            catch { failure ??= "PREPARATION_CLEANUP_INCOMPLETE"; _ = cleanup.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        }
        bool passed = prepared && failure is null && pixelsCleared && clock.ElapsedMilliseconds < 30000;
        await Write("report.json", new { atUtc = DateTimeOffset.UtcNow, passed, stage, failure, bound, foreground = latest,
            hostedSettings = HostedEvidence(hostedSettings),
            elapsedMs = clock.ElapsedMilliseconds, pixelsCleared, modelCalls = 0, inputInjected = false,
            changesSettings = false, settingsAccepted = false, preparationOnly = true }, CancellationToken.None);
        return passed ? 0 : 1;
    }
    private static object? HostedEvidence(HostedWindowIdentity? identity) => identity is null ? null : new
    {
        hostHwndHex = identity.HostHwnd.ToString("X"), identity.HostProcessId, identity.HostProcessName, identity.HostStartTimeUtcTicks,
        contentHwndHex = identity.ContentHwnd is { } content ? content.ToString("X") : null,
        identity.ContentProcessId, identity.ContentProcessName, identity.ContentStartTimeUtcTicks
    };
    private static long Intersection(PhysicalRect a, PhysicalRect b) =>
        Math.Max(0L, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0L, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static string SafeError(Exception error) => error is InvalidOperationException && error.Message.Length is > 0 and <= 100 &&
        error.Message.All(c => char.IsAsciiLetterUpper(c) || c == '_') ? error.Message : "SETTINGS_PREPARATION_FAILED";
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
