using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopAgent.Core.Domain;
using DesktopAgent.Windows.Input;

namespace DesktopAgent.Windows.Observation;

internal sealed class Win32DesktopEnvironment
{
    private readonly object _sync = new();
    private string? _displaySignature;
    private long _generation;

    public DesktopEnvironment Read()
    {
        lock (_sync)
        {
            using var dpi = new PhysicalDpiScope();
            var displays = ImmutableArray.CreateBuilder<DisplayInfo>();
            bool failed = false;
            MonitorCallback callback = (nint monitor, nint _, ref NativeRect rect, nint data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfoW(monitor, ref info)) { failed = true; return false; }
                // A hidden 1-pixel native window gets the effective DPI for this specific monitor.
                // No show/activate call, and no system scaling changes.
                nint probe = CreateWindowExW(0x08000000, "STATIC", "", 0x80000000,
                    info.Monitor.Left + 1, info.Monitor.Top + 1, 1, 1, 0, 0, 0, 0);
                uint monitorDpi = probe != 0 ? GetDpiForWindow(probe) : 0;
                if (probe != 0) DestroyWindow(probe);
                if (monitorDpi == 0) { failed = true; return false; }
                displays.Add(new(info.Device, info.Monitor.ToPhysical(), info.Work.ToPhysical(), monitorDpi, monitorDpi, (info.Flags & 1) != 0));
                return true;
            };
            if (!EnumDisplayMonitors(0, 0, callback, 0) || failed || displays.Count == 0) throw new InvalidOperationException("DISPLAY_UNAVAILABLE");
            var sorted = displays.OrderBy(d => d.Id, StringComparer.Ordinal).ToImmutableArray();
            string signature = string.Join(";", sorted.Select(d => $"{d.Id}|{d.Bounds}|{d.WorkArea}|{d.DpiX}|{d.DpiY}|{d.IsPrimary}"));
            if (signature != _displaySignature) { _generation = checked(_generation + 1); _displaySignature = signature; }
            var state = SessionState();
            ForegroundIdentity? foreground = null;
            nint hwnd = Win32InputDevice.GetForegroundWindow();
            if (state == DesktopSessionState.Available && hwnd != 0 && IsWindowVisible(hwnd) && !IsIconic(hwnd) &&
                GetWindowRect(hwnd, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                string name = "";
                try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; } catch { }
                var windowClass = new StringBuilder(256);
                _ = GetClassNameW(hwnd, windowClass, windowClass.Capacity);
                if (pid > 0) foreground = new(hwnd.ToString("X"), (int)pid, name, bounds.ToPhysical()) { WindowClass = windowClass.ToString() };
            }
            return new(_generation, sorted, foreground, state);
        }
    }

    public static DesktopSessionState SessionState()
    {
        nint desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS only.
        if (desktop == 0) return DesktopSessionState.Unavailable;
        try
        {
            if (!GetUserObjectInformationW(desktop, 6, out int receivingInput, 4, out _) || receivingInput == 0)
                return DesktopSessionState.Disconnected;
            var name = new StringBuilder(256);
            if (!GetUserObjectName(desktop, 2, name, name.Capacity * 2, out _)) return DesktopSessionState.Unavailable;
            if (!string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase)) return DesktopSessionState.Locked;
            // Our thread must belong to the same active desktop, including sandbox/noninteractive detection.
            nint current = GetThreadDesktop(GetCurrentThreadId());
            return current != 0 && GetUserObjectInformationW(current, 6, out int currentInput, 4, out _) && currentInput != 0
                ? DesktopSessionState.Available : DesktopSessionState.Unavailable;
        }
        finally { CloseDesktop(desktop); }
    }

    internal static PhysicalRect VisibleWindowBounds(ForegroundIdentity foreground)
    {
        using var dpi = new PhysicalDpiScope();
        nint hwnd = (nint)long.Parse(foreground.HwndHex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        // The Shell desktop is not a framed DWM application; it may have no extended frame bounds.
        // Capture still intersects this native rectangle with the selected physical monitor.
        if (foreground.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && foreground.WindowClass is "Progman" or "WorkerW")
            return foreground.WindowRect;
        // GetWindowRect includes invisible resize margins with pixels from neighboring apps.
        if (DwmGetWindowAttribute(hwnd, 9, out NativeRect visible, Marshal.SizeOf<NativeRect>()) != 0 ||
            visible.Right <= visible.Left || visible.Bottom <= visible.Top || !foreground.WindowRect.Contains(visible.ToPhysical()))
            throw new InvalidOperationException("FOREGROUND_CAPTURE_BOUNDS_UNAVAILABLE");
        return visible.ToPhysical();
    }

    public static ImmutableArray<PhysicalRect> OwnWindows()
    {
        using var dpi = new PhysicalDpiScope();
        var rectangles = ImmutableArray.CreateBuilder<PhysicalRect>();
        WindowCallback callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId && (GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x20) == 0 && IsWindowVisible(hwnd) && !IsIconic(hwnd) &&
                GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top) rectangles.Add(r.ToPhysical());
            return true;
        };
        if (!EnumWindows(callback, 0)) throw new InvalidOperationException("OWN_WINDOWS_UNAVAILABLE");
        return rectangles.ToImmutable();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    { public int Left, Top, Right, Bottom; public readonly PhysicalRect ToPhysical() => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorCallback(nint monitor, nint hdc, ref NativeRect rectangle, nint data);
    private delegate bool WindowCallback(nint hwnd, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(nint handle, int index, out int value, int length, out int needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectName(nint handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out NativeRect value, int size);
}

internal sealed class PhysicalDpiScope : IDisposable
{
    private readonly nint _previous;
    public PhysicalDpiScope()
    {
        _previous = SetThreadDpiAwarenessContext(-4); // PerMonitorV2, restored on this same synchronous thread.
        if (_previous == 0) throw new InvalidOperationException("PHYSICAL_DPI_UNAVAILABLE");
    }
    public void Dispose() => SetThreadDpiAwarenessContext(_previous);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
