using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace DesktopAgent.Windows.Presentation;

/// <summary>Native notification-area entry. It never registers or injects input.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int Callback = 0x8000 + 71;
    private readonly Window _window;
    private readonly Action _show, _pause, _stop, _exit;
    private HwndSource? _source;
    private Data _data;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool _disposed;
    internal bool Available { get; private set; }
    internal TrayIcon(Window window, Action show, Action pause, Action stop, Action exit)
    {
        _window = window; _show = show; _pause = pause; _stop = stop; _exit = exit;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd); _source.AddHook(Hook);
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "DesktopAgent.exe");
        nint[] small = new nint[1];
        ExtractIconEx(path, 0, null, small, 1);
        _data = new() { Size = (uint)Marshal.SizeOf<Data>(), Window = hwnd, Id = 1, Flags = 7, CallbackMessage = Callback,
            Icon = small[0] != 0 ? small[0] : LoadIcon(0, 32512), Tip = "Prism · 棱镜 — 双击打开，右键更多操作", Info = "", InfoTitle = "" };
        Available = Shell_NotifyIcon(0, ref _data);
    }
    private nint Hook(nint hwnd, int message, nint w, nint l, ref bool handled)
    {
        if (_disposed) return 0;
        if ((uint)message == _taskbarCreated) Available = Shell_NotifyIcon(0, ref _data);
        if (message != Callback) return 0;
        int mouse = (int)l & 0xffff;
        if (mouse is 0x203 or 0x400 or 0x401) _show();
        if (mouse is 0x205 or 0x7B)
        {
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            foreach (var item in new (string Name, Action Action)[] { ("打开 Prism", _show), ("暂停当前任务", _pause), ("停止当前任务", _stop), ("退出 Prism", _exit) })
            { var entry = new MenuItem { Header = item.Name }; entry.Click += (_, _) => item.Action(); menu.Items.Add(entry); }
            SetForegroundWindow(hwnd); menu.IsOpen = true;
        }
        handled = true; return 0;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        Shell_NotifyIcon(2, ref _data); Available = false;
        _source?.RemoveHook(Hook); _source = null;
        if (_data.Icon != 0 && _data.Icon != LoadIcon(0, 32512)) DestroyIcon(_data.Icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Data
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint message, ref Data data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[] small, uint count);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
