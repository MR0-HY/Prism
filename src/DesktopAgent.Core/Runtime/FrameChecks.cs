using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Runtime;

public static class FrameChecks
{
    public static string? Validate(Frame frame, Lease lease, DesktopEnvironment environment, DateTimeOffset now, TimeSpan ttl)
    {
        if (frame.Lease != lease) return "STALE_LEASE";
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(3) || now - frame.CapturedAtUtc > ttl || frame.CapturedAtUtc > now.AddSeconds(1)) return "STALE_FRAME";
        if (environment.SessionState != DesktopSessionState.Available) return "DESKTOP_UNAVAILABLE";
        if (environment.DisplayGeneration != frame.DisplayGeneration) return "DISPLAY_CHANGED";
        if (!SameForeground(frame.Foreground, environment.Foreground)) return "FOREGROUND_CHANGED";
        var monitor = environment.Displays.FirstOrDefault(d => d.Id == frame.MonitorId);
        if (monitor is null || !monitor.Bounds.Contains(frame.PhysicalRegion)) return "DISPLAY_CHANGED";
        return null;
    }

    public static bool SameForeground(ForegroundIdentity? first, ForegroundIdentity? second) => first is not null && second is not null &&
        first.HwndHex == second.HwndHex && first.ProcessId == second.ProcessId && first.WindowRect == second.WindowRect &&
        first.ProcessName == second.ProcessName && first.WindowClass == second.WindowClass;

    public static PhysicalRect MapRegion(NormalizedRect rect, PhysicalRect frame)
    {
        if (!frame.IsValid || !double.IsFinite(rect.X0) || !double.IsFinite(rect.Y0) || !double.IsFinite(rect.X1) || !double.IsFinite(rect.Y1) ||
            rect.X0 < 0 || rect.Y0 < 0 || rect.X1 > 1000 || rect.Y1 > 1000 || rect.X0 >= rect.X1 || rect.Y0 >= rect.Y1)
            throw new ArgumentOutOfRangeException(nameof(rect));
        int left = checked(frame.Left + (int)Math.Floor(rect.X0 * frame.Width / 1000));
        int top = checked(frame.Top + (int)Math.Floor(rect.Y0 * frame.Height / 1000));
        int right = checked(frame.Left + (int)Math.Ceiling(rect.X1 * frame.Width / 1000));
        int bottom = checked(frame.Top + (int)Math.Ceiling(rect.Y1 * frame.Height / 1000));
        if (right - left < 16 || bottom - top < 16) throw new ArgumentException("CROP_TOO_SMALL");
        return new(left, top, right - left, bottom - top);
    }

    public static PhysicalRect Around(PhysicalPoint point, PhysicalRect bounds, int radius = 32)
    {
        if (radius is < 1 or > 256 || !Contains(bounds, point)) throw new ArgumentOutOfRangeException(nameof(point));
        int left = (int)Math.Max(bounds.Left, (long)point.X - radius), top = (int)Math.Max(bounds.Top, (long)point.Y - radius);
        int right = (int)Math.Min(bounds.Right, (long)point.X + radius + 1), bottom = (int)Math.Min(bounds.Bottom, (long)point.Y + radius + 1);
        return new(left, top, right - left, bottom - top);
    }
    public static bool Contains(PhysicalRect rect, PhysicalPoint point) => rect.IsValid &&
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    public static bool Overlaps(PhysicalRect first, PhysicalRect second) => first.IsValid && second.IsValid &&
        first.Left < second.Right && first.Right > second.Left && first.Top < second.Bottom && first.Bottom > second.Top;
}

public sealed record PixelDifference(double MeanChannelDifference, double ChangedPixelFraction, bool IsStale);

/// <summary>Original BGR32 pixels with physical bounds. Never serialized or used as a model response.</summary>
public sealed class OriginalPixels : IDisposable
{
    private byte[]? _bytes;
    public PhysicalRect Region { get; }
    public int Stride => checked(Region.Width * 4);
    [System.Text.Json.Serialization.JsonIgnore] public ReadOnlyMemory<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(OriginalPixels));
    public OriginalPixels(PhysicalRect region, ReadOnlySpan<byte> bytes)
    {
        if (!region.IsValid || (long)region.Width * region.Height > 16 * 1024 * 1024 || bytes.Length != (long)region.Width * region.Height * 4)
            throw new ArgumentException("INVALID_PIXEL_BUFFER");
        Region = region;
        _bytes = bytes.ToArray();
    }
    public PixelDifference Compare(OriginalPixels current, double threshold = 12, double changedFraction = .20)
    {
        if (!Region.Contains(current.Region) || threshold is < 0 or > 255 || !double.IsFinite(threshold) ||
            changedFraction is < 0 or > 1 || !double.IsFinite(changedFraction)) throw new ArgumentException("INVALID_ROI_COMPARISON");
        ReadOnlySpan<byte> old = Bytes.Span, next = current.Bytes.Span;
        long sum = 0, changed = 0;
        int offsetX = current.Region.Left - Region.Left, offsetY = current.Region.Top - Region.Top;
        for (int y = 0; y < current.Region.Height; y++)
            for (int x = 0; x < current.Region.Width; x++)
            {
                int a = (offsetY + y) * Stride + (offsetX + x) * 4, b = y * current.Stride + x * 4;
                int db = Math.Abs(old[a] - next[b]), dg = Math.Abs(old[a + 1] - next[b + 1]), dr = Math.Abs(old[a + 2] - next[b + 2]);
                sum += db + dg + dr;
                if (Math.Max(db, Math.Max(dg, dr)) > threshold) changed++;
            }
        long pixels = (long)current.Region.Width * current.Region.Height;
        double mean = sum / (pixels * 3d), fraction = changed / (double)pixels;
        return new(mean, fraction, mean > threshold || fraction > changedFraction);
    }
    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) Array.Clear(bytes);
    }
    public override string ToString() => "OriginalPixels(bytes omitted)";
}
