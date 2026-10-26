using System;
using System.Drawing;

namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// Pure rectangle arithmetic for the occlusion check: how much of a target rectangle a foreground
/// window actually covers. The capture uses it to refuse a screen-region frame that would show the
/// host's own windows instead of the remote picture.
/// </summary>
public static class OcclusionCalculator
{
    /// <summary>
    /// Returns the part of <paramref name="target"/> covered by <paramref name="foreground"/>, as the
    /// intersection area divided by the target area. The result lies in 0..1: 0 means no intersection
    /// and 1 means the foreground covers the target completely. A foreground without area covers
    /// nothing.
    /// </summary>
    /// <param name="target">The rectangle that must stay visible; it needs a positive width and height.</param>
    /// <param name="foreground">The rectangle that may cover it.</param>
    /// <returns>The covered fraction of the target in 0..1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The target width or height is not positive.</exception>
    public static double OverlapRatio(Rectangle target, Rectangle foreground)
    {
        if (target.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "The target rectangle needs a positive width.");
        }

        if (target.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "The target rectangle needs a positive height.");
        }

        long targetArea = (long)target.Width * target.Height;
        long overlapArea = IntersectionArea(target, foreground);
        return (double)overlapArea / targetArea;
    }

    private static long IntersectionArea(Rectangle left, Rectangle right)
    {
        int intersectionLeft = Math.Max(left.Left, right.Left);
        int intersectionTop = Math.Max(left.Top, right.Top);
        int intersectionRight = Math.Min(left.Right, right.Right);
        int intersectionBottom = Math.Min(left.Bottom, right.Bottom);
        if (intersectionRight <= intersectionLeft || intersectionBottom <= intersectionTop)
        {
            return 0;
        }

        return (long)(intersectionRight - intersectionLeft) * (intersectionBottom - intersectionTop);
    }
}
