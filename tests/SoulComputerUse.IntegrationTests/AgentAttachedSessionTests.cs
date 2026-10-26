using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.Ipc.Agent;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// End-to-end integration over both real named pipes: the owner pipe speaks to
/// <see cref="AgentAttachedSession"/> and the agent pipe speaks to a test agent server. Nothing here
/// touches a real child session or injects input; the agent-side client is a recording fake.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AgentAttachedSessionTests
{
    private const string TestSid = "S-1-5-21-00000000-0000000000-0000000000-1001";
    private static readonly DateTimeOffset HostStartTime = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AcquiredAt = new(2026, 10, 26, 8, 0, 1, TimeSpan.Zero);

    private string _root = string.Empty;
    private string _lockName = string.Empty;
    private string _ownerPipe = string.Empty;
    private string _sessionId = string.Empty;
    private SessionOwnerStore _store = null!;

    [TestInitialize]
    public void CreateTemporaryState()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-attached-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
        _ownerPipe = "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N");
        _sessionId = Guid.NewGuid().ToString("D");
        _store = new SessionOwnerStore(_root, TestSid);
    }

    [TestCleanup]
    public void DeleteTemporaryState()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OwnerPipeReachesTheAgentPipeThroughBothHops()
    {
        RecordingSessionClient agentClient = new(ReadyStatus(_sessionId));
        string agentPipe = AgentPipeNames.ForSession(_sessionId);
        string nonce = AgentNonce.Create();
        await using AgentPipeServer server = new(
            agentPipe,
            nonce,
            new SessionClientToolHandler(agentClient),
            AgentServerTimings.Contract);
        using CancellationTokenSource serverLifetime = new();
        Task serverRun = server.RunAsync(serverLifetime.Token);
        await server.WaitUntilListeningAsync(CancellationToken.None);

        AgentPipeClient hostAgentClient = new(
            agentPipe,
            nonce,
            AgentClientTimings.Contract,
            new WindowsAgentProcessProbe());
        SessionClientToolHandler ownerHandler = new(hostAgentClient);
        SessionOwnerService owner = new(
            new SessionOwnerOptions(
                _lockName,
                _ownerPipe,
                _store,
                ownerHandler,
                _sessionId,
                Environment.ProcessId,
                HostStartTime,
                AcquiredAt));
        await using AgentAttachedSession attached = new(hostAgentClient, owner);

        attached.Start();

        Assert.IsNotNull(attached.Status);
        Assert.AreEqual("ready", attached.Status.State);
        SessionOwnerRecord? record = _store.Read();
        Assert.IsNotNull(record);
        Assert.AreEqual(SessionState.Ready, record.State);
        Assert.AreEqual(_sessionId, record.SessionId);
        using (Process current = Process.GetCurrentProcess())
        {
            Assert.AreEqual(current.SessionId, record.SubagentWtsSessionId);
        }

        await using (NamedPipeSessionClient ownerClient = new(_ownerPipe))
        {
            SessionStatus status = await ownerClient.GetStatusAsync(CancellationToken.None);
            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(_sessionId, status.SessionId);
            Assert.AreEqual(1920, status.Screen.Width);

            byte[] frame = await ownerClient.ScreenshotAsync(CancellationToken.None);
            CollectionAssert.AreEqual(agentClient.Frame, frame);

            await ownerClient.MouseClickAsync(12, 34, MouseButton.Right, 2, CancellationToken.None);
            Assert.AreEqual((12, 34, MouseButton.Right, 2), agentClient.LastClick);
        }

        await attached.DisposeAsync();
        await server.DisposeAsync();
        Assert.IsFalse(File.Exists(_store.RecordPath), "Disposing the attached session must delete its record.");
        await serverRun;
    }

    [TestMethod]
    public async Task StartRefusesAnAgentThatIsNotReadyAndCleansTheOwner()
    {
        RecordingSessionClient agentClient = new(ConnectingStatus(_sessionId));
        string agentPipe = AgentPipeNames.ForSession(_sessionId);
        string nonce = AgentNonce.Create();
        await using AgentPipeServer server = new(
            agentPipe,
            nonce,
            new SessionClientToolHandler(agentClient),
            AgentServerTimings.Contract);
        using CancellationTokenSource serverLifetime = new();
        Task serverRun = server.RunAsync(serverLifetime.Token);
        await server.WaitUntilListeningAsync(CancellationToken.None);

        AgentPipeClient hostAgentClient = new(
            agentPipe,
            nonce,
            AgentClientTimings.Contract,
            new WindowsAgentProcessProbe());
        SessionOwnerService owner = new(
            new SessionOwnerOptions(
                _lockName,
                _ownerPipe,
                _store,
                new SessionClientToolHandler(hostAgentClient),
                _sessionId,
                Environment.ProcessId,
                HostStartTime,
                AcquiredAt));
        AgentAttachedSession attached = new(hostAgentClient, owner);

        Assert.ThrowsExactly<InvalidOperationException>(() => attached.Start());

        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(_lockName), "A refused attach must release the owner lock.");
        Assert.IsFalse(File.Exists(_store.RecordPath), "A refused attach must delete its ownership record.");
        await server.DisposeAsync();
        await serverRun;
    }

    private static SessionStatus ReadyStatus(string sessionId)
    {
        return new SessionStatus(
            "ready",
            sessionId,
            AuthTier.SystemCredential,
            GateStrength.Login,
            new SessionScreen(1920, 1080),
            null);
    }

    private static SessionStatus ConnectingStatus(string sessionId)
    {
        return new SessionStatus(
            "connecting",
            sessionId,
            AuthTier.SystemCredential,
            GateStrength.Login,
            new SessionScreen(1920, 1080),
            null);
    }

    private sealed class RecordingSessionClient : ISessionClient
    {
        private readonly SessionStatus _status;

        public RecordingSessionClient(SessionStatus status)
        {
            _status = status;
        }

        public byte[] Frame { get; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        public (int X, int Y, MouseButton Button, int Count)? LastClick { get; private set; }

        public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_status);
        }

        public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Frame);
        }

        public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastClick = (x, y, button, count);
            return Task.CompletedTask;
        }

        public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}