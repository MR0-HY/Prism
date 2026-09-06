using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Observation;

namespace DesktopAgent.Windows.Input;

internal sealed class Win32InputDevice : IInputDevice
{
    internal static readonly nuint InputMarker = 0x44414754; // Test attribution only, not authorization.
    internal static int NativeInputSize => Marshal.SizeOf<NativeInput>();
    public bool TargetIsCurrent(InputTarget target)
    {
        if (!long.TryParse(target.Foreground.HwndHex.AsSpan().TrimStart('0').TrimStart('x'), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var raw)) return false;
        nint hwnd = (nint)raw;
        return hwnd != 0 && Win32DesktopEnvironment.SessionState() == DesktopSessionState.Available &&
            GetForegroundWindow() == hwnd && IsWindowVisible(hwnd) && !IsIconic(hwnd) &&
            GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == target.Foreground.ProcessId &&
            (target.Foreground.WindowClass is null || ReadWindowClass(hwnd) == target.Foreground.WindowClass) &&
            GetWindowRect(hwnd, out var rectangle) && rectangle.ToPhysical() == target.Foreground.WindowRect &&
            VirtualDesktop() == target.VirtualDesktop;
    }

    private static string ReadWindowClass(nint hwnd)
    {
        var text = new StringBuilder(256);
        return GetClassNameW(hwnd, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    public bool PointIsOnDisplay(PhysicalPoint point) => MonitorFromPoint(new(point.X, point.Y), 0) != 0;
    public bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    public PhysicalPoint CursorPosition() => GetCursorPos(out var point) ? new(point.X, point.Y) : throw new InvalidOperationException("CURSOR_UNAVAILABLE");

    public int Send(ReadOnlySpan<DeviceInput> inputs, PhysicalRect virtualDesktop)
    {
        if (inputs.Length is < 1 or > 16 || NativeInputSize != 40 || IntPtr.Size != 8)
            throw new InvalidOperationException("INVALID_INPUT_BATCH_OR_ABI");
        var native = new NativeInput[inputs.Length];
        for (int i = 0; i < inputs.Length; i++) native[i] = Encode(inputs[i], virtualDesktop);
        return checked((int)SendInput((uint)native.Length, native, NativeInputSize));
    }

    private static NativeInput Encode(DeviceInput input, PhysicalRect virtualDesktop)
    {
        var result = new NativeInput();
        if (input.Kind is DeviceEventKind.KeyDown or DeviceEventKind.KeyUp or DeviceEventKind.UnicodeDown or DeviceEventKind.UnicodeUp)
        {
            bool unicode = input.Kind is DeviceEventKind.UnicodeDown or DeviceEventKind.UnicodeUp;
            bool up = input.Kind is DeviceEventKind.KeyUp or DeviceEventKind.UnicodeUp;
            if (input.Code < 0 || input.Code > (unicode ? 65535 : 255)) throw new ArgumentOutOfRangeException(nameof(input));
            bool extended = !unicode && input.Code is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2E or 0x5B or 0x5C;
            result.Type = 1;
            result.Union.Keyboard = new()
            {
                VirtualKey = unicode ? (ushort)0 : (ushort)input.Code,
                Scan = unicode ? (ushort)input.Code : (ushort)0,
                Flags = (unicode ? 4u : 0) | (up ? 2u : 0) | (extended ? 1u : 0),
                ExtraInfo = InputMarker
            };
        }
        else
        {
            var point = input.Kind == DeviceEventKind.Move ? InputCoordinates.ToAbsolute(new(input.X, input.Y), virtualDesktop) : default;
            result.Union.Mouse = new()
            {
                X = point.X, Y = point.Y, ExtraInfo = InputMarker,
                Flags = input.Kind switch
                {
                    DeviceEventKind.Move => 0x8000 | 0x4000 | 0x0001,
                    DeviceEventKind.LeftDown => 2, DeviceEventKind.LeftUp => 4,
                    DeviceEventKind.RightDown => 8, DeviceEventKind.RightUp => 16,
                    DeviceEventKind.Wheel when input.Code is >= -600 and <= 600 && input.Code != 0 => 0x0800,
                    _ => throw new ArgumentOutOfRangeException(nameof(input))
                },
                Data = input.Kind == DeviceEventKind.Wheel ? unchecked((uint)input.Code) : 0
            };
        }
        return result;
    }

    internal static PhysicalRect VirtualDesktop() => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    internal static InputTarget ReadTarget(nint hwnd, PhysicalRect viewport)
    {
        if (!GetWindowRect(hwnd, out var rect) || GetWindowThreadProcessId(hwnd, out var pid) == 0)
            throw new InvalidOperationException("TARGET_UNAVAILABLE");
        return new(new(hwnd.ToString("X", CultureInfo.InvariantCulture), (int)pid, "", rect.ToPhysical()), viewport, VirtualDesktop());
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HardwareInput { public uint Message; public ushort Low, High; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly PhysicalRect ToPhysical() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
}
