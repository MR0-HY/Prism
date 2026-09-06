using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Windows.Observation;

internal static class GdiScreenCapture
{
    public static OriginalPixels Capture(PhysicalRect region)
    {
        if (!region.IsValid || (long)region.Width * region.Height > 16 * 1024 * 1024) throw new ArgumentException("CAPTURE_TOO_LARGE");
        using var dpi = new PhysicalDpiScope();
        nint screen = 0, memory = 0, bitmap = 0, previous = 0;
        try
        {
            screen = GetDC(0);
            if (screen == 0) throw new InvalidOperationException("SCREEN_DC_UNAVAILABLE");
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw new InvalidOperationException("MEMORY_DC_UNAVAILABLE");
            var info = new BitmapInfo { Header = new() { Size = 40, Width = region.Width, Height = -region.Height, Planes = 1, Bits = 32 } };
            bitmap = CreateDIBSection(screen, ref info, 0, out nint data, 0, 0);
            if (bitmap == 0 || data == 0) throw new InvalidOperationException("BITMAP_UNAVAILABLE");
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) throw new InvalidOperationException("BITMAP_SELECT_FAILED");
            if (!BitBlt(memory, 0, 0, region.Width, region.Height, screen, region.Left, region.Top, 0x00CC0020 | 0x40000000) || !GdiFlush())
                throw new InvalidOperationException("SCREEN_CAPTURE_FAILED");
            byte[] bytes = new byte[checked(region.Width * region.Height * 4)];
            try
            {
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return new OriginalPixels(region, bytes);
            }
            finally { Array.Clear(bytes); }
        }
        finally
        {
            if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }

    public static FrameImage Encode(OriginalPixels pixels, int maximumEdge = 1600)
    {
        if (maximumEdge is < 640 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        byte[] bytes = pixels.Bytes.ToArray();
        try
        {
            BitmapSource source = BitmapSource.Create(pixels.Region.Width, pixels.Region.Height, 96, 96,
                PixelFormats.Bgr32, null, bytes, pixels.Stride);
            double scale = Math.Min(1, maximumEdge / (double)Math.Max(source.PixelWidth, source.PixelHeight));
            if (scale < 1) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return new(source.PixelWidth, source.PixelHeight, "image/png", stream.ToArray());
        }
        finally { Array.Clear(bytes); }
    }

    public static bool IsBlack(OriginalPixels pixels)
    {
        var bytes = pixels.Bytes.Span;
        for (int i = 0; i < bytes.Length; i += 4) if (bytes[i] > 3 || bytes[i + 1] > 3 || bytes[i + 2] > 3) return false;
        return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Color; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint bitmap);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
