using System;
using System.Collections.Generic;
using System.Drawing;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Adversarial checks for the desktop picture layout and the maximize limits: extreme aspect ratios,
/// odd leftovers, multi-monitor origins and degenerate inputs. These are the numbers the window will
/// paint with, so a wrong branch here shows up as a cropped or drifting picture rather than a crash.
/// </summary>
[TestClass]
public sealed class DesktopLayoutAdversarialTests
{
    /// <summary>Whatever the geometry, the picture must stay inside the client area and keep a size.</summary>
    [TestMethod]
    public void ThePictureAlwaysStaysInsideTheClientAreaAndKeepsASize()
    {
        List<(int ClientWidth, int ClientHeight, int SessionWidth, int SessionHeight)> geometries =
        [
            (1, 1, 1, 1),
            (1600, 900, 4, 3),
            (4, 3, 1600, 900),
            (1000, 1000, 1, 10000),
            (10000, 1, 1, 10000),
            (1, 10000, 10000, 1),
            (int.MaxValue / 2, 3, 3, int.MaxValue / 2),
            (3, int.MaxValue / 2, int.MaxValue / 2, 3),
            (1920, 1080, 1080, 1920),
        ];

        foreach ((int clientWidth, int clientHeight, int sessionWidth, int sessionHeight) in geometries)
        {
            Rectangle fitted = DesktopPictureLayout.Fit(clientWidth, clientHeight, sessionWidth, sessionHeight);

            Assert.IsGreaterThanOrEqualTo(1, fitted.Width, $"The picture must keep a width for {clientWidth}x{clientHeight} holding {sessionWidth}x{sessionHeight}.");
            Assert.IsGreaterThanOrEqualTo(1, fitted.Height, $"The picture must keep a height for {clientWidth}x{clientHeight} holding {sessionWidth}x{sessionHeight}.");
            Assert.IsLessThanOrEqualTo(clientWidth, fitted.Right, $"The picture must not run past the right edge for {clientWidth}x{clientHeight} holding {sessionWidth}x{sessionHeight}.");
            Assert.IsLessThanOrEqualTo(clientHeight, fitted.Bottom, $"The picture must not run past the bottom edge for {clientWidth}x{clientHeight} holding {sessionWidth}x{sessionHeight}.");
            Assert.IsGreaterThanOrEqualTo(0, fitted.Left, $"The picture must not start left of the client area for {clientWidth}x{clientHeight}.");
            Assert.IsGreaterThanOrEqualTo(0, fitted.Top, $"The picture must not start above the client area for {clientWidth}x{clientHeight}.");
        }
    }

    /// <summary>The two black bars of a letterbox must never differ by more than one pixel.</summary>
    [TestMethod]
    public void LetterboxBarsDifferByAtMostOnePixel()
    {
        List<(int ClientWidth, int ClientHeight, int SessionWidth, int SessionHeight)> geometries =
        [
            (1601, 900, 4, 3),
            (1600, 901, 16, 9),
            (999, 501, 5, 4),
            (1279, 719, 3, 2),
        ];

        foreach ((int clientWidth, int clientHeight, int sessionWidth, int sessionHeight) in geometries)
        {
            Rectangle fitted = DesktopPictureLayout.Fit(clientWidth, clientHeight, sessionWidth, sessionHeight);
            int leftBar = fitted.Left;
            int rightBar = clientWidth - fitted.Right;
            int topBar = fitted.Top;
            int bottomBar = clientHeight - fitted.Bottom;

            Assert.IsLessThanOrEqualTo(1, Math.Abs(leftBar - rightBar), $"The side bars are uneven for {clientWidth}x{clientHeight} holding {sessionWidth}x{sessionHeight}.");
            Assert.IsLessThanOrEqualTo(1, Math.Abs(topBar - bottomBar), $"The top and bottom bars are uneven for {clientWidth}x{clientHeight} holding {sessionWidth}x{sessionHeight}.");
        }
    }

    /// <summary>A picture with exactly the client's aspect ratio must fill it completely, with no bars.</summary>
    [TestMethod]
    public void AMatchingAspectRatioFillsTheClientExactly()
    {
        foreach ((int clientWidth, int clientHeight, int sessionWidth, int sessionHeight) geometry in
                 new List<(int, int, int, int)> { (1600, 900, 1600, 900), (800, 450, 1600, 900), (400, 400, 1, 1) })
        {
            Rectangle fitted = DesktopPictureLayout.Fit(
                geometry.clientWidth,
                geometry.clientHeight,
                geometry.sessionWidth,
                geometry.sessionHeight);

            Assert.AreEqual(new Rectangle(0, 0, geometry.clientWidth, geometry.clientHeight), fitted);
        }
    }

    /// <summary>The fit keeps the session aspect ratio within a pixel of rounding.</summary>
    [TestMethod]
    public void TheFittedPictureKeepsTheSessionAspectRatio()
    {
        Rectangle fitted = DesktopPictureLayout.Fit(1601, 901, 4, 3);

        double sessionRatio = 4.0 / 3.0;
        double fittedRatio = (double)fitted.Width / fitted.Height;

        Assert.IsLessThanOrEqualTo(0.01, Math.Abs(sessionRatio - fittedRatio), "The fitted picture drifted off the session aspect ratio.");
    }

    /// <summary>Non-positive sizes must be refused instead of producing a broken rectangle.</summary>
    [TestMethod]
    public void NonPositiveSizesAreRefused()
    {
        List<Action> brokenCases =
        [
            () => DesktopPictureLayout.Fit(0, 100, 10, 10),
            () => DesktopPictureLayout.Fit(100, 0, 10, 10),
            () => DesktopPictureLayout.Fit(100, 100, 0, 10),
            () => DesktopPictureLayout.Fit(100, 100, 10, 0),
            () => DesktopPictureLayout.Fit(-1, 100, 10, 10),
        ];

        foreach (Action broken in brokenCases)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(broken);
        }
    }

    /// <summary>A monitor to the left of the primary one must still maximize relative to itself.</summary>
    [TestMethod]
    public void AMonitorLeftOfThePrimaryKeepsItsOwnOrigin()
    {
        MaximizeLimits limits = MaximizeLimits.FromWorkArea(
            monitorLeft: -1920,
            monitorTop: 0,
            workLeft: -1920,
            workTop: 0,
            workRight: 0,
            workBottom: 1080);

        Assert.AreEqual(0, limits.PositionX, "The maximized position is relative to the monitor, so a left monitor starts at zero.");
        Assert.AreEqual(0, limits.PositionY, "The maximized position is relative to the monitor, so a top-aligned monitor starts at zero.");
        Assert.AreEqual(1920, limits.SizeX, "The maximized width must be the work area width.");
        Assert.AreEqual(1080, limits.SizeY, "The maximized height must be the work area height.");
    }

    /// <summary>A left or top taskbar must move the maximized window by exactly the taskbar thickness.</summary>
    [TestMethod]
    public void TaskbarsOnTheLeftOrTopShiftTheMaximizedPosition()
    {
        MaximizeLimits leftTaskbar = MaximizeLimits.FromWorkArea(0, 0, 48, 0, 1920, 1080);
        Assert.AreEqual(48, leftTaskbar.PositionX, "A left taskbar must shift the maximized window right by its width.");
        Assert.AreEqual(1920 - 48, leftTaskbar.SizeX, "A left taskbar must shorten the maximized width.");

        MaximizeLimits topTaskbar = MaximizeLimits.FromWorkArea(0, 0, 0, 40, 1920, 1080);
        Assert.AreEqual(40, topTaskbar.PositionY, "A top taskbar must shift the maximized window down by its height.");
        Assert.AreEqual(1080 - 40, topTaskbar.SizeY, "A top taskbar must shorten the maximized height.");
    }

    /// <summary>A work area with no area must be refused, not turned into a zero-sized window.</summary>
    [TestMethod]
    public void AEmptyWorkAreaIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => MaximizeLimits.FromWorkArea(0, 0, 100, 100, 100, 200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => MaximizeLimits.FromWorkArea(0, 0, 100, 100, 200, 100));
    }
}