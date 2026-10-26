using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the presenter that keeps the caption in step with the session state machine. No
/// window is involved: the point is that the caption content is produced from the machine's states and
/// that an illegal transition is refused instead of being displayed.
/// </summary>
[TestClass]
public sealed class SessionStatusPresenterTests
{
    [TestMethod]
    public void ASessionStartsInTheStartingStateWithItsVisual()
    {
        SessionStatusPresenter presenter = new(AuthTier.HelloEnrolled, GateStrength.Strong);

        Assert.AreEqual(SessionState.Starting, presenter.CurrentState);
        Assert.AreEqual("正在启动…", presenter.Current.CaptionText, "The caption shows the starting sentence, not a guess.");
    }

    [TestMethod]
    public void TheHappyPathProducesTheCaptionOfEveryStateItPasses()
    {
        SessionStatusPresenter presenter = new(AuthTier.HelloEnrolled, GateStrength.Strong);

        Assert.AreEqual("正在连接…", presenter.MoveTo(SessionState.Connecting, null).CaptionText);
        Assert.AreEqual(SessionState.Connecting, presenter.CurrentState);
        Assert.AreEqual("", presenter.MoveTo(SessionState.Ready, null).CaptionText, "The ready state shows only the green dot.");
        Assert.AreEqual(SessionState.Ready, presenter.CurrentState);
        Assert.AreEqual("正在结束会话…", presenter.MoveTo(SessionState.Closing, null).CaptionText);
        Assert.AreEqual(SessionState.Closing, presenter.CurrentState);
    }

    [TestMethod]
    public void ATransitionTheContractForbidsIsRefusedInsteadOfDisplayed()
    {
        SessionStatusPresenter presenter = new(AuthTier.HelloEnrolled, GateStrength.Strong);

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => presenter.MoveTo(SessionState.Ready, null));

        Assert.Contains("starting", error.Message);
        Assert.Contains("ready", error.Message);
        Assert.AreEqual(SessionState.Starting, presenter.CurrentState, "A refused transition must leave the state untouched.");
    }

    [TestMethod]
    public void TheBlockedStateTakesItsCauseAndKeepsItThroughTheVisual()
    {
        SessionStatusPresenter presenter = new(AuthTier.HelloEnrolled, GateStrength.Strong);
        presenter.MoveTo(SessionState.Connecting, null);

        SessionStatusVisual blocked = presenter.MoveTo(SessionState.Blocked, BlockedCause.ProtectedDesktop);

        Assert.AreEqual("不支持：UAC / 安全桌面，请人工处理", blocked.CaptionText);
        Assert.AreEqual(SessionState.Blocked, presenter.CurrentState);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => presenter.MoveTo(SessionState.Blocked, null),
            "Staying in the blocked state is not a legal contract transition, so a cause cannot be silently dropped.");
        Assert.AreEqual(SessionState.Blocked, presenter.CurrentState, "A refused transition must leave the state untouched.");
    }

    [TestMethod]
    public void ALoginTierSessionDisclosesNothingExtraWhenReady()
    {
        SessionStatusPresenter presenter = new(AuthTier.SystemCredential, GateStrength.Login);
        presenter.MoveTo(SessionState.AwaitingCredentials, null);
        presenter.MoveTo(SessionState.Connecting, null);
        SessionStatusVisual ready = presenter.MoveTo(SessionState.Ready, null);

        Assert.AreEqual("", ready.GateTierLabel, "The login tier is a real gate, so nothing is disclosed on the caption.");
        Assert.AreEqual("", ready.CaptionText, "The ready state shows only the green dot.");
    }

    [TestMethod]
    public void ALoginTierSessionMayWaitForTheLogonScreen()
    {
        SessionStatusPresenter presenter = new(AuthTier.SystemCredential, GateStrength.Login);

        SessionStatusVisual waiting = presenter.MoveTo(SessionState.AwaitingCredentials, null);

        Assert.AreEqual(SessionState.AwaitingCredentials, presenter.CurrentState);
        Assert.AreEqual("等待验证 — 请在桌面窗口手动完成", waiting.CaptionText);
    }

    [TestMethod]
    public void ATierThatRefusesToStartCannotBuildAPresenter()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new SessionStatusPresenter(AuthTier.None, GateStrength.Login));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new SessionStatusPresenter(AuthTier.HelloEnrolled, GateStrength.Login),
            "A strong tier must not be presented with a login gate.");
    }
}
