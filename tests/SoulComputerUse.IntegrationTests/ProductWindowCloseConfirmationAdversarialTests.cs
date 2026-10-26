using System;
using System.Drawing;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The negative side of the close-confirmation acceptance item: until the operator confirms, nothing
/// may close. Every close route the spec lists ends at the window procedure's close handling, so this
/// pins that a refused answer leaves the window alive and untouched, and that the operator is asked
/// before anything is destroyed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ProductWindowCloseConfirmationAdversarialTests
{
    private const int WindowWidth = 900;
    private const int WindowHeight = 600;

    // WM_SYSCOMMAND and SC_CLOSE from WinUser.h: what HTCLOSE and Alt+F4 end up sending.
    private const int WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;

    private static SessionStatusVisual ReadyStatus => SessionStatusVisuals.Describe(
        SessionState.Ready,
        AuthTier.HelloEnrolled,
        GateStrength.Strong,
        null);

    /// <summary>
    /// Refusing the confirmation must leave the window alive and unchanged, and must ask exactly once:
    /// a callback that fired twice would mean the close path runs two confirmations.
    /// </summary>
    [TestMethod]
    public void RefusingTheConfirmationLeavesTheWindowUnchanged()
    {
        int confirmations = 0;
        using ProductWindow window = CreateWindow(() =>
        {
            confirmations++;
            return false;
        });

        Rectangle before = ReadWindowRectangle(window.Handle);
        window.SendCloseRequest();

        Assert.AreEqual(1, confirmations, "The close route must ask the operator exactly once.");
        Assert.IsFalse(window.IsDestroyed, "A refused confirmation must leave the window alive.");
        Assert.AreEqual(before, ReadWindowRectangle(window.Handle), "A refused confirmation must not move or resize the window.");
    }

    /// <summary>
    /// The system close command (what HTCLOSE and Alt+F4 send) must also reach the confirmation first,
    /// otherwise the operator could end the session without ever being asked.
    /// </summary>
    [TestMethod]
    public void TheSystemCloseCommandAsksBeforeClosing()
    {
        int confirmations = 0;
        using ProductWindow window = CreateWindow(() =>
        {
            confirmations++;
            return false;
        });

        _ = SendMessageW(window.Handle, WmSysCommand, ScClose, 0);

        Assert.AreEqual(
            1,
            confirmations,
            "Sending SC_CLOSE directly must reach the confirmation, because HTCLOSE travels exactly this route.");
        Assert.IsFalse(window.IsDestroyed, "SC_CLOSE with a refused confirmation must not close the window.");
    }

    /// <summary>
    /// The window must still exist while the operator is being asked, so that cancelling always has a
    /// window to return to.
    /// </summary>
    [TestMethod]
    public void TheWindowIsStillAliveAtTheMomentTheOperatorIsAsked()
    {
        bool aliveWhenAsked = false;
        ProductWindow? window = null;
        window = CreateWindow(() =>
        {
            aliveWhenAsked = window is not null && !window.IsDestroyed;
            return false;
        });

        using (window)
        {
            window.SendCloseRequest();
        }

        Assert.IsTrue(
            aliveWhenAsked,
            "The window must still exist while the operator is being asked, so a cancel can return to it.");
    }

    private static ProductWindow CreateWindow(Func<bool> confirmClose)
    {
        return ProductWindow.Create(
            WindowWidth,
            WindowHeight,
            "SoulComputerUse — example-session",
            confirmClose,
            ReadyStatus);
    }

    private static Rectangle ReadWindowRectangle(nint handle)
    {
        return NativeMethods.GetWindowRect(handle, out NativeRect rect)
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : throw new InvalidOperationException("The window rectangle could not be read.");
    }

    private static nint SendMessageW(nint handle, uint message, nint wParam, nint lParam)
    {
        return NativeMethods.SendMessageW(handle, message, wParam, lParam);
    }
}