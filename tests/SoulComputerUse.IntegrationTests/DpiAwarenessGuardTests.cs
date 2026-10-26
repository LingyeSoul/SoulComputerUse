using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Guards the measurement environment itself. A test host without DPI awareness reads virtualized
/// coordinates, so a case whose name claims to run "at the machine DPI" would be proving its own
/// assumption instead of the product. These cases fail loudly when that happens.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DpiAwarenessGuardTests
{
    private const int WindowWidth = 900;
    private const int WindowHeight = 600;

    private static SessionStatusVisual StartingStatus => SessionStatusVisuals.Describe(
        SessionState.Starting,
        AuthTier.HelloEnrolled,
        GateStrength.Strong,
        null);

    /// <summary>
    /// The test process must be per-monitor DPI aware. Someone removing the assembly initialisation or
    /// a manifest would otherwise turn every pixel assertion into a comparison of scaled coordinates.
    /// </summary>
    [TestMethod]
    public void TheTestProcessIsPerMonitorDpiAware()
    {
        Assert.AreEqual(
            DpiAwareness.PerMonitorAware,
            DpiProbe.GetThreadAwareness(),
            "A DPI-unaware test host compares virtualized coordinates, so no pixel assertion here would mean anything.");
    }

    /// <summary>
    /// The caption layout must be built from the window's own DPI, not from an assumed 96. This
    /// self-calibrates: it reads the real DPI of the created window and recomputes the expected token
    /// values from it, so it stays honest on a 96, 144 or 192 DPI machine.
    /// </summary>
    [TestMethod]
    public void TheCaptionIsLaidOutFromTheWindowsRealDpi()
    {
        using ProductWindow window = ProductWindow.Create(
            WindowWidth,
            WindowHeight,
            "SoulComputerUse — example-session",
            () => false,
            StartingStatus);

        int windowDpi = DpiProbe.GetWindowDpi(window.Handle);
        CaptionMetrics metrics = window.Metrics;

        Assert.AreEqual(
            windowDpi,
            metrics.Dpi,
            $"The caption was laid out for DPI {metrics.Dpi} but the window reports {windowDpi}.");
        Assert.AreEqual(
            CaptionMetrics.Scale(CaptionMetrics.TitleBarHeight, windowDpi),
            metrics.Height,
            "The caption height must be the title-bar token scaled by the window's own DPI.");
        Assert.AreEqual(
            CaptionMetrics.Scale(CaptionMetrics.ButtonWidth, windowDpi),
            metrics.CloseBounds.Width,
            "The close button width must be the button token scaled by the window's own DPI.");
    }

    /// <summary>
    /// A DPI-aware host and a real window must agree on physical pixels: the window's client width is
    /// the number of pixels the caption is laid out for. Under virtualization these two numbers differ
    /// by the scale factor, which is exactly the trap this guard exists to catch.
    /// </summary>
    [TestMethod]
    public void TheWindowClientWidthMatchesThePixelsTheCaptionUses()
    {
        using ProductWindow window = ProductWindow.Create(
            WindowWidth,
            WindowHeight,
            "SoulComputerUse — example-session",
            () => false,
            StartingStatus);

        Assert.AreEqual(
            WindowWidth,
            window.Metrics.ClientWidth,
            "The caption must be laid out for the physical client width, not a virtualized one.");
        Assert.AreEqual(
            WindowWidth,
            window.Metrics.CloseBounds.Right,
            "The close button must reach the physical right edge of the client area.");
    }
}