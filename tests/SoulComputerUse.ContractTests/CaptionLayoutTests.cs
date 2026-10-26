using System;
using System.Drawing;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the caption layout and its hit zones. Every number comes from the token table in
/// <c>docs/native-titlebar-spec.md</c>, so a change of a token has to be a change of this test too.
/// </summary>
[TestClass]
public sealed class CaptionLayoutTests
{
    private const int BaselineDpi = 96;
    private const int WideClientWidth = 1000;

    // HTCAPTION and HTCLIENT from WinUser.h, spelled out because the Windows type only publishes the
    // codes the caption reacts to.
    private const int CaptionHitCode = 2;
    private const int ClientHitCode = 1;

    [TestMethod]
    public void LayoutAtBaselineDpiMatchesTheTokenTable()
    {
        CaptionMetrics metrics = CaptionMetrics.Create(WideClientWidth, BaselineDpi, 100);

        Assert.AreEqual(CaptionMetrics.TitleBarHeight, metrics.Height, "The caption strip is 32 px tall at 96 DPI.");
        Assert.AreEqual(new Rectangle(862, 0, 46, 32), metrics.MinimizeBounds, "Minimize is the first of the three adjacent buttons.");
        Assert.AreEqual(new Rectangle(908, 0, 46, 32), metrics.MaximizeBounds, "Maximize follows minimize with no gap.");
        Assert.AreEqual(new Rectangle(954, 0, 46, 32), metrics.CloseBounds, "Close touches maximize and stays flush with the right edge.");
        Assert.AreEqual(new Rectangle(12, 8, 16, 16), metrics.IconBounds, "The icon is 16 px, inset 12 px and vertically centred.");
        Assert.AreEqual(36, metrics.TitleBounds.Left, "The title starts after the icon plus the 8 px gap.");
        Assert.AreEqual(400, metrics.TitleBounds.Width, "The title is capped at 40 percent of the client width.");
        Assert.AreEqual(854, metrics.StatusRightLimit, "The status group ends one 8 px gap before the buttons.");
        Assert.AreEqual(12, metrics.TitleFontPixels, "The title font is 12 px at 96 DPI and 100 percent text scale.");
        Assert.AreEqual(12, metrics.StatusFontPixels, "The status font is 12 px at 96 DPI and 100 percent text scale.");
        Assert.AreEqual(8, metrics.StatusDotPixels, "The status dot is 8 px.");
    }

    [TestMethod]
    public void LayoutScalesLinearlyWithTheDpi()
    {
        CaptionMetrics metrics = CaptionMetrics.Create(WideClientWidth, 144, 100);

        Assert.AreEqual(48, metrics.Height, "32 px at 96 DPI becomes 48 px at 144 DPI.");
        Assert.AreEqual(69, metrics.MinimizeBounds.Width, "46 px at 96 DPI becomes 69 px at 144 DPI.");
        Assert.AreEqual(24, metrics.IconBounds.Width, "16 px at 96 DPI becomes 24 px at 144 DPI.");
        Assert.AreEqual(12, metrics.IconBounds.Top, "The icon stays vertically centred in the taller strip.");
        Assert.AreEqual(WideClientWidth, metrics.CloseBounds.Right, "Close stays flush with the right edge at any DPI.");
    }

    [TestMethod]
    public void TheThreeCaptionButtonsAreAdjacentWithNoGap()
    {
        CaptionMetrics metrics = CaptionMetrics.Create(WideClientWidth, BaselineDpi, 100);

        Assert.AreEqual(metrics.MinimizeBounds.Right, metrics.MaximizeBounds.Left, "Minimize and maximize must touch.");
        Assert.AreEqual(
            metrics.MaximizeBounds.Right,
            metrics.CloseBounds.Left,
            "Maximize and close must touch: a gap there is a drag area, and aiming one notch off would drag the window instead of closing it.");
        Assert.AreEqual(metrics.ClientWidth, metrics.CloseBounds.Right, "Close stays flush with the right edge.");
    }

    [TestMethod]
    public void EveryButtonWinsAtLeastTheMinimumHitWidth()
    {
        foreach (int dpi in (int[])[96, 120, 144, 192, 240])
        {
            CaptionMetrics metrics = CaptionMetrics.Create(WideClientWidth, dpi, 100);
            foreach (CaptionPart part in (CaptionPart[])[CaptionPart.Minimize, CaptionPart.Maximize, CaptionPart.Close])
            {
                Rectangle bounds = CaptionHitTest.BoundsOf(metrics, part);
                Assert.IsGreaterThanOrEqualTo(
                    44,
                    bounds.Width,
                    $"The {part} hit zone is {bounds.Width} px wide at DPI {dpi}, which is below the 44 px minimum.");
            }
        }
    }

    [TestMethod]
    public void ButtonHeightIsTheFullCaptionStripAndShrinksUnderTheMinimumHitHeightAtLowDpi()
    {
        // The token table asks for a 46x32 button and also for a 44 px minimum hit target; at 96 DPI
        // the width passes and the height cannot, because the strip itself is only 32 px tall. This
        // test states the measured numbers instead of pretending the two lines agree.
        CaptionMetrics atNinetySix = CaptionMetrics.Create(WideClientWidth, 96, 100);
        CaptionMetrics atOneFortyFour = CaptionMetrics.Create(WideClientWidth, 144, 100);

        Assert.AreEqual(atNinetySix.Height, atNinetySix.CloseBounds.Height, "A button is as tall as the strip.");
        Assert.AreEqual(32, atNinetySix.CloseBounds.Height, "At 96 DPI the button hit zone is 32 px tall.");
        Assert.AreEqual(48, atOneFortyFour.CloseBounds.Height, "At 144 DPI the same button is 48 px tall and passes.");
    }

    [TestMethod]
    public void TextScaleFactorAffectsFontsButNotLayout()
    {
        CaptionMetrics scaled = CaptionMetrics.Create(WideClientWidth, BaselineDpi, 150);

        Assert.AreEqual(18, scaled.TitleFontPixels, "12 px at 150 percent text scale is 18 px.");
        Assert.AreEqual(18, scaled.StatusFontPixels, "12 px at 150 percent text scale is 18 px.");
        Assert.AreEqual(32, scaled.Height, "The text scale factor must not change the strip height.");
        Assert.AreEqual(46, scaled.CloseBounds.Width, "The text scale factor must not change the button width.");
    }

    [TestMethod]
    public void TitleWidthIsClampedToTheSpaceLeftOfTheButtons()
    {
        CaptionMetrics metrics = CaptionMetrics.Create(CaptionMetrics.MinimumClientWidth, BaselineDpi, 100);

        Assert.AreEqual(0, metrics.TitleBounds.Width, "At the narrowest legal width the title keeps no room, and it never runs under the buttons.");
        Assert.AreEqual(36, metrics.TitleBounds.Left, "The title still starts after the icon.");
    }

    [TestMethod]
    public void LayoutThrowsWhenTheClientIsTooNarrowForTheCaption()
    {
        ArgumentOutOfRangeException error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CaptionMetrics.Create(CaptionMetrics.MinimumClientWidth - 1, BaselineDpi, 100));

        Assert.Contains("overlap", error.Message);
    }

    [TestMethod]
    public void LayoutRejectsNonPositiveDpiAndTextScale()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CaptionMetrics.Create(WideClientWidth, 0, 100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CaptionMetrics.Create(WideClientWidth, BaselineDpi, 0));
    }

    [TestMethod]
    public void TokenScalingRoundsAwayFromZero()
    {
        Assert.AreEqual(32, CaptionMetrics.Scale(32, 96));
        Assert.AreEqual(48, CaptionMetrics.Scale(32, 144));
        Assert.AreEqual(2, CaptionMetrics.Scale(1, 144), "1.5 px rounds to 2, so a hairline never disappears.");
        Assert.AreEqual(69, CaptionMetrics.Scale(46, 144));
    }

    [TestMethod]
    public void VisibleTokensNeverRoundDownToNothingEvenAtDegenerateDpi()
    {
        // The floor is only for tokens that must be seen: a status dot, a border, a hairline. A spacing
        // token keeps the plain scaling so a zero gap stays zero.
        Assert.AreEqual(1, CaptionMetrics.ScaleVisible(8, 1), "A status dot at a degenerate DPI must still be one pixel.");
        Assert.AreEqual(1, CaptionMetrics.ScaleVisible(1, 1), "A hairline must still be one pixel.");
        Assert.AreEqual(1, CaptionMetrics.ScaleVisible(1, 96));
        Assert.AreEqual(69, CaptionMetrics.ScaleVisible(46, 144), "A visible token that already rounds up is unchanged.");
        Assert.AreEqual(0, CaptionMetrics.Scale(CaptionMetrics.ContentGap, 1), "A spacing token is not floored.");

        CaptionMetrics degenerate = CaptionMetrics.Create(1200, 1, 1);
        Assert.AreEqual(1, degenerate.StatusDotPixels, "The caption must keep its status dot visible at any DPI.");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CaptionMetrics.ScaleVisible(0, 96));
    }

    [TestMethod]
    public void HitZonesCoverTheWholeCaptionStripWithHalfOpenRectangles()
    {
        CaptionMetrics metrics = CaptionMetrics.Create(WideClientWidth, BaselineDpi, 100);
        int middleY = metrics.Height / 2;

        Assert.AreEqual(CaptionPart.Minimize, CaptionHitTest.Resolve(metrics, 862, middleY), "The left edge of minimize is inside it.");
        Assert.AreEqual(CaptionPart.Caption, CaptionHitTest.Resolve(metrics, 861, middleY), "One pixel left of minimize belongs to the caption.");
        Assert.AreEqual(CaptionPart.Minimize, CaptionHitTest.Resolve(metrics, 907, middleY), "The last pixel of minimize is minimize, without a gap.");
        Assert.AreEqual(CaptionPart.Maximize, CaptionHitTest.Resolve(metrics, 908, middleY), "The first pixel of maximize is maximize: the buttons touch.");
        Assert.AreEqual(CaptionPart.Maximize, CaptionHitTest.Resolve(metrics, 953, middleY), "The last pixel of maximize is maximize, without a gap.");
        Assert.AreEqual(CaptionPart.Close, CaptionHitTest.Resolve(metrics, 954, middleY), "The first pixel of close is inside close.");
        Assert.AreEqual(CaptionPart.Close, CaptionHitTest.Resolve(metrics, 999, middleY), "The last pixel of the window is inside close.");
    }

    [TestMethod]
    public void PointsOutsideTheCaptionAreNotCaptionParts()
    {
        CaptionMetrics metrics = CaptionMetrics.Create(WideClientWidth, BaselineDpi, 100);

        Assert.AreEqual(CaptionPart.None, CaptionHitTest.Resolve(metrics, 500, 32), "The first pixel below the strip is the desktop picture.");
        Assert.AreEqual(CaptionPart.None, CaptionHitTest.Resolve(metrics, 500, -1), "Above the strip is not the caption.");
        Assert.AreEqual(CaptionPart.None, CaptionHitTest.Resolve(metrics, -1, 10), "Left of the window is not the caption.");
        Assert.AreEqual(CaptionPart.None, CaptionHitTest.Resolve(metrics, WideClientWidth, 10), "Right of the window is not the caption.");
        Assert.AreEqual(CaptionPart.Caption, CaptionHitTest.Resolve(metrics, 200, 31), "The last pixel row of the strip is still the caption.");
    }

    [TestMethod]
    public void CaptionAnswersHtCaptionAndButtonsAnswerTheirSystemCodes()
    {
        Assert.AreEqual(CaptionHitCode, CaptionHitTest.ToHitTestCode(CaptionPart.Caption), "The strip stays HTCAPTION so dragging and double-click maximize keep working.");
        Assert.AreEqual(8, CaptionHitTest.ToHitTestCode(CaptionPart.Minimize), "Minimize answers HTMINBUTTON so the system performs the action.");
        Assert.AreEqual(9, CaptionHitTest.ToHitTestCode(CaptionPart.Maximize), "Maximize answers HTMAXBUTTON so the system performs the action.");
        Assert.AreEqual(20, CaptionHitTest.ToHitTestCode(CaptionPart.Close), "Close answers HTCLOSE so the system sends WM_CLOSE like every other close path.");
        Assert.AreEqual(ClientHitCode, CaptionHitTest.ToHitTestCode(CaptionPart.None), "The desktop picture is an ordinary client area.");
    }

    [TestMethod]
    public void SystemHitCodesMapBackToTheThreeButtons()
    {
        Assert.AreEqual(CaptionButtonKind.Minimize, CaptionHitTest.ButtonFromHitTestCode(8));
        Assert.AreEqual(CaptionButtonKind.Maximize, CaptionHitTest.ButtonFromHitTestCode(9));
        Assert.AreEqual(CaptionButtonKind.Close, CaptionHitTest.ButtonFromHitTestCode(20));
        Assert.IsNull(CaptionHitTest.ButtonFromHitTestCode(ClientHitCode));
        Assert.IsNull(CaptionHitTest.ButtonFromHitTestCode(CaptionHitCode));
        Assert.AreEqual(CaptionPart.Close, CaptionHitTest.ToCaptionPart(CaptionButtonKind.Close));
        Assert.AreEqual(CaptionPart.Minimize, CaptionHitTest.ToCaptionPart(CaptionButtonKind.Minimize));
        Assert.AreEqual(CaptionPart.Maximize, CaptionHitTest.ToCaptionPart(CaptionButtonKind.Maximize));
    }

    [TestMethod]
    public void ButtonKindMappingOnlyAnswersForTheThreeButtons()
    {
        Assert.AreEqual(CaptionButtonKind.Minimize, CaptionHitTest.ToButtonKind(CaptionPart.Minimize));
        Assert.AreEqual(CaptionButtonKind.Maximize, CaptionHitTest.ToButtonKind(CaptionPart.Maximize));
        Assert.AreEqual(CaptionButtonKind.Close, CaptionHitTest.ToButtonKind(CaptionPart.Close));
        Assert.IsNull(CaptionHitTest.ToButtonKind(CaptionPart.Caption));
        Assert.IsNull(CaptionHitTest.ToButtonKind(CaptionPart.None));
        Assert.IsTrue(CaptionHitTest.IsButton(CaptionPart.Close));
        Assert.IsFalse(CaptionHitTest.IsButton(CaptionPart.Caption));
    }
}
