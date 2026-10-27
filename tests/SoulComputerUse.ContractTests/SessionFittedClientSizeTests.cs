using System;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the client size that makes the letterboxed desktop picture fill the window: the
/// picture area must keep the session's aspect ratio, because any ratio mismatch is exactly the black
/// bar an operator reads as a broken picture, and the default window aims for a share of the session's
/// native width instead of covering the operator's screen.
/// </summary>
[TestClass]
public sealed class SessionFittedClientSizeTests
{
    private const int Caption = 48;
    private const int Border = 12;

    [TestMethod]
    public void ThePictureAreaKeepsTheSessionRatioSoTheLetterboxIsEmpty()
    {
        // A 16:9 session with a work area whose ratio differs by both the caption and the border.
        (int width, int height) = SessionFittedClientSize.Fit(1920, 1080, Caption, Border, 1600, 1200);

        int pictureWidth = width - (Border * 2);
        int pictureHeight = height - Caption - Border;

        // The picture area must fit the work area and hold the session ratio within one rounding row.
        Assert.IsLessThanOrEqualTo(1600 - (Border * 2), pictureWidth);
        Assert.IsLessThanOrEqualTo(1200 - Caption - Border, pictureHeight);
        int byWidth = pictureWidth * 1080;
        int byHeight = pictureHeight * 1920;
        Assert.IsLessThanOrEqualTo(
            1920,
            Math.Abs(byWidth - byHeight),
            "The picture area's ratio must equal the session's; a mismatch is the black bar the letterbox fills.");
    }

    [TestMethod]
    public void TheDefaultWindowAimsForAShareOfTheSessionWidth()
    {
        (int width, int height) = SessionFittedClientSize.Fit(
            2560, 1600, Caption, Border, 2560, 1540, SessionWindowSizing.DefaultScalePercent);

        Assert.AreEqual(
            (2560 * SessionWindowSizing.DefaultScalePercent / 100) + (Border * 2),
            width,
            "The picture aims for the requested share of the session's native width, so the default window neither covers the screen nor shrinks to a corner.");
        Assert.AreEqual(
            Caption + (int)Math.Round(1024.0 * 1600 / 2560) + Border,
            height,
            "The height follows the session ratio from the aimed width.");
    }

    [TestMethod]
    public void AWorkAreaTooSmallForTheAimedShareCapsTheWindow()
    {
        // The share aims at 40 percent of 2560, but the work area is a fraction of that, so the height
        // limit binds and the window still keeps the session ratio.
        (int width, int height) = SessionFittedClientSize.Fit(
            2560, 1600, Caption, Border, 1000, 608, SessionWindowSizing.DefaultScalePercent);

        Assert.AreEqual(
            608 - Caption - Border,
            height - Caption - Border,
            "The picture must use the full available height, because that is the binding limit here.");
        Assert.AreEqual((2560 * (608 - Caption - Border) / 1600) + (Border * 2), width);
    }

    [TestMethod]
    public void ASessionTallerThanTheWorkAreaScalesByHeight()
    {
        // A portrait session on a landscape work area: the height limit is the binding one.
        (int width, int height) = SessionFittedClientSize.Fit(1080, 1920, Caption, Border, 1920, 1040);

        Assert.AreEqual(
            1040 - Caption - Border,
            height - Caption - Border,
            "The picture must use the full available height, because that is the binding limit for a portrait session.");
        Assert.AreEqual((1080 * (1040 - Caption - Border) / 1920) + (Border * 2), width);
    }

    [TestMethod]
    public void ImpossibleInputsAreRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionFittedClientSize.Fit(0, 1080, Caption, Border, 1600, 1200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionFittedClientSize.Fit(1920, 0, Caption, Border, 1600, 1200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionFittedClientSize.Fit(1920, 1080, 0, Border, 1600, 1200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionFittedClientSize.Fit(1920, 1080, Caption, 0, 1600, 1200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionFittedClientSize.Fit(1920, 1080, Caption, Border, 20, 1200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionFittedClientSize.Fit(1920, 1080, Caption, Border, 1600, 50));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionFittedClientSize.Fit(1920, 1080, Caption, Border, 1600, 1200, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionFittedClientSize.Fit(1920, 1080, Caption, Border, 1600, 1200, 101));
    }
}
