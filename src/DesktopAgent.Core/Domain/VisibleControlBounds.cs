namespace DesktopAgent.Core.Domain;

/// <summary>UIA reports the entire control, which can cross a capture edge. Only expose its visible
/// intersection; native resolution must still hit and re-identify the actual control before input.</summary>
public static class VisibleControlBounds
{
    public static bool TryClip(double left, double top, double right, double bottom, PhysicalRect viewport, out PhysicalRect bounds)
    {
        bounds = default;
        if (!viewport.IsValid || !double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue || right - left < 1 || bottom - top < 1)
            return false;
        int clippedLeft = Math.Max(viewport.Left, (int)Math.Floor(left));
        int clippedTop = Math.Max(viewport.Top, (int)Math.Floor(top));
        long clippedRight = Math.Min(viewport.Right, (long)Math.Ceiling(right));
        long clippedBottom = Math.Min(viewport.Bottom, (long)Math.Ceiling(bottom));
        if (clippedRight <= clippedLeft || clippedBottom <= clippedTop) return false;
        bounds = new(clippedLeft, clippedTop, checked((int)(clippedRight - clippedLeft)), checked((int)(clippedBottom - clippedTop)));
        return true;
    }
}
