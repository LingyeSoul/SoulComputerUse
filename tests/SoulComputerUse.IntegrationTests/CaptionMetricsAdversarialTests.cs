using System;
using System.Collections.Generic;
using System.Drawing;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Real-number checks for the self-drawn caption layout: the three caption buttons must always be at
/// least 44 physical pixels wide at every DPI the machine can run at, must never overlap the title or
/// each other, and the close button must stay flush with the right edge. These are the numbers the
/// acceptance criteria name, not a screenshot.
/// </summary>
[TestClass]
public sealed class CaptionMetricsAdversarialTests
{
    /// <summary>The physical pixel target from the spec: a click area must never be smaller than this.</summary>
    private const int MinimumHitTargetPixels = 44;

    private static readonly int[] DpiCases = [96, 120, 144, 168, 192, 240, 288];

    /// <summary>
    /// Every caption button must meet the 44 pixel hit target horizontally at every supported DPI.
    /// The vertical extent is the caption height (32 px at 100%), which the spec itself declares a
    /// passing 46x32 hit area; the two numbers are asserted separately so neither hides the other.
    /// </summary>
    [TestMethod]
    public void EveryCaptionButtonMeetsTheMinimumHitTargetWidthAtEveryDpi()
    {
        foreach (int dpi in DpiCases)
        {
            int width = CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, dpi);
            CaptionMetrics metrics = CaptionMetrics.Create(width, dpi, 100);

            foreach ((string Name, Rectangle Bounds) button in new List<(string Name, Rectangle Bounds)>
            {
                ("minimize", metrics.MinimizeBounds),
                ("maximize", metrics.MaximizeBounds),
                ("close", metrics.CloseBounds),
            })
            {
                Assert.IsGreaterThanOrEqualTo(
                    MinimumHitTargetPixels,
                    button.Bounds.Width,
                    $"The {button.Name} button is only {button.Bounds.Width} px wide at DPI {dpi}, below the {MinimumHitTargetPixels} px target.");

                Assert.AreEqual(
                    metrics.Height,
                    button.Bounds.Height,
                    $"The {button.Name} button must span the full caption height at DPI {dpi}.");
            }
        }
    }

    /// <summary>
    /// The three caption buttons are adjacent with no gap, and close is flush with the right edge. A gap
    /// is not decoration: it is a drag area in the middle of the button strip, so aiming at close and
    /// missing by one slot drags the window instead of closing it.
    /// </summary>
    [TestMethod]
    public void TheThreeCaptionButtonsAreAdjacentWithNoGapAndCloseIsFlushRight()
    {
        foreach (int dpi in DpiCases)
        {
            int width = CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, dpi) + CaptionMetrics.Scale(400, dpi);
            CaptionMetrics metrics = CaptionMetrics.Create(width, dpi, 100);
            int buttonWidth = CaptionMetrics.Scale(CaptionMetrics.ButtonWidth, dpi);

            Assert.AreEqual(width, metrics.CloseBounds.Right, $"The close button must be flush with the right edge at DPI {dpi}.");
            Assert.AreEqual(metrics.CloseBounds.Left, metrics.MaximizeBounds.Right, $"Maximize and close must touch at DPI {dpi}.");
            Assert.AreEqual(metrics.MaximizeBounds.Left, metrics.MinimizeBounds.Right, $"Minimize and maximize must touch at DPI {dpi}.");

            Assert.IsFalse(metrics.MinimizeBounds.IntersectsWith(metrics.MaximizeBounds), $"Minimize and maximize overlap at DPI {dpi}.");
            Assert.IsFalse(metrics.MaximizeBounds.IntersectsWith(metrics.CloseBounds), $"Maximize and close overlap at DPI {dpi}.");

            // Negative: no gap may appear between two neighbouring buttons.
            Assert.AreEqual(0, metrics.MaximizeBounds.Left - metrics.MinimizeBounds.Right, $"A gap appeared between minimize and maximize at DPI {dpi}.");
            Assert.AreEqual(0, metrics.CloseBounds.Left - metrics.MaximizeBounds.Right, $"A gap appeared between maximize and close at DPI {dpi}.");

            Assert.AreEqual(buttonWidth, metrics.MinimizeBounds.Width, $"Every button keeps the same width at DPI {dpi}.");
            Assert.AreEqual(buttonWidth, metrics.MaximizeBounds.Width, $"Every button keeps the same width at DPI {dpi}.");
            Assert.AreEqual(buttonWidth, metrics.CloseBounds.Width, $"Every button keeps the same width at DPI {dpi}.");
        }
    }

    /// <summary>
    /// The title must never be laid out underneath the button strip or the icon. The spec makes this a
    /// hard rule for every DPI and every window width, so the numbers are asserted directly rather
    /// than through a picture.
    /// </summary>
    [TestMethod]
    public void TitleNeverOverlapsTheButtonsOrTheIcon()
    {
        foreach (int dpi in DpiCases)
        {
            int width = CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, dpi) + CaptionMetrics.Scale(600, dpi);
            CaptionMetrics metrics = CaptionMetrics.Create(width, dpi, 100);

            Assert.IsLessThanOrEqualTo(
                metrics.MinimizeBounds.Left,
                metrics.TitleBounds.Right,
                $"The title runs under the button strip at DPI {dpi}.");
            Assert.IsGreaterThanOrEqualTo(
                metrics.IconBounds.Right,
                metrics.TitleBounds.Left,
                $"The title starts before the icon ends at DPI {dpi}.");
        }
    }

    /// <summary>
    /// At the narrowest width the title has no room at all. The spec resolves that case explicitly: a
    /// zero-width title box is not drawn, and the layout must still keep clear of the button strip.
    /// </summary>
    [TestMethod]
    public void AtTheNarrowestWidthTheTitleHasNoRoomAndStillClearsTheButtons()
    {
        foreach (int dpi in DpiCases)
        {
            int width = CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, dpi);
            CaptionMetrics metrics = CaptionMetrics.Create(width, dpi, 100);

            Assert.AreEqual(0, metrics.TitleBounds.Width, $"At the narrowest width the title must have no room at DPI {dpi}.");
            Assert.IsLessThanOrEqualTo(
                metrics.MinimizeBounds.Left,
                metrics.TitleBounds.Right,
                $"Even with no room, the title box must clear the button strip at DPI {dpi}.");
        }
    }

    /// <summary>The title width is capped, so a very wide window does not stretch the title forever.</summary>
    [TestMethod]
    public void TitleWidthIsCappedAtItsPercentageOfTheClientWidth()
    {
        const int ClientWidth = 4000;
        CaptionMetrics metrics = CaptionMetrics.Create(ClientWidth, 96, 100);

        int cap = (ClientWidth * CaptionMetrics.TitleMaxWidthPercent) / 100;
        Assert.IsLessThanOrEqualTo(cap, metrics.TitleBounds.Width, "The title must never exceed its percentage cap.");
    }

    /// <summary>A width that cannot fit the caption must be refused, not laid out overlapping.</summary>
    [TestMethod]
    public void TooNarrowAClientWidthIsRefusedInsteadOfOverlapping()
    {
        int tooNarrow = CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, 96) - 1;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CaptionMetrics.Create(tooNarrow, 96, 100),
            "A client width that cannot fit the caption must be refused.");
    }

    /// <summary>Font sizes follow both DPI and the system text scale factor, and never reach zero.</summary>
    [TestMethod]
    public void FontSizesFollowDpiAndTextScalingWithoutReachingZero()
    {
        CaptionMetrics baseMetrics = CaptionMetrics.Create(1200, 96, 100);
        CaptionMetrics scaled = CaptionMetrics.Create(1200, 192, 150);
        CaptionMetrics minimum = CaptionMetrics.Create(1200, 1, 1);

        Assert.IsGreaterThan(baseMetrics.TitleFontPixels, scaled.TitleFontPixels, "A higher DPI with 150% text must produce a larger title font.");
        Assert.IsGreaterThanOrEqualTo(1, minimum.TitleFontPixels, "A font size must never round down to zero pixels.");
        Assert.IsGreaterThanOrEqualTo(1, minimum.StatusFontPixels, "A font size must never round down to zero pixels.");
        Assert.IsGreaterThanOrEqualTo(1, minimum.StatusDotPixels, "The status dot must never round down to zero pixels.");
    }

    /// <summary>The caption height must track the DPI, so the strip is never a hairline at high scaling.</summary>
    [TestMethod]
    public void CaptionHeightTracksTheDpi()
    {
        foreach (int dpi in DpiCases)
        {
            CaptionMetrics metrics = CaptionMetrics.Create(1200, dpi, 100);
            int expected = CaptionMetrics.Scale(CaptionMetrics.TitleBarHeight, dpi);

            Assert.AreEqual(expected, metrics.Height, $"The caption height must be the scaled title-bar token at DPI {dpi}.");
        }
    }
}