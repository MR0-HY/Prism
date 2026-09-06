using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Observation;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Reads only visible native desktop rename Edit children, not desktop filenames or unrelated apps.
/// No windows shown, screenshot, focus change, hotkeys, model or input executor.</summary>
internal static class DesktopRenameReadDiagnostic
{
    private sealed record NativeEdit(long Hwnd, long Parent, string ClassName, uint ProcessId, long Style, int[] Bounds,
        bool Visible, bool Enabled, bool Password, string? Value, bool? Truncated, int? SelectionStart, int? SelectionLength);
    private sealed record UiaNode(string Role, int Hwnd, int ProcessId, bool Focused, bool Focusable, bool Enabled, bool Offscreen, bool Password,
        double[] Bounds, int[] RuntimeId);
    private sealed record EditComparison(NativeEdit Native, UiaNode[] UiaChain, string? UiaValue, string? TextPatternValue, string? Error);

    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
        TextReplacementDiagnostic.CheckDirectory(directory); Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var worker = new BoundedReadWorker("Desktop Agent desktop rename metadata only");
        nint shell = GetShellWindow(), foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(shell, out uint shellPid);
        if (shell == 0 || Class(shell) != "Progman" || Process.GetProcessById((int)shellPid).ProcessName != "explorer") return 1;
        uint thread = GetWindowThreadProcessId(shell, out _);
        var gui = new Gui { Size = (uint)Marshal.SizeOf<Gui>() };
        bool guiRead = GetGUIThreadInfo(thread, ref gui);
        var edits = new List<nint>(); int visited = 0;
        _ = EnumChildWindows(shell, (hwnd, parameter) =>
        {
            if (++visited > 128) return false;
            if (Class(hwnd) == "Edit" && IsWindowVisible(hwnd) && Class(GetParent(hwnd)) == "SysListView32" &&
                GetAncestor(hwnd, 2) == shell && SameProcess(hwnd, shellPid)) edits.Add(hwnd);
            return true;
        }, 0);
        var result = await worker.ReadAsync(() =>
        {
            using var dpi = new PhysicalDpiScope();
            var comparisons = new List<EditComparison>();
            foreach (nint hwnd in edits)
            {
                if (!IsWindowVisible(hwnd) || Class(hwnd) != "Edit" || GetAncestor(hwnd, 2) != shell || !SameProcess(hwnd, shellPid)) continue;
                _ = GetWindowRect(hwnd, out var rect);
                long style = GetWindowLongPtrW(hwnd, -16).ToInt64(); bool password = (style & 0x20) != 0;
                string? nativeValue = null; bool? truncated = null; int? start = null, length = null;
                if (!password && SendMessageTimeoutW(hwnd, 0x000E, 0, 0, 0x0003, 80, out nuint count) != 0)
                {
                    if (count > 512) truncated = true;
                    else
                    {
                        var buffer = new StringBuilder(513);
                        if (SendMessageTextTimeoutW(hwnd, 0x000D, 513, buffer, 0x0003, 80, out nuint read) != 0 && read == count)
                        {
                            nativeValue = buffer.ToString(); truncated = false;
                            if (SendMessageTimeoutW(hwnd, 0x00B0, 0, 0, 0x0003, 80, out nuint selection) != 0)
                            {
                                int begin = (int)(selection & 0xFFFF), end = (int)((selection >> 16) & 0xFFFF);
                                if (begin <= end && end <= nativeValue.Length) { start = begin; length = end - begin; }
                            }
                        }
                    }
                }
                var native = new NativeEdit(hwnd.ToInt64(), GetParent(hwnd).ToInt64(), "Edit", shellPid, style,
                    [rect.Left, rect.Top, rect.Right, rect.Bottom], IsWindowVisible(hwnd), IsWindowEnabled(hwnd), password, nativeValue, truncated, start, length);
                var chain = new List<UiaNode>(); string? value = null, text = null, error = null;
                try
                {
                    var element = AutomationElement.FromHandle(hwnd); var initial = element.Current;
                    if (!password && !initial.IsPassword && initial.ProcessId == shellPid && initial.ControlType == ControlType.Edit)
                    {
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern valuePattern)
                        { string readValue = valuePattern.Current.Value; if (readValue.Length <= 512) value = readValue; }
                        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern textPattern)
                        { string readText = textPattern.DocumentRange.GetText(513); if (readText.Length <= 512) text = readText; }
                    }
                    for (var node = element; node is not null && chain.Count < 8; node = TreeWalker.ControlViewWalker.GetParent(node))
                    {
                        var p = node.Current; var b = p.BoundingRectangle;
                        chain.Add(new(p.ControlType.ProgrammaticName, p.NativeWindowHandle, p.ProcessId, p.HasKeyboardFocus, p.IsKeyboardFocusable,
                            p.IsEnabled, p.IsOffscreen, p.IsPassword, [b.Left, b.Top, b.Right, b.Bottom], node.GetRuntimeId()));
                        if (p.NativeWindowHandle == unchecked((int)shell)) break;
                    }
                }
                catch (Exception ex) { error = ex.GetType().Name; }
                comparisons.Add(new(native, chain.ToArray(), value, text, error));
            }
            return comparisons.ToArray();
        }, TimeSpan.FromSeconds(3), deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "rename.json"), JsonSerializer.Serialize(new
        {
            atUtc = DateTimeOffset.UtcNow, runtime = ".NET_DESKTOP_AGENT_PRODUCTION_UIA_RUNTIME", foreground = foreground.ToString("X"),
            foregroundClass = Class(foreground), shell = shell.ToString("X"), shellPid, foregroundIsShell = foreground == shell,
            guiRead, shellThreadFocus = gui.Focus.ToString("X"), shellThreadFocusClass = Class(gui.Focus), gui.Flags,
            visitedNativeChildren = visited, matchingNativeEdits = edits.Count, workerStatus = result.Status.ToString(), edits = result.Value,
            screenshots = 0, inputEvents = 0, modelCalls = 0, hotkeysRegistered = false, windowsShown = 0,
            source = "READ_ONLY_DESKTOP_RENAME_EDIT_NOT_EXECUTION_AUTHORITY"
        }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        return result.Status == ReadWorkerStatus.Completed ? 0 : 1;
    }
    private static string Class(nint hwnd) { var text = new StringBuilder(256); _ = GetClassNameW(hwnd, text, text.Capacity); return text.ToString(); }
    private static bool SameProcess(nint hwnd, uint pid) => GetWindowThreadProcessId(hwnd, out uint current) != 0 && current == pid;
    private delegate bool WindowCallback(nint hwnd, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Gui { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref Gui info);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint hwnd, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern nint SendMessageTextTimeoutW(nint hwnd, uint message, nuint wParam, StringBuilder lParam, uint flags, uint timeout, out nuint result);
}
