using System;
using System.Drawing;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Real-window acceptance for the resize border: the four edges answer HTLEFT / HTRIGHT / HTTOP /
/// HTBOTTOM inside the scaled resize border, while the caption buttons must win where they meet that
/// border. Every assertion reads a hit-test code back from a real window, so resize support cannot
/// exist only in the spec.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ProductWindowResizeAdversarialTests
{
    /// <summary>The resize border width at 100% scaling, in physical pixels, straight from the product.</summary>
    private const int ResizeBorderToken = WindowFrameHitTest.ResizeBorderPixels;

    private const int WindowWidth = 900;
    private const int WindowHeight = 600;

    // Hit-test codes from WinUser.h.
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtBottom = 15;

    private static SessionStatusVisual StartingStatus => SessionStatusVisuals.Describe(
        SessionState.Starting,
        AuthTier.HelloEnrolled,
        GateStrength.Strong,
        null);

    /// <summary>
    /// The left, right, top and bottom borders must answer the matching resize code, otherwise a
    /// borderless window cannot be resized with the mouse at all.
    /// </summary>
    [TestMethod]
    public void EveryEdgeOfTheWindowAnswersTheMatchingResizeHitCode()
    {
        using ProductWindow window = CreateWindow();
        int middleColumn = WindowWidth / 2;
        int middleRow = WindowHeight / 2;

        Assert.AreEqual(HtLeft, window.ProbeHitTestCode(1, middleRow), "The left edge must resize from the left.");
        Assert.AreEqual(HtRight, window.ProbeHitTestCode(WindowWidth - 2, middleRow), "The right edge must resize from the right.");
        Assert.AreEqual(HtTop, window.ProbeHitTestCode(middleColumn, 1), "The top edge must resize from the top.");
        Assert.AreEqual(HtBottom, window.ProbeHitTestCode(middleColumn, WindowHeight - 2), "The bottom edge must resize from the bottom.");
    }

    /// <summary>A point well inside the desktop area must stay a plain client point.</summary>
    [TestMethod]
    public void TheMiddleOfTheDesktopAreaStaysAClientPoint()
    {
        using ProductWindow window = CreateWindow();

        Assert.AreEqual(
            HtClient,
            window.ProbeHitTestCode(WindowWidth / 2, WindowHeight / 2),
            "The middle of the desktop must stay HTCLIENT so the picture receives input.");
    }

    /// <summary>
    /// Where the resize border meets the caption buttons, the buttons must win: the spec puts the three
    /// buttons ahead of the border in the hit-test order, and a swallowed button is unusable.
    /// </summary>
    [TestMethod]
    public void CaptionButtonsWinOverTheResizeBorderWhereTheyMeet()
    {
        using ProductWindow window = CreateWindow();
        Rectangle close = window.Metrics.CloseBounds;
        int closeCentreY = close.Top + (close.Height / 2);

        Assert.AreNotEqual(
            HtRight,
            window.ProbeHitTestCode(WindowWidth - 2, closeCentreY),
            "The close button must not be swallowed by the right resize border.");
    }

    /// <summary>The resize border must scale with the DPI instead of staying a fixed pixel count.</summary>
    [TestMethod]
    public void TheResizeBorderWidensWithDpi()
    {
        using ProductWindow window = CreateWindow();
        int scaledBorder = CaptionMetrics.Scale(ResizeBorderToken, window.Metrics.Dpi);
        int middleRow = WindowHeight / 2;

        Assert.AreEqual(
            HtLeft,
            window.ProbeHitTestCode(scaledBorder - 1, middleRow),
            "Just inside the scaled border must still resize.");
        Assert.AreNotEqual(
            HtLeft,
            window.ProbeHitTestCode(scaledBorder + 1, middleRow),
            "Just outside the scaled border must no longer resize.");
    }

    private static ProductWindow CreateWindow()
    {
        return ProductWindow.Create(
            WindowWidth,
            WindowHeight,
            "SoulComputerUse — example-session",
            () => false,
            StartingStatus);
    }
}