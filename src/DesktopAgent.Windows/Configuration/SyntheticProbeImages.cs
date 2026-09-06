using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopAgent.Core.Domain;

namespace DesktopAgent.Windows.Configuration;

/// <summary>Renders invented data only. Crops always come from original pixels, never an upscaled preview.</summary>
internal static class SyntheticProbeImages
{
    private static int Next(int min, int max) => RandomNumberGenerator.GetInt32(min, max);
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static string Code() => string.Concat(Enumerable.Range(0, 6).Select(_ => Alphabet[Next(0, Alphabet.Length)]));
    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush? brush = null)
        => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush ?? Brushes.Black, 1), new Point(x, y));
    private static FrameImage Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new(bitmap.PixelWidth, bitmap.PixelHeight, "image/png", stream.ToArray());
    }
    private static RenderTargetBitmap Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height)); draw(dc); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
    public static ImmutableArray<GeneratedProbeImage> Create()
    {
        var cases = ImmutableArray.CreateBuilder<GeneratedProbeImage>();
        for (int i = 0; i < 2; i++)
        {
            bool orangeLeft = i == 0;
            string code = Code();
            int blueX = orangeLeft ? Next(490, 640) : Next(70, 170), y = Next(130, 290);
            int orangeX = orangeLeft ? Next(60, 180) : Next(580, 680);
            var bitmap = Render(960, 560, dc =>
            {
                Text(dc, "Visual check", 42, 30, 28);
                dc.DrawRectangle(Brushes.RoyalBlue, null, new Rect(blueX, y, 250, 95));
                Text(dc, code, blueX + 24, y + 22, 32, Brushes.White);
                dc.DrawRectangle(Brushes.Orange, null, new Rect(orangeX + 10, y + 6, 160, 78));
                Text(dc, Code(), 380, 470, 24, Brushes.Gray);
            });
            cases.Add(new(Guid.NewGuid().ToString("N"), Encode(bitmap), new(ProbeKind.ReadImage, code, orangeLeft ? "left" : "right", null)));
        }
        for (int i = 0; i < 6; i++)
        {
            bool small = i >= 4;
            int width = small ? 2560 : 1000, height = small ? 1440 : 720;
            int panelX = small ? Next(250, 1700) : Next(50, 180), panelY = small ? Next(180, 850) : Next(65, 120);
            int panelWidth = small ? 600 : 720, rowHeight = small ? 36 : 57, target = Next(1, 7);
            string[] codes = Enumerable.Range(0, 8).Select(_ => Code()).ToArray();
            int toggleX = panelX + panelWidth - (small ? 90 : 120), toggleWidth = small ? 40 : 60, toggleHeight = small ? 18 : 28;
            int top = panelY + 50;
            var hit = new PhysicalRect(toggleX, top + target * rowHeight + 5, toggleWidth, toggleHeight);
            var bitmap = Render(width, height, dc =>
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(233, 238, 246)), null, new Rect(0, 0, width, height));
                Text(dc, "Settings preview", 35, 20, small ? 24 : 28);
                dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.SlateGray, 1),
                    new Rect(panelX, panelY, panelWidth, rowHeight * 8 + 65), 8, 8);
                Text(dc, "Display preferences", panelX + 20, panelY + 12, small ? 16 : 22);
                for (int row = 0; row < 8; row++)
                {
                    int rowY = top + row * rowHeight;
                    Text(dc, (row == target ? "★ " : "   ") + codes[row] + "  显示选项", panelX + 20, rowY, small ? 14 : 24);
                    bool enabled = Next(0, 2) == 1;
                    dc.DrawRoundedRectangle(enabled ? Brushes.RoyalBlue : Brushes.SlateGray, null,
                        new Rect(toggleX, rowY + 5, toggleWidth, toggleHeight), toggleHeight / 2d, toggleHeight / 2d);
                    double radius = toggleHeight / 2d - 3;
                    dc.DrawEllipse(Brushes.White, null, new Point(toggleX + (enabled ? toggleWidth - toggleHeight / 2d : toggleHeight / 2d), rowY + 5 + toggleHeight / 2d), radius, radius);
                }
            });
            FrameImage current = Encode(bitmap);
            FrameImage? overview = null;
            if (small)
            {
                // Capture the whole panel, not just the correct target. All neighboring rows remain visible.
                var crop = new Int32Rect(panelX - 20, panelY - 20, panelWidth + 40, rowHeight * 8 + 105);
                overview = current;
                current = Encode(new CroppedBitmap(bitmap, crop));
                hit = new(hit.Left - crop.X, hit.Top - crop.Y, hit.Width, hit.Height);
            }
            cases.Add(new(Guid.NewGuid().ToString("N"), current, new(ProbeKind.GroundPoint, codes[target], null, hit), overview));
        }
        return cases.ToImmutable();
    }

    public static ImmutableArray<GeneratedProbeImage> CreateGroundingDiagnostic()
    {
        var scene = Create()[2];
        // Independent requests share the exact pixels and local truth; only ID and guidance differ.
        return [scene, scene with { Id = Guid.NewGuid().ToString("N") }];
    }
}
