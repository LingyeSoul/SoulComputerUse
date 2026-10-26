using System.Drawing;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The M0 Snap gate, automated half: a real window is created and real
/// <c>WM_NCHITTEST</c> messages are answered by the real window procedure. The visual
/// half (Windows actually opening the Snap Layouts flyout) is checked by a human on the
/// host machine and is not asserted here.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SnapHitTestSpikeWindowTests
{
    // HTCAPTION from WinUser.h. The production type only publishes the hit codes this spike
    // reacts to, so the untouched caption path is asserted here.
    private const int CaptionHitTestCode = 2;

    [TestMethod]
    public void WindowAnswersMaxButtonHitCodeForBothHotZones()
    {
        using SnapHitTestSpikeWindow window = SnapHitTestSpikeWindow.Create(960, 600);

        Rectangle titleBarZoneInScreen = Offset(window.TitleBarHotZone, window.WindowRectangleInScreen.Location);
        Rectangle clientZoneInScreen = Offset(window.ClientHotZone, window.ClientOriginInScreen);

        Assert.AreEqual(
            SnapHitTestSpikeWindow.MaxButtonHitTestCode,
            window.ProbeCentreOfMaximizeZone(titleBarZoneInScreen),
            "The title-bar hot zone must answer HTMAXBUTTON.");
        Assert.AreEqual(
            SnapHitTestSpikeWindow.MaxButtonHitTestCode,
            window.ProbeCentreOfMaximizeZone(clientZoneInScreen),
            "The client-area hot zone must answer HTMAXBUTTON.");
    }

    [TestMethod]
    public void WindowLeavesOrdinaryClientPointsToTheDefaultHandler()
    {
        using SnapHitTestSpikeWindow window = SnapHitTestSpikeWindow.Create(960, 600);

        Point clientOrigin = window.ClientOriginInScreen;
        int hitCode = window.ProbeHitTest(clientOrigin.X + 600, clientOrigin.Y + 300);

        Assert.AreEqual(
            SnapHitTestSpikeWindow.ClientHitTestCode,
            hitCode,
            "A point outside both hot zones must fall through to the default window procedure.");
    }

    [TestMethod]
    public void WindowLeavesTitleBarPointsOutsideTheHotZoneToTheCaption()
    {
        using SnapHitTestSpikeWindow window = SnapHitTestSpikeWindow.Create(960, 600);

        Rectangle windowRectangle = window.WindowRectangleInScreen;
        int captionX = windowRectangle.Left + (windowRectangle.Width / 2);
        int captionY = windowRectangle.Top + (window.TitleBarHotZone.Height / 2);

        Assert.AreEqual(
            CaptionHitTestCode,
            window.ProbeHitTest(captionX, captionY),
            "A title-bar point outside the hot zone must stay HTCAPTION, otherwise a self-drawn caption swallows the whole title bar and the window stops dragging.");
    }

    private static Rectangle Offset(Rectangle zone, Point origin)
    {
        return new Rectangle(origin.X + zone.Left, origin.Y + zone.Top, zone.Width, zone.Height);
    }
}