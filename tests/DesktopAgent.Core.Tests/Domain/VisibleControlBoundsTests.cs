using DesktopAgent.Core.Domain;

namespace DesktopAgent.Core.Tests.Domain;

public sealed class VisibleControlBoundsTests
{
    [Fact]
    public void DesktopIconAcrossLeftEdgeKeepsVisiblePortion()
    {
        Assert.True(VisibleControlBounds.TryClip(-2, 3, 130, 150, new(0, 0, 2560, 1440), out var result));
        Assert.Equal(new PhysicalRect(0, 3, 130, 147), result);
    }

    [Fact]
    public void DesktopListCanCrossRightAndBottomWithoutHidingItsChildren()
    {
        Assert.True(VisibleControlBounds.TryClip(0, 1, 2560, 1441, new(0, 0, 2560, 1440), out var result));
        Assert.Equal(new PhysicalRect(0, 1, 2560, 1439), result);
    }

    [Fact]
    public void CroppedOrNegativeOriginDisplayNeverProducesOutsidePoint()
    {
        var viewport = new PhysicalRect(-1920, 150, 1000, 800);
        Assert.True(VisibleControlBounds.TryClip(-2000.3, 100.4, -950.5, 1200, viewport, out var result));
        Assert.Equal(new PhysicalRect(-1920, 150, 970, 800), result);
        Assert.True(viewport.Contains(result));
    }

    [Theory]
    [InlineData(-132, 3, 0, 150)]
    [InlineData(2560, 5, 2600, 50)]
    [InlineData(10, 1440, 30, 1450)]
    [InlineData(double.NaN, 1, 50, 50)]
    [InlineData(1, 1, double.PositiveInfinity, 50)]
    [InlineData(1, 1, 1, 50)]
    [InlineData(1, 50, 60, 40)]
    public void InvisibleOrInvalidControlsAreRejected(double left, double top, double right, double bottom)
    {
        Assert.False(VisibleControlBounds.TryClip(left, top, right, bottom, new(0, 0, 2560, 1440), out _));
    }
}
