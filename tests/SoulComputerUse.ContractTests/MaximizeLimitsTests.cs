using System;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the maximize limits a frameless window reports: the work area, not the monitor,
/// and the same size again as the tracking limit so a drag cannot cross the taskbar.
/// </summary>
[TestClass]
public sealed class MaximizeLimitsTests
{
    [TestMethod]
    public void AMonitorWithATaskbarAtTheBottomMaximizesToTheWorkArea()
    {
        // A 1920x1080 monitor with a 48 pixel taskbar at the bottom.
        MaximizeLimits limits = MaximizeLimits.FromWorkArea(0, 0, 0, 0, 1920, 1032);

        Assert.AreEqual(0, limits.PositionX);
        Assert.AreEqual(0, limits.PositionY);
        Assert.AreEqual(1920, limits.SizeX);
        Assert.AreEqual(1032, limits.SizeY, "The height must stop at the work area, otherwise the taskbar is covered.");
    }

    [TestMethod]
    public void ASecondMonitorReportsItsPositionRelativeToItsOwnCorner()
    {
        // A second monitor to the right, whose work area does not start at its own origin because its
        // taskbar sits on the left edge.
        MaximizeLimits limits = MaximizeLimits.FromWorkArea(1920, 0, 1968, 0, 3840, 1080);

        Assert.AreEqual(48, limits.PositionX, "The position is relative to the monitor's own top-left corner.");
        Assert.AreEqual(0, limits.PositionY);
        Assert.AreEqual(1872, limits.SizeX);
        Assert.AreEqual(1080, limits.SizeY);
    }

    [TestMethod]
    public void ATaskbarAtTheTopMovesTheMaximizedWindowDown()
    {
        MaximizeLimits limits = MaximizeLimits.FromWorkArea(0, 0, 0, 40, 1600, 900);

        Assert.AreEqual(0, limits.PositionX);
        Assert.AreEqual(40, limits.PositionY, "A top taskbar pushes the maximized window down by its height.");
        Assert.AreEqual(1600, limits.SizeX);
        Assert.AreEqual(860, limits.SizeY);
    }

    [TestMethod]
    public void ADegenerateWorkAreaIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MaximizeLimits.FromWorkArea(0, 0, 100, 0, 100, 900));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MaximizeLimits.FromWorkArea(0, 0, 0, 500, 1600, 500));
    }
}
