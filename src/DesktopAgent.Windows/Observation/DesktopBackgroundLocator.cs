using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Windows.Observation;

/// <summary>Read-only proof of an empty point in Explorer's actual desktop list. Never invokes UIA patterns.</summary>
internal static class DesktopBackgroundLocator
{
    internal const string CandidateId = "desktop-background";
    internal sealed record Binding(nint Host, nint View, nint List, PhysicalPoint Point, PhysicalRect Bounds, int[] RuntimeId);

    internal static Binding? TryCapture(Frame frame, HostedWindowIdentity identity)
    {
        using var dpi = new PhysicalDpiScope();
        if (!TryShell(identity, out nint view, out nint list) || !GetWindowRect(list, out var rect)) return null;
        int left = Math.Max(frame.PhysicalRegion.Left, rect.Left), top = Math.Max(frame.PhysicalRegion.Top, rect.Top);
        int right = (int)Math.Min(frame.PhysicalRegion.Right, rect.Right), bottom = (int)Math.Min(frame.PhysicalRegion.Bottom, rect.Bottom);
        if (right - left < 24 || bottom - top < 24) return null;
        var clock = Stopwatch.StartNew();
        // Bounded native/UIA hit tests, not image-derived coordinates. Avoid usual icon columns first.
        foreach (int y in new[] { 500, 700, 300, 850, 150 })
        foreach (int x in new[] { 500, 700, 300, 850, 150 })
        {
            if (clock.ElapsedMilliseconds > 400) return null;
            var point = new PhysicalPoint(left + (right - left) * x / 1000, top + (bottom - top) * y / 1000);
            var bounds = new PhysicalRect(point.X - 4, point.Y - 4, 9, 9);
            if (!frame.PhysicalRegion.Contains(bounds) || frame.OwnWindowRects.Any(r => Overlaps(r, bounds))) continue;
            int[]? runtime = EmptyPoint(identity, view, list, point);
            if (runtime is not null) return new(identity.HostHwnd, view, list, point, bounds, runtime);
        }
        return null;
    }

    internal static ControlCandidate Candidate(Binding binding) => new(CandidateId, null, "桌面空白处（已核对无图标，可右键）",
        "DesktopBackground", binding.Bounds, true, false, false, null, null);

    internal static ControlResolution Resolve(Frame frame, ControlCandidate candidate, Binding binding, HostedWindowIdentity identity)
    {
        using var dpi = new PhysicalDpiScope();
        if (candidate != Candidate(binding) || binding.Host != identity.HostHwnd || !frame.PhysicalRegion.Contains(binding.Bounds) ||
            frame.OwnWindowRects.Any(r => Overlaps(r, binding.Bounds)) ||
            !TryShell(identity, out var view, out var list) || view != binding.View || list != binding.List)
            return new(null, "DESKTOP_BACKGROUND_CHANGED");
        int[]? runtime = EmptyPoint(identity, view, list, binding.Point);
        return runtime is not null && runtime.SequenceEqual(binding.RuntimeId)
            ? new(binding.Point, null) : new(null, "DESKTOP_BACKGROUND_OCCLUDED_OR_CHANGED");
    }

    private static int[]? EmptyPoint(HostedWindowIdentity identity, nint view, nint list, PhysicalPoint point)
    {
        try
        {
            if (!ValidChain(identity, view, list) || WindowFromPoint(new(point.X, point.Y)) != list) return null;
            // A transparent third-party overlay can own UIA's global FromPoint while native mouse hit testing
            // still targets Explorer. Never trust that foreign UIA element: prove emptiness inside the real list.
            var cache = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, AutomationElementMode = AutomationElementMode.Full };
            foreach (var property in new[] { AutomationElement.ProcessIdProperty, AutomationElement.NativeWindowHandleProperty,
                AutomationElement.ControlTypeProperty, AutomationElement.IsOffscreenProperty, AutomationElement.IsPasswordProperty,
                AutomationElement.IsEnabledProperty, AutomationElement.BoundingRectangleProperty }) cache.Add(property);
            var nativeList = AutomationElement.FromHandle(list).GetUpdatedCache(cache);
            var p = nativeList.Cached;
            var location = new Point(point.X, point.Y);
            if (p.ProcessId != identity.HostProcessId || p.NativeWindowHandle != unchecked((int)list) ||
                p.IsOffscreen || p.IsPassword || !p.IsEnabled || p.ControlType != ControlType.List || !p.BoundingRectangle.Contains(location)) return null;
            int[] runtime = nativeList.GetRuntimeId();
            if (runtime.Length is 0 or > 64) return null;
            var watch = Stopwatch.StartNew();
            int visited = 0;
            var pending = new Queue<(AutomationElement Element, int Depth)>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { string.Join(",", runtime) };
            pending.Enqueue((nativeList, 0));
            while (pending.TryDequeue(out var branch))
            {
                if (branch.Depth > 8 || watch.ElapsedMilliseconds > 250) return null;
                var parent = ReferenceEquals(branch.Element, nativeList) ? nativeList : branch.Element.GetUpdatedCache(cache);
                foreach (AutomationElement child in parent.CachedChildren)
                {
                    if (++visited > 384 || watch.ElapsedMilliseconds > 250) return null;
                    var childProperties = child.Cached;
                    int[] childId = child.GetRuntimeId();
                    if (childId.Length is 0 or > 64 || !seen.Add(string.Join(",", childId)) || childProperties.ProcessId != identity.HostProcessId) return null;
                    if (childProperties.IsOffscreen) continue;
                    var bounds = childProperties.BoundingRectangle;
                    // Reject icons and every other visible child covering the point, including unknown controls.
                    if (childProperties.IsPassword || bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
                        !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) || bounds.Contains(location)) return null;
                    pending.Enqueue((child, branch.Depth + 1));
                }
            }
            if (!runtime.SequenceEqual(AutomationElement.FromHandle(list).GetRuntimeId())) return null;
            if (!ValidChain(identity, view, list) || WindowFromPoint(new(point.X, point.Y)) != list || !identity.IsCurrent()) return null;
            return runtime;
        }
        catch { return null; } // Unavailable UIA, foreign overlays and desktop icons never become a blank-surface proof.
    }

    private static bool TryShell(HostedWindowIdentity identity, out nint view, out nint list)
    {
        view = list = 0;
        if (!string.Equals(identity.HostProcessName, "explorer", StringComparison.OrdinalIgnoreCase) || identity.ContentHwnd is not null ||
            ClassName(identity.HostHwnd) is not ("Progman" or "WorkerW") || GetForegroundWindow() != identity.HostHwnd || !identity.IsCurrent()) return false;
        nint shell = GetShellWindow();
        if (shell == 0 || ClassName(shell) != "Progman" || !SameProcess(shell, identity)) return false;
        view = FindWindowExW(identity.HostHwnd, 0, "SHELLDLL_DefView", null);
        if (view == 0) return false;
        list = FindWindowExW(view, 0, "SysListView32", null);
        return ValidChain(identity, view, list);
    }

    private static bool ValidChain(HostedWindowIdentity identity, nint view, nint list) =>
        GetForegroundWindow() == identity.HostHwnd && ClassName(identity.HostHwnd) is "Progman" or "WorkerW" &&
        IsWindowVisible(identity.HostHwnd) && ClassName(view) == "SHELLDLL_DefView" && ClassName(list) == "SysListView32" &&
        IsWindowVisible(view) && IsWindowVisible(list) && GetParent(view) == identity.HostHwnd && GetParent(list) == view &&
        GetAncestor(view, 2) == identity.HostHwnd && GetAncestor(list, 2) == identity.HostHwnd &&
        SameProcess(view, identity) && SameProcess(list, identity);

    private static bool SameProcess(nint hwnd, HostedWindowIdentity identity) =>
        GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == identity.HostProcessId;
    private static string ClassName(nint hwnd)
    {
        var text = new StringBuilder(256);
        return hwnd != 0 && GetClassNameW(hwnd, text, text.Capacity) > 0 ? text.ToString() : "";
    }
    private static bool Overlaps(PhysicalRect a, PhysicalRect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string className, string? title);
}
