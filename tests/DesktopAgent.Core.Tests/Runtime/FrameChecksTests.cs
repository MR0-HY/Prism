using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class FrameChecksTests
{
    [Theory]
    [InlineData(-2560, -400, 2560, 1440)]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(3840, -720, 3840, 2160)]
    public void CropsUseExclusiveEdgesAndNestedPhysicalOrigins(int left, int top, int width, int height)
    {
        var whole = new PhysicalRect(left, top, width, height);
        Assert.Equal(whole, FrameChecks.MapRegion(new(0, 0, 1000, 1000), whole));
        var half = FrameChecks.MapRegion(new(500, 500, 1000, 1000), whole);
        Assert.Equal(left + width / 2, half.Left);
        Assert.Equal(top + height / 2, half.Top);
        var quarter = FrameChecks.MapRegion(new(500, 500, 1000, 1000), half);
        Assert.Equal(whole.Right, quarter.Right);
        Assert.Equal(whole.Bottom, quarter.Bottom);
        Assert.Equal(new PhysicalPoint((int)whole.Right - 1, (int)whole.Bottom - 1), InputCoordinates.ToPhysical(new(1000, 1000), quarter));
    }

    [Fact]
    public void InvalidTinyAndNonFiniteCropsCannotBeClamped()
    {
        var region = new PhysicalRect(0, 0, 1000, 1000);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameChecks.MapRegion(new(-1, 0, 100, 100), region));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameChecks.MapRegion(new(0, 0, double.NaN, 100), region));
        Assert.Throws<ArgumentException>(() => FrameChecks.MapRegion(new(0, 0, 15, 100), region));
        Assert.Equal(new PhysicalRect(0, 0, 33, 33), FrameChecks.Around(new(0, 0), region));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameChecks.Around(new(1000, 0), region));
    }

    [Fact]
    public void IdentityClockSessionAndTopologyChangesInvalidateFrame()
    {
        var rect = new PhysicalRect(0, 0, 1000, 800);
        var lease = new Lease(Guid.NewGuid(), 0);
        var foreground = new ForegroundIdentity("42", 100, "fixture", rect);
        var now = DateTimeOffset.UtcNow;
        var frame = new Frame("frame", lease, now, 1, "display", rect, new(1000, 800, "image/png", [1]), FrameViewKind.Overview, foreground, []);
        var env = new DesktopEnvironment(1, [new("display", rect, rect, 144, 144, true)], foreground, DesktopSessionState.Available);
        string? Check(DesktopEnvironment value, Lease? identity = null, DateTimeOffset? time = null)
            => FrameChecks.Validate(frame, identity ?? lease, value, time ?? now, TimeSpan.FromSeconds(90));
        Assert.Null(Check(env));
        Assert.Equal("STALE_LEASE", Check(env, lease with { Epoch = 1 }));
        Assert.Equal("STALE_FRAME", Check(env, time: now.AddSeconds(91)));
        Assert.Equal("STALE_FRAME", Check(env, time: now.AddSeconds(-5)));
        Assert.Equal("DISPLAY_CHANGED", Check(env with { DisplayGeneration = 2 }));
        Assert.Equal("DESKTOP_UNAVAILABLE", Check(env with { SessionState = DesktopSessionState.Locked }));
        Assert.Equal("FOREGROUND_CHANGED", Check(env with { Foreground = foreground with { WindowRect = new(1, 0, 1000, 800) } }));
        Assert.Equal("FOREGROUND_CHANGED", Check(env with { Foreground = foreground with { ProcessId = 101 } }));
        Assert.Equal("FOREGROUND_CHANGED", Check(env with { Foreground = null }));
    }

    [Fact]
    public void PixelComparisonUsesOriginalOffsetAndIgnoresAlpha()
    {
        byte[] image = new byte[100 * 100 * 4];
        for (int i = 0; i < image.Length; i++) image[i] = (byte)(i % 251);
        var region = new PhysicalRect(-100, 25, 100, 100);
        using var original = new OriginalPixels(region, image);
        byte[] roi = new byte[20 * 20 * 4];
        for (int y = 0; y < 20; y++) Array.Copy(image, (30 + y) * 400 + 40 * 4, roi, y * 80, 80);
        for (int i = 3; i < roi.Length; i += 4) roi[i] = 0;
        using var same = new OriginalPixels(new(-60, 55, 20, 20), roi);
        Assert.False(original.Compare(same).IsStale);
        for (int i = 0; i < roi.Length; i++) roi[i] = 255;
        using var changed = new OriginalPixels(same.Region, roi);
        Assert.True(original.Compare(changed).IsStale);
    }

    [Fact]
    public void SmallAnimatedPatchDoesNotRequireWholeScreenEqualityAndBuffersClear()
    {
        byte[] image = new byte[100 * 100 * 4];
        using var original = new OriginalPixels(new(0, 0, 100, 100), image);
        Array.Fill(image, (byte)255, 0, 100 * 4); // 1% of original ROI.
        using var changed = new OriginalPixels(original.Region, image);
        Assert.False(original.Compare(changed).IsStale);
        var owned = original.Bytes;
        original.Dispose();
        Assert.All(owned.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => original.Bytes);
    }
}
