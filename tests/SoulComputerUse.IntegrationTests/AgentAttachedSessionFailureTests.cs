using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting;
using SoulComputerUse.Windows.Ipc.Agent;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Covers the attached session's two failure shapes that do not need a live child session: an agent
/// that never answers must fail the attach instead of hanging, and a session that never started must
/// refuse to shut down.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AgentAttachedSessionFailureTests
{
    private const string TestSid = "S-1-5-21-00000000-0000000000-0000000000-1001";
    private static readonly DateTimeOffset HostStartTime = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AcquiredAt = new(2026, 10, 26, 8, 0, 1, TimeSpan.Zero);
    private static readonly TimeSpan ShortConnectTiming = TimeSpan.FromMilliseconds(600);

    private string _root = string.Empty;
    private string _lockName = string.Empty;
    private string _ownerPipe = string.Empty;
    private string _sessionId = string.Empty;
    private SessionOwnerStore _store = null!;

    [TestInitialize]
    public void CreateTemporaryState()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-attach-failure-" + Guid.NewGuid().ToString("N"));
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
    public async Task AMissingAgentFailsTheAttachInsteadOfHanging()
    {
        AgentClientTimings timings = new(
            ShortConnectTiming,
            TimeSpan.FromMilliseconds(600),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
        AgentPipeClient client = new(
            AgentPipeNames.ForSession(_sessionId),
            AgentNonce.Create(),
            timings,
            new WindowsAgentProcessProbe());
        SessionOwnerService owner = new(
            new SessionOwnerOptions(
                _lockName,
                _ownerPipe,
                _store,
                new SessionClientToolHandler(client),
                _sessionId,
                Environment.ProcessId,
                HostStartTime,
                AcquiredAt));
        await using AgentAttachedSession attached = new(client, owner);

        Stopwatch stopwatch = Stopwatch.StartNew();
        SessionOperationException error = Assert.ThrowsExactly<SessionOperationException>(() => attached.Start());
        stopwatch.Stop();

        Assert.AreEqual(SessionErrorCode.Disconnected, error.Code);
        Assert.IsTrue(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            "A missing agent must be reported within the connect timeout instead of blocking the process.");
        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(_lockName), "A failed attach must release the owner lock.");
        Assert.IsFalse(File.Exists(_store.RecordPath), "A failed attach must delete its ownership record.");
    }

    [TestMethod]
    public async Task ShutdownIsRefusedBeforeTheSessionStarted()
    {
        AgentPipeClient client = new(
            AgentPipeNames.ForSession(_sessionId),
            AgentNonce.Create(),
            AgentClientTimings.Contract,
            new WindowsAgentProcessProbe());
        SessionOwnerService owner = new(
            new SessionOwnerOptions(
                _lockName,
                _ownerPipe,
                _store,
                new SessionClientToolHandler(client),
                _sessionId,
                Environment.ProcessId,
                HostStartTime,
                AcquiredAt));
        await using AgentAttachedSession attached = new(client, owner);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => attached.CloseAsync(CancellationToken.None));
        Assert.IsFalse(File.Exists(_store.RecordPath), "Nothing was started, so no ownership record may exist.");
    }
}