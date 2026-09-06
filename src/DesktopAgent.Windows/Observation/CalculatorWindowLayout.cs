using System.Runtime.InteropServices;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Input;

namespace DesktopAgent.Windows.Observation;

/// <summary>Centers the current Calculator once per task/window, without activating or resizing it.</summary>
internal sealed class CalculatorWindowLayout(InputSafetyGate gate)
{
    private readonly HashSet<(Guid Task, nint Hwnd, long Started)> _positioned = [];
    private readonly Win32DesktopEnvironment _environment = new();
    internal Task PrepareAsync(InputRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!gate.Status.IsOpen || gate.Status.Lease != run.Lease.Lease) return Task.CompletedTask;
        using var dpi = new PhysicalDpiScope();
        var environment = _environment.Read();
        if (environment.SessionState != DesktopSessionState.Available || environment.Foreground is not { } foreground) return Task.CompletedTask;
        var identity = HostedWindowIdentity.TryCapture(foreground);
        if (identity is null || !string.Equals(identity.ContentProcessName ?? identity.HostProcessName, "CalculatorApp", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        var key = (run.Lease.Lease.TaskId, identity.HostHwnd, identity.HostStartTimeUtcTicks);
        if (_positioned.Contains(key) || IsZoomed(identity.HostHwnd) || IsIconic(identity.HostHwnd)) return Task.CompletedTask;
        var bounds = foreground.WindowRect;
        var display = environment.Displays.OrderByDescending(d => IntersectionArea(d.Bounds, bounds)).First();
        var work = display.WorkArea;
        if (bounds.Width > work.Width || bounds.Height > work.Height) return Task.CompletedTask;
        int x = work.Left + (work.Width - bounds.Width) / 2, y = work.Top + (work.Height - bounds.Height) / 2;
        if (!identity.IsCurrent() || Win32InputDevice.GetForegroundWindow() != identity.HostHwnd) return Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        // Same admission barrier as keyboard/mouse; stop invalidates all subsequent mutations.
        var result = gate.TryExecuteSegment(run, () =>
            Win32InputDevice.GetForegroundWindow() == identity.HostHwnd &&
            SetWindowPos(identity.HostHwnd, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x4000) ? 1 : 0);
        if (result.Admitted && result.AppliedCount == 1)
        {
            _positioned.Add(key);
            return Task.Delay(120, ct); // The next observation is captured after the queued move.
        }
        return Task.CompletedTask;
    }
    private static long IntersectionArea(PhysicalRect a, PhysicalRect b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
