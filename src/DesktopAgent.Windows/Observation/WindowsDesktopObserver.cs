using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Windows.Observation;

internal sealed class WindowsDesktopObserver : IDesktopObserver, IAsyncDisposable
{
    private readonly Func<Lease, CancellationToken, Task<IAsyncDisposable>>? _hideForCapture;
    private readonly bool _preferForegroundWindow;
    private readonly Action<DesktopEnvironment>? _validateEnvironment;
    public WindowsDesktopObserver(Func<Lease, CancellationToken, Task<IAsyncDisposable>>? hideForCapture = null, bool preferForegroundWindow = false,
        Action<DesktopEnvironment>? validateEnvironment = null)
        => (_hideForCapture, _preferForegroundWindow, _validateEnvironment) = (hideForCapture, preferForegroundWindow, validateEnvironment);
    private readonly Win32DesktopEnvironment _environment = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Queue<(Frame Frame, OriginalPixels Pixels)> _cache = new();
    private Frame? _current;
    private long _captureNotBefore;
    private bool _disposed;
    internal void MarkInputApplied(bool desktopContextMenu = false) => Volatile.Write(ref _captureNotBefore,
        System.Diagnostics.Stopwatch.GetTimestamp() + (long)(System.Diagnostics.Stopwatch.Frequency * (desktopContextMenu ? 2.0 : 0.8)));
    internal Frame? CurrentFrame => Volatile.Read(ref _current);

    public Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct) => Task.Run(() => { ct.ThrowIfCancellationRequested(); return _environment.Read(); }, ct);

    public async Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? physicalRegion, CancellationToken ct)
    {
        lease.EnsureValid();
        await _serial.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Settings navigation can keep painting the previous page briefly after SendInput returns.
            // This cancellable observation delay uses no input or model request and does not certify stability.
            long ready = Volatile.Read(ref _captureNotBefore);
            if (ready != 0)
            {
                // Explorer's name column still showed the pre-toggle labels at 350 ms while UIA had updated.
                // Keep this bounded and cancellable; it is a paint-settle allowance, not proof of semantic agreement.
                // The actual desktop context menu first appeared after 1611 ms in native-03.
                // Only that input receives 2 s; other operations keep their existing 800 ms allowance.
                double remaining = (ready - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), ct);
            }
            var occupied = Win32DesktopEnvironment.OwnWindows();
            await using var suppression = _hideForCapture is null ? null : await _hideForCapture(lease, ct);
            return await Task.Run(() => Capture(lease, monitorId, physicalRegion, occupied, ct), ct);
        }
        finally { _serial.Release(); }
    }

    private Frame Capture(Lease lease, string monitorId, PhysicalRect? physicalRegion, System.Collections.Immutable.ImmutableArray<PhysicalRect> occupied, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var before = _environment.Read();
        _validateEnvironment?.Invoke(before);
        if (before.SessionState != DesktopSessionState.Available || before.Foreground is null) throw new InvalidOperationException("DESKTOP_UNAVAILABLE");
        if (before.Foreground.ProcessId == Environment.ProcessId) throw new InvalidOperationException("SELF_FOREGROUND");
        var monitor = before.Displays.SingleOrDefault(d => d.Id == monitorId) ?? throw new InvalidOperationException("MONITOR_UNAVAILABLE");
        PhysicalRect region = physicalRegion ?? monitor.Bounds;
        if (physicalRegion is null && _preferForegroundWindow)
        {
            var window = Win32DesktopEnvironment.VisibleWindowBounds(before.Foreground);
            int left = Math.Max(window.Left, monitor.Bounds.Left), top = Math.Max(window.Top, monitor.Bounds.Top);
            int width = checked((int)(Math.Min(window.Right, monitor.Bounds.Right) - left));
            int height = checked((int)(Math.Min(window.Bottom, monitor.Bounds.Bottom) - top));
            if (width < 16 || height < 16) throw new InvalidOperationException("FOREGROUND_OUTSIDE_MONITOR");
            region = new(left, top, width, height);
        }
        string? sourceId = null;
        if (!monitor.Bounds.Contains(region)) throw new InvalidOperationException("REGION_OUTSIDE_MONITOR");
        if (physicalRegion is not null)
        {
            if (_current is null || _current.MonitorId != monitorId || !_current.PhysicalRegion.Contains(region)) throw new InvalidOperationException("CROP_SOURCE_UNAVAILABLE");
            string? stale = FrameChecks.Validate(_current, lease, before, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90));
            if (stale is not null) throw new InvalidOperationException(stale);
            if (region.Width < 16 || region.Height < 16) throw new InvalidOperationException("CROP_TOO_SMALL");
            sourceId = _current.Id;
        }
        var ownWindows = occupied;
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        OriginalPixels? pixels = GdiScreenCapture.Capture(region);
        try
        {
            ct.ThrowIfCancellationRequested();
            var after = _environment.Read();
            _validateEnvironment?.Invoke(after);
            if (after.SessionState != DesktopSessionState.Available || after.DisplayGeneration != before.DisplayGeneration ||
                !FrameChecks.SameForeground(before.Foreground, after.Foreground)) throw new InvalidOperationException("DESKTOP_CHANGED_DURING_CAPTURE");
            if (GdiScreenCapture.IsBlack(pixels)) throw new InvalidOperationException("BLACK_CAPTURE");
            var image = GdiScreenCapture.Encode(pixels);
            var frame = new Frame(Guid.NewGuid().ToString("N"), lease, capturedAt, before.DisplayGeneration, monitor.Id,
                region, image, sourceId is null ? FrameViewKind.Overview : FrameViewKind.Crop, before.Foreground, ownWindows, sourceId);
            ct.ThrowIfCancellationRequested();
            _cache.Enqueue((frame, pixels));
            pixels = null;
            while (_cache.Count > 2) _cache.Dequeue().Pixels.Dispose();
            _current = frame;
            return frame;
        }
        finally { pixels?.Dispose(); }
    }

    public async Task<string?> CheckPointAsync(Frame frame, Lease lease, NormalizedPoint target, TimeSpan ttl, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var occupied = Win32DesktopEnvironment.OwnWindows();
            var mapped = InputCoordinates.ToPhysical(target, frame.PhysicalRegion);
            if (occupied.Any(r => FrameChecks.Contains(r, mapped))) return "OWN_WINDOW_TARGET";
            await using var suppression = _hideForCapture is null ? null : await _hideForCapture(lease, ct);
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_current, frame)) return "STALE_FRAME_ID";
                var cached = _cache.FirstOrDefault(c => ReferenceEquals(c.Frame, frame));
                if (cached.Pixels is null) return "ORIGINAL_PIXELS_UNAVAILABLE";
                var before = _environment.Read();
                _validateEnvironment?.Invoke(before);
                string? error = FrameChecks.Validate(frame, lease, before, DateTimeOffset.UtcNow, ttl);
                if (error is not null) return error;
                var point = InputCoordinates.ToPhysical(target, frame.PhysicalRegion);
                if (Win32DesktopEnvironment.OwnWindows().Any(r => FrameChecks.Contains(r, point))) return "OWN_WINDOW_TARGET";
                var region = FrameChecks.Around(point, frame.PhysicalRegion);
                using var pixels = GdiScreenCapture.Capture(region);
                ct.ThrowIfCancellationRequested();
                var after = _environment.Read();
                _validateEnvironment?.Invoke(after);
                error = FrameChecks.Validate(frame, lease, after, DateTimeOffset.UtcNow, ttl);
                if (error is not null) return error;
                return cached.Pixels.Compare(pixels).IsStale ? "TARGET_PIXELS_CHANGED" : null;
            }, ct);
        }
        finally { _serial.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync();
        try
        {
            _disposed = true;
            while (_cache.TryDequeue(out var entry)) entry.Pixels.Dispose();
            _current = null;
        }
        finally { _serial.Release(); }
    }
    internal async Task ClearAsync()
    {
        await _serial.WaitAsync();
        try { while (_cache.TryDequeue(out var entry)) entry.Pixels.Dispose(); _current = null; }
        finally { _serial.Release(); }
    }
}
