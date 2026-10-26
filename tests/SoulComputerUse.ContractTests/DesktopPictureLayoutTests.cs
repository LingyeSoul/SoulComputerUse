using System;
using System.Drawing;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the letterbox geometry: the session picture is fitted and centred, the leftover
/// bars stay pure black, and no arrangement may push the picture outside the client area.
/// </summary>
[TestClass]
public sealed class DesktopPictureLayoutTests
{
    [TestMethod]
    public void AMatchingAspectRatioFillsTheWholeClientArea()
    {
        Rectangle fitted = DesktopPictureLayout.Fit(1920, 1080, 1920, 1080);
        Assert.AreEqual(new Rectangle(0, 0, 1920, 1080), fitted);

        Rectangle scaledDown = DesktopPictureLayout.Fit(960, 540, 1920, 1080);
        Assert.AreEqual(
            new Rectangle(0, 0, 960, 540),
            scaledDown,
            "Same aspect ratio means the picture fills everything, scaled.");
    }

    [TestMethod]
    public void ATooWideClientAreaGetsBlackBarsOnBothSides()
    {
        Rectangle fitted = DesktopPictureLayout.Fit(1600, 900, 1024, 768);

        Assert.AreEqual(1200, fitted.Width, "A 4:3 picture in a 16:9 area is limited by height: 900 x 4 / 3 = 1200.");
        Assert.AreEqual(900, fitted.Height);
        Assert.AreEqual(200, fitted.Left, "The left bar is half of the leftover 400 pixels.");
        Assert.AreEqual(200, 1600 - fitted.Right, "The two bars must be the same width, so the picture is centred.");
        Assert.AreEqual(0, fitted.Top, "A height-fitted picture touches the top and bottom edges.");
    }

    [TestMethod]
    public void ATooTallClientAreaGetsBlackBarsAboveAndBelow()
    {
        Rectangle fitted = DesktopPictureLayout.Fit(1280, 1024, 1920, 1080);

        Assert.AreEqual(1280, fitted.Width, "A 16:9 picture in a 5:4 area is limited by width.");
        Assert.AreEqual(720, fitted.Height, "1280 x 9 / 16 = 720.");
        Assert.AreEqual(152, fitted.Top, "The top bar is half of the leftover 304 pixels.");
        Assert.AreEqual(152, 1024 - fitted.Bottom, "The two bars must have the same height.");
    }

    [TestMethod]
    public void TheBarsAreNeverOneSided()
    {
        // An odd leftover leaves one extra pixel; it must not all land on one side beyond that.
        Rectangle fitted = DesktopPictureLayout.Fit(1001, 500, 1000, 500);
        int left = fitted.Left;
        int right = 1001 - fitted.Right;

        Assert.IsLessThanOrEqualTo(
            1,
            Math.Abs(left - right),
            $"The bars are {left} and {right} pixels wide, so the picture is off-centre.");
    }

    [TestMethod]
    public void ThePictureNeverEscapesTheClientArea()
    {
        (int ClientWidth, int ClientHeight, int SessionWidth, int SessionHeight)[] cases =
        [
            (100, 100, 1920, 1080),
            (100, 100, 1080, 1920),
            (1000, 3, 16, 9),
            (3, 1000, 16, 9),
            (800, 600, 800, 601),
            (1920, 1080, 1, 1),
        ];

        foreach ((int clientWidth, int clientHeight, int sessionWidth, int sessionHeight) in cases)
        {
            Rectangle fitted = DesktopPictureLayout.Fit(clientWidth, clientHeight, sessionWidth, sessionHeight);
            Assert.IsTrue(
                fitted.Left >= 0 && fitted.Top >= 0 && fitted.Right <= clientWidth && fitted.Bottom <= clientHeight,
                $"The picture {fitted} escapes the client area {clientWidth}x{clientHeight}.");
            Assert.IsTrue(fitted.Width > 0 && fitted.Height > 0, "The picture must never be laid out with zero size.");
        }
    }

    [TestMethod]
    public void ImpossibleGeometryIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DesktopPictureLayout.Fit(0, 100, 100, 100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DesktopPictureLayout.Fit(100, 0, 100, 100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DesktopPictureLayout.Fit(100, 100, 0, 100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DesktopPictureLayout.Fit(100, 100, 100, 0));
    }
}
