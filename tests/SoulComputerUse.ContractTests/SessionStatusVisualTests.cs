using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for what the caption shows for each session state, its gate tier and its gate
/// strength. The expectations are the state table of the native title bar spec.
/// </summary>
[TestClass]
public sealed class SessionStatusVisualTests
{
    [TestMethod]
    public void EveryRunningStateMapsToItsDotAndText()
    {
        (SessionState State, int Dot, string Text)[] table =
        [
            (SessionState.Starting, 0x9CA0A6, "正在启动…"),
            (SessionState.AwaitingCredentials, 0xF9A825, "等待验证 — 请在桌面窗口手动完成"),
            (SessionState.Connecting, 0x4EA6F0, "正在连接…"),
            (SessionState.Ready, 0x3FB950, ""),
            (SessionState.Disconnected, 0xF85149, "连接已断开"),
            (SessionState.Closing, 0x9CA0A6, "正在结束会话…"),
        ];

        foreach ((SessionState state, int dot, string text) in table)
        {
            SessionStatusVisual visual = SessionStatusVisuals.Describe(
                state,
                AuthTier.HelloEnrolled,
                GateStrength.Strong,
                null);

            Assert.AreEqual(CaptionColor.FromRgb(dot), visual.DotColor, $"The {state} dot colour is wrong.");
            Assert.AreEqual(text, visual.CaptionText, $"The {state} caption text is wrong.");
        }
    }

    [TestMethod]
    public void WaitingForTheOperatorIsTheOnlyStateWithAGateTierLabel()
    {
        SessionStatusVisual waiting = SessionStatusVisuals.Describe(
            SessionState.AwaitingCredentials,
            AuthTier.HelloEnrolled,
            GateStrength.Strong,
            null);

        Assert.AreEqual("请完成系统验证", waiting.GateTierLabel, "The hello tier tells the operator to finish the system check.");

        SessionStatusVisual ready = SessionStatusVisuals.Describe(
            SessionState.Ready,
            AuthTier.HelloEnrolled,
            GateStrength.Strong,
            null);
        Assert.AreEqual("", ready.GateTierLabel, "Only the waiting state shows a gate tier label.");
    }

    [TestMethod]
    public void EveryTierWaitsForTheOperatorBeforeItCanBeReady()
    {
        // The login tier's gate is the child session's own Windows logon screen, so it must be able
        // to render the waiting state too; the caption says the same thing either way and the tier
        // label is reserved for the Hello prompt.
        SessionStatusVisual loginTierWaiting = SessionStatusVisuals.Describe(
            SessionState.AwaitingCredentials,
            AuthTier.SystemCredential,
            GateStrength.Login,
            null);

        Assert.AreEqual("等待验证 — 请在桌面窗口手动完成", loginTierWaiting.CaptionText);
        Assert.AreEqual("", loginTierWaiting.GateTierLabel, "The login tier has no extra label: the logon screen is already in the window.");
    }

    [TestMethod]
    public void TheReadyStateNeverCarriesAGateNoteForAnyTier()
    {
        // Gate strength is a machine-readable field on get_status, not a caption annotation; the v2.6
        // contract removed the weak-gate note entirely.
        SessionStatusVisual loginTierReady = SessionStatusVisuals.Describe(
            SessionState.Ready,
            AuthTier.SystemCredential,
            GateStrength.Login,
            null);
        Assert.AreEqual("", loginTierReady.CaptionText, "The ready state shows only the green dot.");
        Assert.AreEqual("", loginTierReady.GateTierLabel);
    }

    [TestMethod]
    public void BlockedStateTakesItsSentenceFromTheCause()
    {
        (BlockedCause Cause, string Text)[] table =
        [
            (BlockedCause.ProtectedDesktop, "不支持：UAC / 安全桌面，请人工处理"),
            (BlockedCause.NonInteractiveSession, "不支持：非交互会话"),
            (BlockedCause.ChildSessionPolicy, "不支持：本机策略禁止子会话连接"),
        ];

        foreach ((BlockedCause cause, string text) in table)
        {
            SessionStatusVisual visual = SessionStatusVisuals.Describe(
                SessionState.Blocked,
                AuthTier.HelloEnrolled,
                GateStrength.Strong,
                cause);

            Assert.AreEqual(CaptionColor.FromRgb(0xD2A8FF), visual.DotColor, "Every blocked state uses the purple dot.");
            Assert.AreEqual(text, visual.CaptionText, $"The {cause} sentence is wrong.");
        }
    }

    [TestMethod]
    public void BlockedStateRefusesToGuessItsCause()
    {
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(
            () => SessionStatusVisuals.Describe(
                SessionState.Blocked,
                AuthTier.HelloEnrolled,
                GateStrength.Strong,
                null));

        Assert.Contains("cause", error.Message);
    }

    [TestMethod]
    public void ACauseOutsideTheBlockedStateIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => SessionStatusVisuals.Describe(
                SessionState.Ready,
                AuthTier.HelloEnrolled,
                GateStrength.Strong,
                BlockedCause.ProtectedDesktop));
    }

    [TestMethod]
    public void OnlyTheHelloTierShowsTheSystemVerificationLabel()
    {
        Assert.AreEqual(
            "",
            SessionStatusVisuals.Describe(
                SessionState.AwaitingCredentials,
                AuthTier.SystemCredential,
                GateStrength.Login,
                null).GateTierLabel);
    }

    [TestMethod]
    public void TheNoneTierNeverReachesTheCaption()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SessionStatusVisuals.Describe(SessionState.Starting, AuthTier.None, GateStrength.Login, null));
    }

    [TestMethod]
    public void ATierAndStrengthThatContradictEachOtherAreRefused()
    {
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => SessionStatusVisuals.Describe(
                SessionState.Ready,
                AuthTier.HelloEnrolled,
                GateStrength.Login,
                null));

        Assert.Contains("strong", error.Message);
    }

    [TestMethod]
    public void WireNamesRoundTripForEveryTierAndStrength()
    {
        Assert.AreEqual("hello-enrolled", AuthTierNames.ToWireName(AuthTier.HelloEnrolled));
        Assert.AreEqual("system-credential", AuthTierNames.ToWireName(AuthTier.SystemCredential));
        Assert.AreEqual("none", AuthTierNames.ToWireName(AuthTier.None));

        foreach (AuthTier tier in Enum.GetValues<AuthTier>())
        {
            Assert.AreEqual(tier, AuthTierNames.FromWireName(AuthTierNames.ToWireName(tier)));
        }

        Assert.AreEqual("strong", GateStrengthNames.ToWireName(GateStrength.Strong));
        Assert.AreEqual("login", GateStrengthNames.ToWireName(GateStrength.Login));

        foreach (GateStrength strength in Enum.GetValues<GateStrength>())
        {
            Assert.AreEqual(strength, GateStrengthNames.FromWireName(GateStrengthNames.ToWireName(strength)));
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AuthTierNames.FromWireName("hello"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GateStrengthNames.FromWireName("session-only"));
    }

    [TestMethod]
    public void TheGateStrengthOfATierIsFixedByTheContract()
    {
        Assert.AreEqual(GateStrength.Strong, AuthTierRules.StrengthFor(AuthTier.HelloEnrolled));
        Assert.AreEqual(GateStrength.Login, AuthTierRules.StrengthFor(AuthTier.SystemCredential));
        Assert.ThrowsExactly<InvalidOperationException>(() => AuthTierRules.StrengthFor(AuthTier.None));
    }

    [TestMethod]
    public void TheTitleIsProductNamePlusALabel()
    {
        Assert.AreEqual("SoulComputerUse — example-session", SessionStatusVisuals.BuildTitle("example-session"));
        Assert.AreEqual("SoulComputerUse — example-session", SessionStatusVisuals.BuildTitle("  example-session  "));
        Assert.ThrowsExactly<ArgumentException>(() => SessionStatusVisuals.BuildTitle("   "));
    }
}
