using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Windows.Observation;

/// <summary>Read-only adapter for Explorer's standard in-place label Edit. The observed Windows provider
/// calls this HWND a Document and offers neither ValuePattern nor TextPattern. No arbitrary Document is adapted.</summary>
internal static class NativeShellRenameEdit
{
    internal const string CandidateId = "shell-rename-edit";
    internal sealed record Binding(nint Host, nint List, nint Edit, int ProcessId, long ProcessStarted, long Style, string Identity);
    internal sealed record Observation(Binding Binding, ControlCandidate Candidate);

    internal static Observation? TryCapture(Frame frame, HostedWindowIdentity identity)
    {
        using var dpi = new PhysicalDpiScope();
        if (!HostCurrent(identity)) return null;
        uint thread = GetWindowThreadProcessId(identity.HostHwnd, out _);
        var gui = new Gui { Size = (uint)Marshal.SizeOf<Gui>() };
        if (!GetGUIThreadInfo(thread, ref gui) || gui.Focus == 0 || gui.MenuOwner != 0 || (gui.Flags & 0x1c) != 0) return null;
        nint edit = gui.Focus, list = GetParent(edit);
        long style = GetWindowLongPtrW(edit, -16).ToInt64();
        var binding = new Binding(identity.HostHwnd, list, edit, identity.HostProcessId, identity.HostStartTimeUtcTicks, style,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"shell-label:{identity.HostProcessId}:{identity.HostStartTimeUtcTicks}:{identity.HostHwnd}:{list}:{edit}:{style}"))));
        var candidate = Read(frame, identity, binding);
        return candidate is null ? null : new(binding, candidate);
    }

    internal static ControlResolution Resolve(Frame frame, ControlCandidate expected, Binding binding, HostedWindowIdentity identity)
    {
        using var dpi = new PhysicalDpiScope();
        var actual = Read(frame, identity, binding);
        if (actual is null || actual != expected) return new(null, "SHELL_RENAME_EDIT_CHANGED");
        var point = new PhysicalPoint(expected.Bounds.Left + (expected.Bounds.Width - 1) / 2, expected.Bounds.Top + (expected.Bounds.Height - 1) / 2);
        if (WindowFromPoint(new(point.X, point.Y)) != binding.Edit || !Current(binding, identity))
            return new(null, "SHELL_RENAME_EDIT_OCCLUDED_OR_CHANGED");
        return new(point, null);
    }

    // Only native ancestors of the confirmed keyboard receiver can have a duplicate structural focus claim removed.
    internal static bool IsStructuralFocusAncestor(Binding binding, nint hwnd, string role) =>
        hwnd != 0 && hwnd != binding.Edit && role is "List" or "Pane" && IsChild(hwnd, binding.Edit);

    private static ControlCandidate? Read(Frame frame, HostedWindowIdentity identity, Binding binding)
    {
        if (!Current(binding, identity) || !GetWindowRect(binding.Edit, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return null;
        var bounds = new PhysicalRect(rect.Left, rect.Top, checked(rect.Right - rect.Left), checked(rect.Bottom - rect.Top));
        if (!frame.PhysicalRegion.Contains(bounds) || frame.OwnWindowRects.Any(r => r.Left < bounds.Right && bounds.Left < r.Right && r.Top < bounds.Bottom && bounds.Top < r.Bottom)) return null;
        var first = ReadValue(binding.Edit);
        if (first is null) return null;
        if (first.Value.Truncated) return Current(binding, identity) ? Candidate(binding, bounds, null, true, null, null) : null;
        if (SendMessageTimeoutW(binding.Edit, 0x00B0, 0, 0, 0x0003, 80, out nuint selection) == 0) return null;
        int start = (int)(selection & 0xffff), end = (int)((selection >> 16) & 0xffff);
        if (start > end || end > first.Value.Text!.Length) return null;
        var second = ReadValue(binding.Edit);
        if (second is null || second.Value.Truncated || second.Value.Text != first.Value.Text || !Current(binding, identity) ||
            !GetWindowRect(binding.Edit, out var after) || after.Left != rect.Left || after.Top != rect.Top || after.Right != rect.Right || after.Bottom != rect.Bottom) return null;
        return Candidate(binding, bounds, first.Value.Text, false, start, end - start);
    }

    private static ControlCandidate Candidate(Binding binding, PhysicalRect bounds, string? text, bool truncated, int? start, int? length) =>
        new(CandidateId, null, "文件或文件夹名称（原生重命名框）", "Edit", bounds, true, true, true, null, null)
        { LocalIdentity = binding.Identity, CurrentValue = text, ValueTruncated = truncated, SelectionStart = start, SelectionLength = length };

    private static (string? Text, bool Truncated)? ReadValue(nint hwnd)
    {
        if (SendMessageTimeoutW(hwnd, 0x000E, 0, 0, 0x0003, 80, out nuint count) == 0) return null;
        if (count > ControlCandidate.MaximumValueLength) return (null, true);
        var text = new StringBuilder(ControlCandidate.MaximumValueLength + 1);
        if (SendMessageTextTimeoutW(hwnd, 0x000D, (nuint)text.Capacity, text, 0x0003, 80, out nuint length) == 0 || length != count) return null;
        string value = text.ToString();
        if (value.Length != (int)count || value.Contains('\0')) return null;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return null; }
            else if (char.IsLowSurrogate(value[i])) return null;
        }
        return (value, false);
    }

    private static bool HostCurrent(HostedWindowIdentity identity) =>
        identity.ContentHwnd is null && string.Equals(identity.HostProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
        Class(identity.HostHwnd) is "Progman" or "WorkerW" or "CabinetWClass" && GetForegroundWindow() == identity.HostHwnd && identity.IsCurrent();

    private static bool Current(Binding binding, HostedWindowIdentity identity)
    {
        if (binding.Host != identity.HostHwnd || binding.ProcessId != identity.HostProcessId || binding.ProcessStarted != identity.HostStartTimeUtcTicks ||
            !HostCurrent(identity) || Class(binding.Edit) != "Edit" || Class(binding.List) != "SysListView32" ||
            !IsWindowVisible(binding.Edit) || !IsWindowEnabled(binding.Edit) || !IsWindowVisible(binding.List) || !IsWindowEnabled(binding.List) ||
            GetParent(binding.Edit) != binding.List || GetAncestor(binding.Edit, 2) != binding.Host || GetAncestor(binding.List, 2) != binding.Host ||
            GetWindowThreadProcessId(binding.Edit, out uint editPid) == 0 || editPid != binding.ProcessId ||
            GetWindowThreadProcessId(binding.List, out uint listPid) == 0 || listPid != binding.ProcessId ||
            GetWindowLongPtrW(binding.Edit, -16).ToInt64() != binding.Style || (binding.Style & (0x20 | 0x800)) != 0) return false;
        uint thread = GetWindowThreadProcessId(binding.Host, out _);
        var gui = new Gui { Size = (uint)Marshal.SizeOf<Gui>() };
        return GetGUIThreadInfo(thread, ref gui) && gui.Focus == binding.Edit && gui.MenuOwner == 0 && (gui.Flags & 0x1c) == 0;
    }
    private static string Class(nint hwnd) { var name = new StringBuilder(128); _ = GetClassNameW(hwnd, name, name.Capacity); return name.ToString(); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Gui { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref Gui info);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern nint SendMessageTextTimeoutW(nint hwnd, uint message, nuint wParam, StringBuilder text, uint flags, uint timeout, out nuint result);
}
