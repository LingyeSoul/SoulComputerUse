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
                "--nonce",
                Nonce,
                "--auth-tier",
                "system-credential",
                "--gate-strength",
                "login",
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
    public void ParseRejectsTheNoneTierAndMismatchedGateStrength()
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
            AgentClaimPolicy.Evaluate(Record(SessionId, 7, SessionState.Closing, 7), SessionId, 7));
        Assert.AreEqual(
            AgentClaimVerdict.WrongSession,
            AgentClaimPolicy.Evaluate(Record(SessionId, 7, SessionState.Disconnected, 7), SessionId, 7));
        Assert.AreEqual(
            AgentClaimVerdict.WrongSession,
            AgentClaimPolicy.Evaluate(claimed, "c9f1c2d4-5678-4abc-9def-0a1b2c3d4e5f", 7));
        Assert.AreEqual(
            AgentClaimVerdict.NoOwnerRecord,
            AgentClaimPolicy.Evaluate(null, SessionId, 7));
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
        DesktopSessionClient client = new(surface, SessionId, AuthTier.SystemCredential, GateStrength.Login);

        SessionStatus status = await client.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual("ready", status.State);
        Assert.AreEqual(SessionId, status.SessionId);
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
    public async Task DesktopClientStopsBeforeTheSurfaceWhenCancelled()
    {
        FakeDesktopSurface surface = new()
        {
            Screen = new DesktopScreen(0, 0, 100, 100),
            Frame = [1],
        };
        DesktopSessionClient client = new(surface, SessionId, AuthTier.HelloEnrolled, GateStrength.Strong);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => client.MouseClickAsync(1, 1, MouseButton.Left, 1, cancelled.Token));
        Assert.IsNull(surface.LastClick);
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