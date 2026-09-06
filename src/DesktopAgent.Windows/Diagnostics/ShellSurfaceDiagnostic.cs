using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Observation;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Native Shell/UIA metadata only, including bounded desktop item names and raw bounds.
/// No screenshots, windows, hotkeys, model or input.</summary>
internal static class ShellSurfaceDiagnostic
{
    private sealed record NativeWindow(string Hwnd, string Parent, string Root, string ClassName, uint ProcessId, bool Visible, int[] Bounds);
    private sealed record UiaWindow(string Hwnd, string Role, int[] RuntimeId, double[] Bounds, bool Offscreen, bool Focusable, string? Error);
    private sealed record PointHit(int X, int Y, NativeWindow Native, string Role, int UiaHwnd, int ProcessId,
        int[] RuntimeId, double[] Bounds, bool Enabled, bool Offscreen, bool Password, string? Error)
    { public string? ExpectedDesktopItemName { get; init; } public int[]? ExpectedRuntimeId { get; init; } }
    private sealed record ItemBounds(string Name, string Role, int ProcessId, double[] RawBounds, bool Offscreen,
        bool Enabled, bool Focusable, bool FullyInsideNativeShell, bool IntersectsNativeShell, int[] RuntimeId);

    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var windows = new List<NativeWindow>();
        nint shell, foreground;
        using (var dpi = new PhysicalDpiScope())
        {
            shell = GetShellWindow(); foreground = GetForegroundWindow();
            if (shell != 0)
            {
                windows.Add(ReadWindow(shell));
                var watch = Stopwatch.StartNew();
                WindowCallback callback = (hwnd, _) =>
                {
                    if (windows.Count >= 64 || watch.ElapsedMilliseconds > 300) return false;
                    windows.Add(ReadWindow(hwnd)); return true;
                };
                _ = EnumChildWindows(shell, callback, 0);
            }
        }
        using var worker = new BoundedReadWorker("Desktop Agent Shell metadata only");
        var uia = await worker.ReadAsync(() =>
        {
            using var dpi = new PhysicalDpiScope();
            return windows.Where(w => w.ClassName is "Progman" or "SHELLDLL_DefView" or "SysListView32").Select(w =>
            {
                try
                {
                    var element = AutomationElement.FromHandle((nint)long.Parse(w.Hwnd, System.Globalization.NumberStyles.HexNumber));
                    var p = element.Current; var r = p.BoundingRectangle;
                    return new UiaWindow(w.Hwnd, p.ControlType.ProgrammaticName, element.GetRuntimeId(),
                        [r.Left, r.Top, r.Right, r.Bottom], p.IsOffscreen, p.IsKeyboardFocusable, null);
                }
                catch (Exception e) { return new UiaWindow(w.Hwnd, "", [], [], false, false, e.GetType().Name); }
            }).ToArray();
        }, TimeSpan.FromMilliseconds(1500), CancellationToken.None);
        var items = await worker.ReadAsync(() =>
        {
            using var dpi = new PhysicalDpiScope();
            var results = new List<ItemBounds>();
            if (!GetWindowRect(shell, out var shellRect)) return results;
            _ = GetWindowThreadProcessId(shell, out uint shellPid);
            var clock = Stopwatch.StartNew();
            var cache = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, AutomationElementMode = AutomationElementMode.Full };
            foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.ProcessIdProperty,
                AutomationElement.BoundingRectangleProperty, AutomationElement.IsOffscreenProperty, AutomationElement.IsEnabledProperty,
                AutomationElement.IsKeyboardFocusableProperty, AutomationElement.IsPasswordProperty }) cache.Add(property);
            foreach (var list in windows.Where(w => w.ClassName == "SysListView32" && w.ProcessId == shellPid && w.Visible))
            {
                nint hwnd = (nint)long.Parse(list.Hwnd, System.Globalization.NumberStyles.HexNumber);
                var root = AutomationElement.FromHandle(hwnd).GetUpdatedCache(cache);
                foreach (AutomationElement child in root.CachedChildren)
                {
                    if (results.Count >= 256 || clock.ElapsedMilliseconds > 2000) return results;
                    var p = child.Cached;
                    if (p.ProcessId != shellPid || p.ControlType != ControlType.ListItem || p.IsPassword) continue;
                    var b = p.BoundingRectangle;
                    string name = p.Name; if (name.Length > 128) name = name[..128];
                    results.Add(new(name, p.ControlType.ProgrammaticName, p.ProcessId, [b.Left, b.Top, b.Right, b.Bottom], p.IsOffscreen,
                        p.IsEnabled, p.IsKeyboardFocusable, !b.IsEmpty && b.Left >= shellRect.Left && b.Top >= shellRect.Top && b.Right <= shellRect.Right && b.Bottom <= shellRect.Bottom,
                        !b.IsEmpty && b.Left < shellRect.Right && b.Right > shellRect.Left && b.Top < shellRect.Bottom && b.Bottom > shellRect.Top, child.GetRuntimeId()));
                }
            }
            return results;
        }, TimeSpan.FromMilliseconds(2500), CancellationToken.None);
        var hits = await worker.ReadAsync(() =>
        {
            using var dpi = new PhysicalDpiScope();
            var results = new List<PointHit>();
            if (!GetWindowRect(shell, out var r)) return results;
            foreach (int y in new[] { 500, 700, 300, 850, 150 })
            foreach (int x in new[] { 500, 700, 300, 850, 150 })
            {
                int px = r.Left + (r.Right - r.Left) * x / 1000, py = r.Top + (r.Bottom - r.Top) * y / 1000;
                var native = ReadWindow(WindowFromPoint(new(px, py)));
                try
                {
                    var e = AutomationElement.FromPoint(new System.Windows.Point(px, py)); var p = e.Current; var b = p.BoundingRectangle;
                    results.Add(new(px, py, native, p.ControlType.ProgrammaticName, p.NativeWindowHandle, p.ProcessId,
                        e.GetRuntimeId(), [b.Left, b.Top, b.Right, b.Bottom], p.IsEnabled, p.IsOffscreen, p.IsPassword, null));
                }
                catch (Exception e) { results.Add(new(px, py, native, "", 0, 0, [], [], false, false, false, e.GetType().Name)); }
            }
            foreach (var item in (items.Value ?? []).Where(i => i.Name is "新建文件夹" or "新建文件夹 (2)").Take(2))
            {
                int px = (int)((item.RawBounds[0] + item.RawBounds[2]) / 2), py = (int)((item.RawBounds[1] + item.RawBounds[3]) / 2);
                var native = ReadWindow(WindowFromPoint(new(px, py)));
                try
                {
                    var e = AutomationElement.FromPoint(new System.Windows.Point(px, py)); var p = e.Current; var b = p.BoundingRectangle;
                    results.Add(new(px, py, native, p.ControlType.ProgrammaticName, p.NativeWindowHandle, p.ProcessId,
                        e.GetRuntimeId(), [b.Left, b.Top, b.Right, b.Bottom], p.IsEnabled, p.IsOffscreen, p.IsPassword, null)
                        { ExpectedDesktopItemName = item.Name, ExpectedRuntimeId = item.RuntimeId });
                }
                catch (Exception e) { results.Add(new(px, py, native, "", 0, 0, [], [], false, false, false, e.GetType().Name)
                    { ExpectedDesktopItemName = item.Name, ExpectedRuntimeId = item.RuntimeId }); }
            }
            return results;
        }, TimeSpan.FromMilliseconds(3000), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(directory, "surface.json"), JsonSerializer.Serialize(new
        {
            atUtc = DateTimeOffset.UtcNow, evidenceMode = "READ_ONLY_NATIVE_SHELL_METADATA",
            shell = shell.ToString("X"), foreground = foreground.ToString("X"), foregroundIsShell = foreground == shell && shell != 0,
            windows, uiaStatus = uia.Status.ToString(), uia = uia.Value,
            itemStatus = items.Status.ToString(), listItems = items.Value, maximumDesktopItemNames = 256,
            hitStatus = hits.Status.ToString(), hits = hits.Value,
            screenshots = 0, windowsShown = 0, modelCalls = 0, inputEvents = 0, hotkeysRegistered = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return shell == 0 ? 1 : 0;
    }

    private static NativeWindow ReadWindow(nint hwnd)
    {
        var text = new StringBuilder(256); _ = GetClassNameW(hwnd, text, text.Capacity);
        _ = GetWindowThreadProcessId(hwnd, out uint pid); _ = GetWindowRect(hwnd, out var r);
        return new(hwnd.ToString("X"), GetParent(hwnd).ToString("X"), GetAncestor(hwnd, 2).ToString("X"),
            text.ToString(), pid, IsWindowVisible(hwnd), [r.Left, r.Top, r.Right, r.Bottom]);
    }
    private delegate bool WindowCallback(nint hwnd, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint hwnd, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int size);
}
