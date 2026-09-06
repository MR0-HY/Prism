using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Windows.Observation;

/// <summary>Only classic popup menus natively owned by the current Explorer desktop; never searches sibling apps.</summary>
internal static class ShellMenuLocator
{
    internal sealed record Binding(nint Popup, nint Owner, nint MenuOwner, uint ThreadId, int[] RootRuntimeId);
    internal sealed record Root(AutomationElement Element, Binding Binding);

    internal static IReadOnlyList<Root> ReadRoots(HostedWindowIdentity identity, CacheRequest cache)
    {
        if (!IsDesktop(identity)) return [];
        var handles = new List<nint>();
        var timer = Stopwatch.StartNew(); int visited = 0;
        WindowCallback callback = (hwnd, unusedParameter) =>
        {
            if (++visited > 512 || timer.ElapsedMilliseconds > 150 || handles.Count >= 8) return false;
            if (TryNative(hwnd, identity, out _, out _, out _)) handles.Add(hwnd);
            return true;
        };
        _ = EnumWindows(callback, 0);
        var roots = new List<Root>();
        foreach (nint hwnd in handles)
        {
            try
            {
                if (!TryNative(hwnd, identity, out var owner, out var menuOwner, out var thread)) continue;
                var element = AutomationElement.FromHandle(hwnd).GetUpdatedCache(cache);
                var p = element.Cached; int[] runtime = element.GetRuntimeId();
                if (p.ProcessId != identity.HostProcessId || p.NativeWindowHandle != unchecked((int)hwnd) ||
                    p.IsOffscreen || p.IsPassword || p.ControlType != ControlType.Menu || runtime.Length is 0 or > 64) continue;
                var binding = new Binding(hwnd, owner, menuOwner, thread, runtime);
                if (IsCurrent(binding, identity)) roots.Add(new(element, binding));
            }
            catch { /* An unavailable menu cannot authorize controls from a broader scope. */ }
        }
        return roots;
    }

    internal static bool AllowsNode(AutomationElement node, Binding binding, HostedWindowIdentity identity)
    {
        var p = node.Cached;
        nint hwnd = (nint)p.NativeWindowHandle;
        return p.ProcessId == identity.HostProcessId && !p.IsPassword &&
            (hwnd == 0 || hwnd == binding.Popup || GetAncestor(hwnd, 2) == binding.Popup);
    }

    internal static ControlResolution Resolve(Frame frame, ControlCandidate expected, int[] runtimeId, Binding binding,
        HostedWindowIdentity identity, CacheRequest cache, Func<AutomationElement, string, string?, Frame, HostedWindowIdentity, ControlCandidate?> candidate)
    {
        using var dpi = new PhysicalDpiScope();
        var point = new PhysicalPoint(expected.Bounds.Left + (expected.Bounds.Width - 1) / 2, expected.Bounds.Top + (expected.Bounds.Height - 1) / 2);
        if (!IsCurrent(binding, identity) || expected.Role != "MenuItem" || !Contains(binding.Popup, expected.Bounds) ||
            WindowFromPoint(new(point.X, point.Y)) != binding.Popup) return new(null, "SHELL_MENU_OCCLUDED_OR_CHANGED");
        cache.TreeScope = TreeScope.Element | TreeScope.Children;
        var root = AutomationElement.FromHandle(binding.Popup).GetUpdatedCache(cache);
        if (root.Cached.NativeWindowHandle != unchecked((int)binding.Popup) || !AllowsNode(root, binding, identity) ||
            !root.GetRuntimeId().SequenceEqual(binding.RootRuntimeId)) return new(null, "SHELL_MENU_CHANGED");
        var pending = new Queue<(AutomationElement Node, int Depth)>(); pending.Enqueue((root, 0));
        var seen = new HashSet<string>(StringComparer.Ordinal) { string.Join(",", binding.RootRuntimeId) };
        var timer = Stopwatch.StartNew(); int visited = 0, matches = 0;
        AutomationElement? match = null;
        while (pending.TryDequeue(out var branch))
        {
            if (branch.Depth >= 18 || timer.ElapsedMilliseconds >= 400) return new(null, "SHELL_MENU_RECHECK_INCOMPLETE");
            var parent = ReferenceEquals(branch.Node, root) ? root : branch.Node.GetUpdatedCache(cache);
            foreach (AutomationElement child in parent.CachedChildren)
            {
                if (++visited > 384 || timer.ElapsedMilliseconds >= 400) return new(null, "SHELL_MENU_RECHECK_INCOMPLETE");
                if (!AllowsNode(child, binding, identity)) continue;
                int[] id = child.GetRuntimeId();
                if (id.Length is 0 or > 64 || !seen.Add(string.Join(",", id))) return new(null, "SHELL_MENU_RECHECK_INCOMPLETE");
                if (id.SequenceEqual(runtimeId)) { matches++; match = child; }
                pending.Enqueue((child, branch.Depth + 1));
            }
        }
        if (matches != 1 || match is null) return new(null, "SHELL_MENU_ITEM_UNAVAILABLE");
        var fresh = match.GetUpdatedCache(cache);
        var actual = candidate(fresh, expected.Id, expected.ParentId, frame, identity);
        if (actual is null || !actual.Enabled || actual.Name != expected.Name || actual.Role != expected.Role || actual.Bounds != expected.Bounds ||
            actual.ToggleState != expected.ToggleState || actual.Selected != expected.Selected || actual.ExpandCollapseState != expected.ExpandCollapseState ||
            actual.HasSubmenu != expected.HasSubmenu || !fresh.GetRuntimeId().SequenceEqual(runtimeId) ||
            !IsCurrent(binding, identity) || WindowFromPoint(new(point.X, point.Y)) != binding.Popup || !Contains(binding.Popup, expected.Bounds))
            return new(null, "SHELL_MENU_CHANGED");
        return new(point, null);
    }

    internal static bool IsCurrent(Binding binding, HostedWindowIdentity identity) =>
        TryNative(binding.Popup, identity, out var owner, out var menuOwner, out var thread) &&
        owner == binding.Owner && menuOwner == binding.MenuOwner && thread == binding.ThreadId && identity.IsCurrent();

    private static bool TryNative(nint popup, HostedWindowIdentity identity, out nint owner, out nint menuOwner, out uint thread)
    {
        owner = menuOwner = 0; thread = 0;
        if (!IsDesktop(identity) || popup == 0 || ClassName(popup) != "#32768" || !IsWindowVisible(popup) || GetAncestor(popup, 2) != popup ||
            (thread = GetWindowThreadProcessId(popup, out uint pid)) == 0 || pid != identity.HostProcessId) return false;
        owner = GetWindow(popup, 4);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(thread, ref info) && (info.Flags & 0x1C) != 0) menuOwner = info.MenuOwner;
        // Classic submenu HWNDs may have no GW_OWNER; the exact native menu thread identifies their initiating host.
        if (!OwnedByHost(owner, identity) && (menuOwner == 0 || !OwnedByHost(menuOwner, identity))) return false;
        return true;
    }

    private static bool IsDesktop(HostedWindowIdentity identity) =>
        string.Equals(identity.HostProcessName, "explorer", StringComparison.OrdinalIgnoreCase) && identity.ContentHwnd is null &&
        ClassName(identity.HostHwnd) is "Progman" or "WorkerW" && GetForegroundWindow() == identity.HostHwnd;
    private static bool OwnedByHost(nint hwnd, HostedWindowIdentity identity)
    {
        for (int depth = 0; hwnd != 0 && depth < 8; depth++, hwnd = GetWindow(hwnd, 4))
        {
            if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid != identity.HostProcessId) return false;
            if (hwnd == identity.HostHwnd || GetAncestor(hwnd, 2) == identity.HostHwnd) return true;
        }
        return false;
    }
    private static bool Contains(nint hwnd, PhysicalRect bounds) => GetWindowRect(hwnd, out var r) &&
        r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom;
    private static string ClassName(nint hwnd)
    {
        var name = new StringBuilder(256); return GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }
    private delegate bool WindowCallback(nint hwnd, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public int Size; public uint Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public NativeRect CaretRect; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder text, int count);
}
