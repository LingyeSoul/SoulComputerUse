using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the wire between the remote desktop client and the session state machine: which
/// connected-state number means which session state, when the desktop counts as settled, and the one
/// chain the product must refuse — reaching ready without ever showing the logon step.
/// </summary>
[TestClass]
public sealed class RdpStateMappingTests
{
    [TestMethod]
    public void TheClientStateNumbersMapToTheSessionStatesTheyMean()
    {
        Assert.AreEqual(SessionState.Disconnected, RdpStateMapping.ToSessionState(RdpConnectedState.Idle));
        Assert.AreEqual(SessionState.Connecting, RdpStateMapping.ToSessionState(RdpConnectedState.Connecting));
        Assert.AreEqual(
            SessionState.AwaitingCredentials,
            RdpStateMapping.ToSessionState(RdpConnectedState.AwaitingLogon),
            "State 3 is the client sitting on its logon screen, and that is exactly the step the operator has to perform.");
        Assert.AreEqual(SessionState.Ready, RdpStateMapping.ToSessionState(RdpConnectedState.Connected));
    }

    [TestMethod]
    public void TheRawNumbersAreParsedAndUnknownOnesAreRefused()
    {
        Assert.AreEqual(RdpConnectedState.Connecting, RdpStateMapping.FromRawValue(2));
        Assert.AreEqual(RdpConnectedState.AwaitingLogon, RdpStateMapping.FromRawValue(3));
        Assert.AreEqual(RdpConnectedState.Connected, RdpStateMapping.FromRawValue(1));
        Assert.AreEqual(RdpConnectedState.Idle, RdpStateMapping.FromRawValue(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RdpStateMapping.FromRawValue(7));
    }

    [TestMethod]
    public void ABrightPictureAloneDoesNotMeanTheDesktopIsReady()
    {
        DesktopReadinessFacts brightOnly = new(
            OperatorIsLoggedOn: false,
            HasLogonName: false,
            SecondsSinceLogon: 0.0,
            ConsecutiveBrightFrames: 30);

        Assert.IsFalse(
            RdpStateMapping.IsSettled(brightOnly),
            "The welcome screen is bright too, so brightness alone must never be accepted as ready.");
    }

    [TestMethod]
    public void AllFourReadinessFactsAreNeeded()
    {
        DesktopReadinessFacts all = new(true, true, 5.0, 5);
        Assert.IsTrue(RdpStateMapping.IsSettled(all), "All four facts together mean the desktop is settled.");

        Assert.IsFalse(RdpStateMapping.IsSettled(all with { OperatorIsLoggedOn = false }));
        Assert.IsFalse(RdpStateMapping.IsSettled(all with { HasLogonName = false }));
        Assert.IsFalse(RdpStateMapping.IsSettled(all with { SecondsSinceLogon = 0.5 }));
        Assert.IsFalse(RdpStateMapping.IsSettled(all with { ConsecutiveBrightFrames = 2 }));
    }

    [TestMethod]
    public void TheMeasuredChainThroughTheLogonStepIsAccepted()
    {
        // The verified run: connecting, then the logon screen the operator completed, then ready.
        RdpStateMapping.EnsureLogonWasObserved(
            [SessionState.Starting, SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready]);
    }

    [TestMethod]
    public void ReachingReadyWithoutTheLogonStepIsRefused()
    {
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => RdpStateMapping.EnsureLogonWasObserved(
                [SessionState.Starting, SessionState.Connecting, SessionState.Ready]));

        Assert.Contains("logon", error.Message);
        Assert.Contains("refused", error.Message);
    }

    [TestMethod]
    public void AChainThatNeverReachesReadyIsNotAccusedOfSkippingAnything()
    {
        RdpStateMapping.EnsureLogonWasObserved(
            [SessionState.Starting, SessionState.Connecting, SessionState.Disconnected]);
        RdpStateMapping.EnsureLogonWasObserved([]);
    }

    [TestMethod]
    public void TheReadinessThresholdsAreThePinnedNumbers()
    {
        // Guarded against a silent change of the settled rule: the values are compared through the
        // readiness decision, which is the only thing that actually consumes them.
        DesktopReadinessFacts justBelow = new(true, true, RdpStateMapping.SettleSecondsAfterLogon - 0.1, RdpStateMapping.SettleBrightFramesRequired);
        DesktopReadinessFacts exactly = new(true, true, RdpStateMapping.SettleSecondsAfterLogon, RdpStateMapping.SettleBrightFramesRequired);
        DesktopReadinessFacts oneFrameShort = new(true, true, RdpStateMapping.SettleSecondsAfterLogon, RdpStateMapping.SettleBrightFramesRequired - 1);

        Assert.IsFalse(RdpStateMapping.IsSettled(justBelow), "Just below the settle delay is not settled yet.");
        Assert.IsTrue(RdpStateMapping.IsSettled(exactly), "Reaching the settle delay and the frame count is settled.");
        Assert.IsFalse(RdpStateMapping.IsSettled(oneFrameShort), "One frame short of the streak is not settled.");
    }
}
