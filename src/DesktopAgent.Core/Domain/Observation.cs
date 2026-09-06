using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace DesktopAgent.Core.Domain;

public readonly record struct Lease(Guid TaskId, long Epoch)
{
    public void EnsureValid()
    {
        if (TaskId == Guid.Empty || Epoch < 0)
            throw new ArgumentException("A lease requires a nonempty task ID and nonnegative epoch.");
    }
}

/// <summary>Physical pixels, with an exclusive right/bottom edge. Negative origins are valid.</summary>
public readonly record struct PhysicalRect
{
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public long Right => (long)Left + Width;
    public long Bottom => (long)Top + Height;

    public PhysicalRect(int left, int top, int width, int height)
    {
        if (width < 1 || height < 1 || (long)left + width > int.MaxValue || (long)top + height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid physical rectangle.");
        (Left, Top, Width, Height) = (left, top, width, height);
    }

    public bool IsValid => Width > 0 && Height > 0;
    public bool Contains(PhysicalRect other) => IsValid && other.IsValid &&
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;
}

public enum FrameViewKind { Overview, Crop }
public enum DesktopSessionState { Available, Locked, Disconnected, Unavailable }

public sealed record ForegroundIdentity(string HwndHex, int ProcessId, string ProcessName, PhysicalRect WindowRect)
{
    // Native observation only; never supplied by a model action.
    public string? WindowClass { get; init; }
}
public sealed record DisplayInfo(string Id, PhysicalRect Bounds, PhysicalRect WorkArea, uint DpiX, uint DpiY, bool IsPrimary);
public sealed record DesktopEnvironment(long DisplayGeneration, ImmutableArray<DisplayInfo> Displays,
    ForegroundIdentity? Foreground, DesktopSessionState SessionState);

/// <summary>Owns a copy of encoded bytes. Image bytes never enter normal JSON serialization.</summary>
[JsonConverter(typeof(FrameImageMetadataConverter))]
public sealed class FrameImage
{
    private readonly byte[] _bytes;
    public int Width { get; }
    public int Height { get; }
    public string Mime { get; }
    [JsonIgnore] public ReadOnlyMemory<byte> Bytes => _bytes;

    public FrameImage(int width, int height, string mime, ReadOnlySpan<byte> bytes)
    {
        if (width < 1 || height < 1 || width > 16384 || height > 16384)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (mime is not ("image/png" or "image/jpeg"))
            throw new ArgumentException("Only PNG and JPEG frames are supported.", nameof(mime));
        if (bytes.IsEmpty || bytes.Length > 20 * 1024 * 1024)
            throw new ArgumentException("Frame bytes are required and limited to 20 MiB.", nameof(bytes));
        (Width, Height, Mime) = (width, height, mime);
        _bytes = bytes.ToArray();
    }

    public override string ToString() => $"FrameImage({Width}x{Height}, {Mime}, bytes omitted)";
}

// A memory-only frame cannot be reconstructed from diagnostic metadata. An explicit writer also
// avoids System.Text.Json inspecting the ReadOnlySpan constructor for deserialization metadata.
internal sealed class FrameImageMetadataConverter : JsonConverter<FrameImage>
{
    public override FrameImage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Images must come from the observer, not diagnostic JSON.");

    public override void Write(Utf8JsonWriter writer, FrameImage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(options.PropertyNamingPolicy?.ConvertName(nameof(FrameImage.Width)) ?? nameof(FrameImage.Width), value.Width);
        writer.WriteNumber(options.PropertyNamingPolicy?.ConvertName(nameof(FrameImage.Height)) ?? nameof(FrameImage.Height), value.Height);
        writer.WriteString(options.PropertyNamingPolicy?.ConvertName(nameof(FrameImage.Mime)) ?? nameof(FrameImage.Mime), value.Mime);
        writer.WriteEndObject();
    }
}

public sealed class Frame
{
    public string Id { get; }
    public Guid TaskId => Lease.TaskId;
    public long Epoch => Lease.Epoch;
    [JsonIgnore] public Lease Lease { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public long DisplayGeneration { get; }
    public string MonitorId { get; }
    public PhysicalRect PhysicalRegion { get; }
    public FrameImage Image { get; }
    public FrameViewKind ViewKind { get; }
    public ForegroundIdentity Foreground { get; }
    public ImmutableArray<PhysicalRect> OwnWindowRects { get; }
    public string? SourceFrameId { get; }

    public Frame(string id, Lease lease, DateTimeOffset capturedAtUtc, long displayGeneration,
        string monitorId, PhysicalRect physicalRegion, FrameImage image, FrameViewKind viewKind,
        ForegroundIdentity foreground, ImmutableArray<PhysicalRect> ownWindowRects, string? sourceFrameId = null)
    {
        lease.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(foreground);
        if (id.Length > 128 || monitorId.Length > 128 || displayGeneration < 0 || !physicalRegion.IsValid ||
            !foreground.WindowRect.IsValid || foreground.ProcessId <= 0 || string.IsNullOrWhiteSpace(foreground.HwndHex) ||
            ownWindowRects.IsDefault || ownWindowRects.Any(r => !r.IsValid) || !Enum.IsDefined(viewKind))
            throw new ArgumentException("Invalid frame metadata.");
        if ((viewKind == FrameViewKind.Crop && (string.IsNullOrWhiteSpace(sourceFrameId) || sourceFrameId == id)) ||
            (viewKind == FrameViewKind.Overview && sourceFrameId is not null))
            throw new ArgumentException("A crop requires a different source frame; an overview has no parent.");
        (Id, Lease, CapturedAtUtc, DisplayGeneration, MonitorId, PhysicalRegion, Image, ViewKind, Foreground, OwnWindowRects, SourceFrameId) =
            (id, lease, capturedAtUtc.ToUniversalTime(), displayGeneration, monitorId, physicalRegion, image, viewKind, foreground, ownWindowRects, sourceFrameId);
    }

    public override string ToString() => $"Frame({Id}, task={TaskId}, epoch={Epoch}, {ViewKind}; image omitted)";
}
