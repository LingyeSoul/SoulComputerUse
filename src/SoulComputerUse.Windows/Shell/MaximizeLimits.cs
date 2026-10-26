using System;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// The bounds a window with no non-client area must report for maximizing and for tracking: it grows
/// to the monitor work area, never to the whole monitor, so the taskbar stays visible.
/// </summary>
public readonly record struct MaximizeLimits(int PositionX, int PositionY, int SizeX, int SizeY)
{
    /// <summary>
    /// Computes the maximized position and size from a monitor rectangle and its work area. The
    /// position is relative to the monitor's top-left corner, which is what the window manager expects.
    /// </summary>
    public static MaximizeLimits FromWorkArea(
        int monitorLeft,
        int monitorTop,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom)
    {
        if (workRight <= workLeft)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workRight),
                workRight,
                $"The work area width is {workRight - workLeft}, so there is nothing to maximize into.");
        }

        if (workBottom <= workTop)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workBottom),
                workBottom,
                $"The work area height is {workBottom - workTop}, so there is nothing to maximize into.");
        }

        return new MaximizeLimits(
            workLeft - monitorLeft,
            workTop - monitorTop,
            workRight - workLeft,
            workBottom - workTop);
    }
}
