using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DesktopAgent.Core.Domain;

namespace DesktopAgent.Windows.Observation;

/// <summary>A read-only native host/content binding; never expands to other windows of either process.</summary>
internal sealed record HostedWindowIdentity
{
    private sealed record WindowBinding(nint Hwnd, int ProcessId, string ProcessName, long StartTimeUtcTicks, string ClassName);
    private readonly WindowBinding _host;
    private readonly WindowBinding? _content;
    private HostedWindowIdentity(WindowBinding host, WindowBinding? content) => (_host, _content) = (host, content);
    internal nint HostHwnd => _host.Hwnd;
    internal int HostProcessId => _host.ProcessId;
    internal string HostProcessName => _host.ProcessName;
    internal long HostStartTimeUtcTicks => _host.StartTimeUtcTicks;
    internal nint? ContentHwnd => _content?.Hwnd;
    internal int? ContentProcessId => _content?.ProcessId;
    internal string? ContentProcessName => _content?.ProcessName;
    internal long? ContentStartTimeUtcTicks => _content?.StartTimeUtcTicks;
    internal bool AllowsProcess(int processId) => processId == HostProcessId || processId == ContentProcessId;

    // No foreground requirement: callers may verify the exact planned window before activating it.
    internal static HostedWindowIdentity? TryCapture(ForegroundIdentity foreground)
    {
        string text = foreground.HwndHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? foreground.HwndHex[2..] : foreground.HwndHex;
        return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hwnd)
            ? Capture((nint)hwnd, foreground.ProcessId, foreground.ProcessName) : null;
    }
    internal bool IsCurrent()
    {
        var current = Capture(HostHwnd, HostProcessId, HostProcessName);
        return current is not null && current._host == _host && current._content == _content;
    }
    private static HostedWindowIdentity? Capture(nint hwnd, int processId, string processName)
    {
        try
        {
            var host = ReadWindow(hwnd);
            if (host is null || host.ProcessId != processId || !Same(host.ProcessName, processName) || GetAncestor(hwnd, 2) != hwnd) return null;
            bool hostProcess = Same(host.ProcessName, "ApplicationFrameHost"), hostClass = host.ClassName == "ApplicationFrameWindow";
            if (!hostProcess && !hostClass) return new(host, null);
            if (!hostProcess || !hostClass) return null;
            var clock = Stopwatch.StartNew();
            WindowBinding? content = null;
            int visited = 0, matches = 0;
            bool incomplete = false;
            WindowCallback callback = (child, _) =>
            {
                try
                {
                    if (++visited > 128 || clock.ElapsedMilliseconds > 200) { incomplete = true; return false; }
                    if (!IsWindowVisible(child) || ClassName(child) != "Windows.UI.Core.CoreWindow") return true;
                    if (++matches != 1) return false;
                    content = ReadWindow(child);
                    if (content is null || content.ProcessId == host.ProcessId || GetAncestor(child, 2) != hwnd || !IsChild(hwnd, child))
                    { incomplete = true; return false; }
                    return true;
                }
                catch { incomplete = true; return false; }
            };
            _ = EnumChildWindows(hwnd, callback, 0);
            if (incomplete || matches != 1 || content is null || clock.ElapsedMilliseconds > 200 ||
                !IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetAncestor(hwnd, 2) != hwnd || ClassName(hwnd) != host.ClassName ||
                !IsWindow(content.Hwnd) || !IsWindowVisible(content.Hwnd) || ClassName(content.Hwnd) != content.ClassName ||
                GetAncestor(content.Hwnd, 2) != hwnd || !IsChild(hwnd, content.Hwnd) ||
                GetWindowThreadProcessId(hwnd, out uint hostPid) == 0 || hostPid != host.ProcessId ||
                GetWindowThreadProcessId(content.Hwnd, out uint contentPid) == 0 || contentPid != content.ProcessId) return null;
            return new(host, content);
        }
        catch { return null; } // Unavailable metadata never authorizes a broader UIA scope.
    }
    private static WindowBinding? ReadWindow(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid is 0 or > int.MaxValue) return null;
        using var process = Process.GetProcessById((int)pid);
        string name = process.ProcessName, className = ClassName(hwnd);
        long started = process.StartTime.ToUniversalTime().Ticks;
        return !process.HasExited && name.Length > 0 && className.Length > 0 && IsWindow(hwnd) &&
            GetWindowThreadProcessId(hwnd, out uint currentPid) != 0 && currentPid == pid
            ? new(hwnd, (int)pid, name, started, className) : null;
    }
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string ClassName(nint hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }
    private delegate bool WindowCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint hwnd, WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int maximum);
}
