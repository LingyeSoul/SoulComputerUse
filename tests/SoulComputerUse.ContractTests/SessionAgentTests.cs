using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.SessionAgent;
using SoulComputerUse.Windows.Desktop;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the child-session agent's command line, claim gate, interactive-desktop check
/// and desktop session client. No test starts the agent process or injects real input.
/// </summary>
[TestClass]
public sealed class SessionAgentTests
{
    private const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";

    private const string Nonce = "0123456789ABCDEF0123456789ABCDEF";

    [TestMethod]
    public void ParseAcceptsTheCompleteBootstrapCommandLine()
    {
        AgentOptions options = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
            "--auth-tier",
            "system-credential",
            "--gate-strength",
            "login",
        ]);

        Assert.AreEqual(SessionId, options.SessionId);
        Assert.AreEqual(Nonce, options.Nonce);
        Assert.AreEqual(AuthTier.SystemCredential, options.AuthTier);
        Assert.AreEqual(GateStrength.Login, options.GateStrength);
    }

    [TestMethod]
    public void ParseAcceptsTheHelloTierWithItsStrongGate()
    {
        AgentOptions options = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
            "--auth-tier",
            "hello-enrolled",
            "--gate-strength",
            "strong",
        ]);

        Assert.AreEqual(AuthTier.HelloEnrolled, options.AuthTier);
        Assert.AreEqual(GateStrength.Strong, options.GateStrength);
    }

    [TestMethod]
    public void ParseAcceptsACommandLineWithoutTheDeclaredGateValues()
    {
        AgentOptions options = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
        ]);

        Assert.AreEqual(SessionId, options.SessionId);
        Assert.AreEqual(Nonce, options.Nonce);
        Assert.IsNull(
            options.AuthTier,
            "The tier is detected by the agent, so an undeclared tier must stay undeclared instead of defaulting to one.");
        Assert.IsNull(
            options.GateStrength,
            "The strength is pinned to the detected tier, so an undeclared strength must stay undeclared.");
    }

    [TestMethod]
    public void ParseAcceptsOneDeclaredGateValueWithoutTheOther()
    {
        AgentOptions tierOnly = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
            "--auth-tier",
            "hello-enrolled",
        ]);
        AgentOptions strengthOnly = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
            "--gate-strength",
            "login",
        ]);

        Assert.AreEqual(AuthTier.HelloEnrolled, tierOnly.AuthTier);
        Assert.IsNull(tierOnly.GateStrength);
        Assert.IsNull(strengthOnly.AuthTier);
        Assert.AreEqual(GateStrength.Login, strengthOnly.GateStrength);
    }

    [TestMethod]
    public void ParseRejectsMissingUnknownAndDuplicatedArguments()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AgentCommandLine.Parse([]));
        Assert.ThrowsExactly<ArgumentException>(() => AgentCommandLine.Parse(["--scu-bootstrap"]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--session",
                SessionId,
                "--nonce",
                Nonce,
                "--auth-tier",
                "system-credential",
                "--gate-strength",
                "login",
                "--unknown",
            ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--scu-bootstrap",
                "--session",
                SessionId,
                "--nonce",
                Nonce,
            ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--session",
                "--nonce",
                Nonce,
                "--auth-tier",
                "system-credential",
                "--gate-strength",
                "login",
            ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--session",
                SessionId,
                "--nonce",
                Nonce,
                "--auth-tier",
                "system-credential",
                "--auth-tier",
                "hello-enrolled",
            ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--session",
                SessionId,
                "--nonce",
                Nonce,
                "--gate-strength",
            ]));
    }

    [TestMethod]
    public void ParseRejectsANonHexNonceWithoutEchoingIt()
    {
        const string SecretLookingNonce = "SECRETSECRETSECRETSECRETSECRET12";

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--session",
                SessionId,
                "--nonce",
                SecretLookingNonce,
                "--auth-tier",
                "system-credential",
                "--gate-strength",
                "login",
            ]));

        Assert.IsFalse(error.Message.Contains(SecretLookingNonce, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseRejectsTheNoneTierAndMismatchedDeclaredValues()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--session",
                SessionId,
                "--nonce",
                Nonce,
                "--auth-tier",
                "none",
                "--gate-strength",
                "login",
            ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--session",
                SessionId,
                "--nonce",
                Nonce,
                "--auth-tier",
                "hello-enrolled",
                "--gate-strength",
                "login",
            ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentCommandLine.Parse(
            [
                "--scu-bootstrap",
                "--session",
                SessionId,
                "--nonce",
                Nonce,
                "--auth-tier",
                "system-credential",
                "--gate-strength",
                "strong",
            ]));
    }

    [TestMethod]
    public void AnUndeclaredGateNeverContradictsTheDetectedTier()
    {
        AgentOptions options = ParseMinimal();
        AgentAuthTier detected = AgentAuthTierDetector.Decide(false, true, false);

        AgentCommandLine.EnsureDeclarationMatches(options, detected);
    }

    [TestMethod]
    public void ADeclaredTierThatMatchesTheDetectionIsAccepted()
    {
        AgentOptions options = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
            "--auth-tier",
            "hello-enrolled",
            "--gate-strength",
            "strong",
        ]);
        AgentAuthTier detected = AgentAuthTierDetector.Decide(true, false, false);

        AgentCommandLine.EnsureDeclarationMatches(options, detected);
    }

    [TestMethod]
    public void ADeclaredTierThatContradictsTheDetectionIsRefusedWithoutEchoingValues()
    {
        AgentOptions claimsHello = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
            "--auth-tier",
            "hello-enrolled",
            "--gate-strength",
            "strong",
        ]);
        AgentAuthTier detectedLogin = AgentAuthTierDetector.Decide(false, true, false);

        AgentGateDeclarationException error = Assert.ThrowsExactly<AgentGateDeclarationException>(
            () => AgentCommandLine.EnsureDeclarationMatches(claimsHello, detectedLogin));

        Assert.IsFalse(
            error.Message.Contains(Nonce, StringComparison.Ordinal),
            "The refusal must never echo the binding nonce.");
        Assert.IsFalse(
            error.Message.Contains("hello-enrolled", StringComparison.Ordinal)
                || error.Message.Contains("system-credential", StringComparison.Ordinal),
            "The refusal must describe the contradiction without repeating a tier value.");
    }

    [TestMethod]
    public void ADeclaredStrengthThatContradictsTheDetectedTierIsRefused()
    {
        AgentOptions claimsLogin = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
            "--gate-strength",
            "login",
        ]);
        AgentAuthTier detectedHello = AgentAuthTierDetector.Decide(true, true, false);

        Assert.ThrowsExactly<AgentGateDeclarationException>(
            () => AgentCommandLine.EnsureDeclarationMatches(claimsLogin, detectedHello));
    }

    [TestMethod]
    public void ADeclaredStrengthContradictsTheRefusalTier()
    {
        AgentOptions claimsLogin = AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
            "--gate-strength",
            "login",
        ]);
        AgentAuthTier refused = AgentAuthTierDetector.Decide(false, false, false);

        Assert.ThrowsExactly<AgentGateDeclarationException>(
            () => AgentCommandLine.EnsureDeclarationMatches(claimsLogin, refused));
    }

    [TestMethod]
    public void TheWaitingEventAndTheMismatchEventsAreDistinct()
    {
        string[] events =
        [
            AgentClaimLogEvents.OwnerRecordMissingWaiting,
            AgentClaimLogEvents.OwnerRecordWaitExpired,
            AgentClaimLogEvents.Claimed,
            AgentClaimLogEvents.HostClaimPending,
            AgentClaimLogEvents.ForRefusal(AgentClaimReason.SessionIdDiffers),
            AgentClaimLogEvents.ForRefusal(AgentClaimReason.RecordSessionEnded),
            AgentClaimLogEvents.ForRefusal(AgentClaimReason.InvalidChildSessionId),
            AgentClaimLogEvents.ForRefusal(AgentClaimReason.ChildSessionDiffers),
        ];

        CollectionAssert.AllItemsAreUnique(
            events,
            "A log has to say whether the agent was waiting for a record or refusing one that existed, so no two findings may share an event.");
    }

    [TestMethod]
    public void ARulePassingOrRecordlessReasonHasNoRefusalEvent()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AgentClaimLogEvents.ForRefusal(AgentClaimReason.Bound));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AgentClaimLogEvents.ForRefusal(AgentClaimReason.HostClaimPending));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AgentClaimLogEvents.ForRefusal(AgentClaimReason.OwnerRecordAbsent),
            "A missing record is the wait's own finding, so it must not borrow a wrong-session event.");
    }

    [TestMethod]
    public void TheRefusalTierHasItsOwnExitCode()
    {
        int[] gateCodes =
        [
            AgentExitCode.Success,
            AgentExitCode.InvalidArguments,
            AgentExitCode.ConsoleSession,
            AgentExitCode.StartFailure,
            AgentExitCode.Unclaimed,
            AgentExitCode.WrongSession,
            AgentExitCode.AuthTierNone,
        ];

        int authTierNone = gateCodes[gateCodes.Length - 1];

        Assert.AreEqual(
            7,
            authTierNone,
            "The refusal tier must be tellable apart from every other gate verdict.");
        CollectionAssert.AllItemsAreUnique(
            gateCodes,
            "Every gate verdict needs its own exit code, or a caller cannot tell why the agent stopped.");
    }

    [TestMethod]
    public void ClaimPolicyBindsTheRecordSessionAndChildWtsSession()
    {
        SessionOwnerRecord claimed = Record(SessionId, 7, SessionState.Ready, subagentWtsSessionId: 7);

        Assert.AreEqual(
            AgentClaimVerdict.Claimed,
            AgentClaimPolicy.Evaluate(claimed, SessionId, 7));
        Assert.AreEqual(
            AgentClaimVerdict.PendingHostClaim,
            AgentClaimPolicy.Evaluate(Record(SessionId, 7, SessionState.Connecting, 0), SessionId, 7));
        Assert.AreEqual(
            AgentClaimVerdict.WrongSession,
            AgentClaimPolicy.Evaluate(Record(SessionId, 7, SessionState.Ready, 8), SessionId, 7));
        Assert.AreEqual(
            AgentClaimVerdict.WrongSession,
            AgentClaimPolicy.Evaluate(claimed, "c9f1c2d4-5678-4abc-9def-0a1b2c3d4e5f", 7));
        Assert.AreEqual(
            AgentClaimVerdict.NoOwnerRecord,
            AgentClaimPolicy.Evaluate(null, SessionId, 7));
    }

    [TestMethod]
    public void ClaimPolicyRefusesAnEndedRecordBeforeThePipeCanClaimIt()
    {
        SessionOwnerRecord closing = Record(SessionId, 7, SessionState.Closing, subagentWtsSessionId: 7);
        SessionOwnerRecord disconnected = Record(SessionId, 7, SessionState.Disconnected, subagentWtsSessionId: 0);

        Assert.AreEqual(
            AgentClaimVerdict.WrongSession,
            AgentClaimPolicy.Evaluate(closing, SessionId, 7),
            "A record in closing describes a session that is ending, so the agent must not serve it.");
        Assert.AreEqual(
            AgentClaimVerdict.WrongSession,
            AgentClaimPolicy.Evaluate(disconnected, SessionId, 7),
            "A disconnected record is terminal, and a zero child session id must not turn it into a pending claim.");
    }

    [TestMethod]
    public void ClaimPolicyNamesTheReasonBehindEveryWrongSession()
    {
        SessionOwnerRecord claimed = Record(SessionId, 7, SessionState.Ready, subagentWtsSessionId: 7);

        Assert.AreEqual(
            AgentClaimReason.Bound,
            AgentClaimPolicy.Decide(claimed, SessionId, 7).Reason);
        Assert.AreEqual(
            AgentClaimReason.HostClaimPending,
            AgentClaimPolicy.Decide(Record(SessionId, 7, SessionState.Connecting, 0), SessionId, 7).Reason);
        Assert.AreEqual(
            AgentClaimReason.OwnerRecordAbsent,
            AgentClaimPolicy.Decide(null, SessionId, 7).Reason);
        Assert.AreEqual(
            AgentClaimReason.SessionIdDiffers,
            AgentClaimPolicy.Decide(claimed, "c9f1c2d4-5678-4abc-9def-0a1b2c3d4e5f", 7).Reason);
        Assert.AreEqual(
            AgentClaimReason.RecordSessionEnded,
            AgentClaimPolicy.Decide(Record(SessionId, 7, SessionState.Closing, 7), SessionId, 7).Reason);
        Assert.AreEqual(
            AgentClaimReason.RecordSessionEnded,
            AgentClaimPolicy.Decide(Record(SessionId, 7, SessionState.Disconnected, 7), SessionId, 7).Reason);
        Assert.AreEqual(
            AgentClaimReason.InvalidChildSessionId,
            AgentClaimPolicy.Decide(Record(SessionId, 7, SessionState.Ready, -1), SessionId, 7).Reason);
        Assert.AreEqual(
            AgentClaimReason.ChildSessionDiffers,
            AgentClaimPolicy.Decide(Record(SessionId, 7, SessionState.Ready, 8), SessionId, 7).Reason);
    }

    [TestMethod]
    public void APendingHostClaimIsNotAnUnclaimedAgent()
    {
        AgentClaimDecision pending = AgentClaimPolicy.Decide(
            Record(SessionId, 7, SessionState.Connecting, 0),
            SessionId,
            7);

        Assert.AreEqual(
            AgentClaimVerdict.PendingHostClaim,
            pending.Verdict,
            "A zero child session id means the host has not written it yet, so the agent continues to the pipe's nonce claim.");
        Assert.AreNotEqual(
            AgentClaimVerdict.WrongSession,
            pending.Verdict,
            "Not-yet-written must never be read as a failed claim, because that would exit before the pipe can authenticate.");
    }

    [TestMethod]
    public void InteractiveDesktopNamesAcceptOnlyWinSta0AndDefault()
    {
        Assert.IsTrue(WindowStationProbe.IsInteractiveName("WinSta0", "Default"));
        Assert.IsFalse(WindowStationProbe.IsInteractiveName("Service-0x0-3e7$", "Default"));
        Assert.IsFalse(WindowStationProbe.IsInteractiveName("WinSta0", "Disconnect"));
        Assert.IsFalse(WindowStationProbe.IsInteractiveName("winsta0", "Default"));
    }

    [TestMethod]
    public void CurrentProcessRunsOnTheInteractiveDesktop()
    {
        Assert.IsTrue(
            WindowStationProbe.IsInteractiveDesktop(),
            "The test process must run on WinSta0\\Default for the agent's claim gate.");
    }

    [TestMethod]
    public async Task DesktopClientReportsReadyAndForwardsEveryOperation()
    {
        FakeDesktopSurface surface = new()
        {
            Screen = new DesktopScreen(0, 0, 1920, 1080),
            Frame = [0x89, 0x50, 0x4E, 0x47],
        };
        DesktopSessionClient client = new(
            surface,
            SessionId,
            AuthTier.SystemCredential,
            GateStrength.Login,
            helloHint: null);

        SessionStatus status = await client.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual("ready", status.State);
        Assert.AreEqual(SessionId, status.SessionId);
        Assert.AreEqual(AuthTier.SystemCredential, status.AuthTier);
        Assert.AreEqual(GateStrength.Login, status.GateStrength);
        Assert.IsNull(status.HelloHint);
        Assert.AreEqual(1920, status.Screen.Width);
        Assert.AreEqual(1080, status.Screen.Height);

        byte[] frame = await client.ScreenshotAsync(CancellationToken.None);
        CollectionAssert.AreEqual(surface.Frame, frame);

        await client.MouseMoveAsync(10, 20, CancellationToken.None);
        await client.MouseClickAsync(30, 40, MouseButton.Right, 2, CancellationToken.None);
        await client.MouseScrollAsync(50, 60, 120, -120, CancellationToken.None);
        await client.KeyTypeAsync("text", CancellationToken.None);
        await client.KeyPressAsync([SessionKey.Ctrl, SessionKey.Alt, SessionKey.Delete], CancellationToken.None);

        Assert.AreEqual((10, 20), surface.LastMove);
        Assert.AreEqual((30, 40, MouseButton.Right, 2), surface.LastClick);
        Assert.AreEqual((50, 60, 120, -120), surface.LastScroll);
        Assert.AreEqual("text", surface.LastText);
        CollectionAssert.AreEqual(
            new[] { SessionKey.Ctrl, SessionKey.Alt, SessionKey.Delete },
            new List<SessionKey>(surface.LastKeys));
    }

    [TestMethod]
    public async Task DesktopClientReportsTheOperatorHintItWasGiven()
    {
        FakeDesktopSurface surface = new()
        {
            Screen = new DesktopScreen(0, 0, 100, 100),
            Frame = [1],
        };
        DesktopSessionClient client = new(
            surface,
            SessionId,
            AuthTier.SystemCredential,
            GateStrength.Login,
            AgentAuthTier.HelloNotEnrolledHint);

        SessionStatus status = await client.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual("helloNotEnrolled", status.HelloHint);
    }

    [TestMethod]
    public void DesktopClientRefusesAHintThatDoesNotBelongToItsTier()
    {
        FakeDesktopSurface surface = new()
        {
            Screen = new DesktopScreen(0, 0, 100, 100),
            Frame = [1],
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => new DesktopSessionClient(
                surface,
                SessionId,
                AuthTier.HelloEnrolled,
                GateStrength.Strong,
                AgentAuthTier.HelloNotEnrolledHint));
        Assert.ThrowsExactly<ArgumentException>(
            () => new DesktopSessionClient(
                surface,
                SessionId,
                AuthTier.SystemCredential,
                GateStrength.Login,
                "some other hint"));
        Assert.ThrowsExactly<ArgumentException>(
            () => new DesktopSessionClient(
                surface,
                SessionId,
                AuthTier.None,
                GateStrength.Login,
                helloHint: null));
    }

    [TestMethod]
    public async Task DesktopClientStopsBeforeTheSurfaceWhenCancelled()
    {
        FakeDesktopSurface surface = new()
        {
            Screen = new DesktopScreen(0, 0, 100, 100),
            Frame = [1],
        };
        DesktopSessionClient client = new(
            surface,
            SessionId,
            AuthTier.HelloEnrolled,
            GateStrength.Strong,
            helloHint: null);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => client.MouseClickAsync(1, 1, MouseButton.Left, 1, cancelled.Token));
        Assert.IsNull(surface.LastClick);
    }

    private static AgentOptions ParseMinimal()
    {
        return AgentCommandLine.Parse(
        [
            "--scu-bootstrap",
            "--session",
            SessionId,
            "--nonce",
            Nonce,
        ]);
    }

    private static SessionOwnerRecord Record(
        string sessionId,
        int childSessionId,
        SessionState state,
        int subagentWtsSessionId)
    {
        return new SessionOwnerRecord(
            sessionId,
            childSessionId,
            DateTimeOffset.UtcNow,
            subagentWtsSessionId,
            false,
            state,
            DateTimeOffset.UtcNow);
    }

    private sealed class FakeDesktopSurface : IDesktopSurface
    {
        public DesktopScreen Screen { get; init; } = new(0, 0, 1, 1);

        public byte[] Frame { get; init; } = [];

        public (int X, int Y)? LastMove { get; private set; }

        public (int X, int Y, MouseButton Button, int Count)? LastClick { get; private set; }

        public (int X, int Y, int DeltaX, int DeltaY)? LastScroll { get; private set; }

        public string? LastText { get; private set; }

        public IReadOnlyList<SessionKey> LastKeys { get; private set; } = [];

        public DesktopScreen GetScreen()
        {
            return Screen;
        }

        public byte[] CapturePng()
        {
            return Frame;
        }

        public void MovePointer(int x, int y)
        {
            LastMove = (x, y);
        }

        public void Click(int x, int y, MouseButton button, int count)
        {
            LastClick = (x, y, button, count);
        }

        public void Scroll(int x, int y, int deltaX, int deltaY)
        {
            LastScroll = (x, y, deltaX, deltaY);
        }

        public void TypeText(string text)
        {
            LastText = text;
        }

        public void PressKeys(IReadOnlyList<SessionKey> keys)
        {
            LastKeys = [.. keys];
        }
    }
}