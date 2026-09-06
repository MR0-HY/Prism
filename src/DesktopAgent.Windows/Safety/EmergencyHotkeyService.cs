using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Windows.Safety;

public sealed record HotkeyRegistrationResult(bool Registered, int ErrorCode, string? FailedCombination);

/// <summary>Dedicated thread queue, independent of WPF Dispatcher and model work.</summary>
public sealed class EmergencyHotkeyService : IAsyncDisposable
{
    internal const int PauseId = 0x4D10;
    internal const int StopId = 0x4D11;
    private const uint HotkeyMessage = 0x0312;
    private const uint QuitMessage = 0x0012;
    private const uint Modifiers = 0x0001 | 0x0002 | 0x4000; // ALT | CTRL | NOREPEAT
    private readonly InputSafetyGate _gate;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<HotkeyRegistrationResult> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private int _stopRequested;
    private uint _threadId;
    private int _queueReady;
    private long _lastCloseTicks;
    private int _messageCount;

    public long LastCloseTicks => Interlocked.Read(ref _lastCloseTicks);
    public int MessageCount => Volatile.Read(ref _messageCount);
    public bool ThreadExited => _finished.Task.IsCompleted;

    public EmergencyHotkeyService(InputSafetyGate gate)
    {
        _gate = gate;
        _thread = new Thread(Run) { Name = "DesktopAgent.EmergencyHotkeys", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public Task<HotkeyRegistrationResult> StartAsync()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            try { _thread.Start(); }
            catch
            {
                _gate.SetHotkeysReady(false);
                _ready.TrySetResult(new(false, -1, "THREAD_START"));
                _finished.TrySetResult();
            }
        }
        return _ready.Task;
    }

    private void Run()
    {
        bool pauseRegistered = false, stopRegistered = false;
        try
        {
            _threadId = GetCurrentThreadId();
            _ = PeekMessageW(out _, 0, 0, 0, 0); // Create queue before publishing ready.
            Volatile.Write(ref _queueReady, 1);
            if (Volatile.Read(ref _stopRequested) != 0) return;
            pauseRegistered = RegisterHotKey(0, PauseId, Modifiers, 0x77); // F8
            if (!pauseRegistered)
            {
                _ready.TrySetResult(new(false, Marshal.GetLastWin32Error(), "Ctrl+Alt+F8"));
                return;
            }
            stopRegistered = RegisterHotKey(0, StopId, Modifiers, 0x78); // F9
            if (!stopRegistered)
            {
                _ready.TrySetResult(new(false, Marshal.GetLastWin32Error(), "Ctrl+Alt+F9"));
                return;
            }
            if (Volatile.Read(ref _stopRequested) != 0) return;
            _gate.SetHotkeysReady(true);
            _ready.TrySetResult(new(true, 0, null));
            while (Volatile.Read(ref _stopRequested) == 0)
            {
                int result = GetMessageW(out var message, 0, 0, 0);
                if (result <= 0) break; // 0 = quit, -1 = failure; both fail closed in finally.
                if (message.Message != HotkeyMessage) continue;
                InputStopReason reason;
                if (message.WParam == (nuint)PauseId) reason = InputStopReason.Paused;
                else if (message.WParam == (nuint)StopId) reason = InputStopReason.Stopped;
                else continue;
                long start = Stopwatch.GetTimestamp();
                _gate.Trip(reason);
                Interlocked.Exchange(ref _lastCloseTicks, Stopwatch.GetTimestamp() - start);
                Interlocked.Increment(ref _messageCount);
            }
        }
        catch
        {
            // Public diagnostics never include arbitrary exception details or desktop contents.
            _ready.TrySetResult(new(false, -1, "MESSAGE_THREAD"));
        }
        finally
        {
            _gate.SetHotkeysReady(false);
            if (stopRegistered) _ = UnregisterHotKey(0, StopId);
            if (pauseRegistered) _ = UnregisterHotKey(0, PauseId);
            Volatile.Write(ref _queueReady, 0);
            _ready.TrySetResult(new(false, 0, "STOPPED"));
            _finished.TrySetResult();
        }
    }

    // Explicit diagnostic only: posts to OUR queue; it does not simulate a physical key press.
    internal bool PostDiagnosticSignal(bool stop) => Volatile.Read(ref _queueReady) != 0 &&
        PostThreadMessageW(_threadId, HotkeyMessage, (nuint)(stop ? StopId : PauseId), 0);

    public async ValueTask DisposeAsync()
    {
        _gate.Trip(InputStopReason.Shutdown);
        Interlocked.Exchange(ref _stopRequested, 1);
        if (Volatile.Read(ref _started) == 0) return;
        if (Volatile.Read(ref _queueReady) != 0) _ = PostThreadMessageW(_threadId, QuitMessage, 0, 0);
        // No GUI callback is required by this thread. Do not hang shutdown on an unexpected failure.
        await _finished.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out NativeMessage message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMessage message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);
}
