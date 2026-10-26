using System;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Owner discovery against real machine state: a real kernel lock object, a real owner pipe server and
/// the real 300 ms probe.
/// </summary>
/// <remarks>
/// Every case uses a per-run lock name of the form <c>Global\SoulComputerUse-Test-{guid}</c> and a
/// per-run pipe name of the form <c>SoulComputerUse.owner.test-{guid}</c>, so the contract-pinned
/// production lock and the production owner pipe are never touched. Each case disposes the lock and
/// the pipe it created; no files are written, so there is no temporary directory to remove.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class OwnerDiscoveryIntegrationTests
{
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(30);

    private string _lockName = string.Empty;
    private string _pipeName = string.Empty;

    [TestInitialize]
    public void CreateIsolatedNames()
    {
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
        _pipeName = "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N");
    }

    [TestCleanup]
    public void AssertNothingIsLeftBehind()
    {
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The case's own lock object must be gone afterwards, otherwise the case leaked a holder.");
    }

    [TestMethod]
    public void TheProbeSeesARealLockAndStopsSeeingItAfterRelease()
    {
        OwnerProbe probe = new(_lockName, _pipeName);

        Assert.IsFalse(
            probe.LockExists(),
            "Probing before any holder exists must report no lock object, not create one.");

        SingleInstanceLock held = SingleInstanceLock.Acquire(_lockName);
        try
        {
            Assert.IsTrue(
                probe.LockExists(),
                "With a live holder the probe must report the lock object, because the handle model defines occupancy.");
        }
        finally
        {
            held.Dispose();
        }

        Assert.IsFalse(probe.LockExists(), "Closing the only handle destroys the object, so the probe must report no lock.");
    }

    [TestMethod]
    public async Task TheProbeConnectsToARealOwnerPipeWithinTheTimeout()
    {
        OwnerProbe probe = new(_lockName, _pipeName);

        bool beforeServer = await probe.IsOwnerPipeReachableAsync(CancellationToken.None).WaitAsync(CaseTimeout);
        Assert.IsFalse(beforeServer, "With no server listening the probe must report the pipe as unreachable.");

        StubOwnerToolHandler handler = new();
        NamedPipeSessionServer server = new(_pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        try
        {
            bool whileServing = await probe.IsOwnerPipeReachableAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.IsTrue(whileServing, "A listening owner pipe must be reported as reachable within the probe timeout.");
        }
        finally
        {
            serverLifetime.Cancel();
            await acceptLoop.WaitAsync(CaseTimeout);
            await server.DisposeAsync();
        }

        bool afterStop = await probe.IsOwnerPipeReachableAsync(CancellationToken.None).WaitAsync(CaseTimeout);
        Assert.IsFalse(afterStop, "A stopped owner pipe must be reported as unreachable without throwing.");
    }

    [TestMethod]
    public async Task AReachableOwnerIsUsedForARealStatusRoundTrip()
    {
        StubOwnerToolHandler handler = new();
        await using NamedPipeSessionServer server = new(_pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        OwnerSessionResolver resolver = new(
            new OwnerProbe(_lockName, _pipeName),
            () => new NamedPipeSessionClient(_pipeName),
            null);

        await using (ResolvedSession session = await resolver.ResolveAsync(CancellationToken.None))
        {
            SessionStatus status = await session.Client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);

            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(StubOwnerToolHandler.SessionId, status.SessionId);
            Assert.AreEqual(1920, status.Screen.Width);
            Assert.AreEqual(1080, status.Screen.Height);
            Assert.AreEqual(1, handler.CallCount, "The resolved client must carry exactly the one real status call.");
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task AHeldLockWithoutAnAnswerFailsWithLockHeldAndStartsNothing()
    {
        using SingleInstanceLock held = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(held.IsHeld, "The case needs a live lock holder for its premise to hold.");

        RecordingSessionStarter starter = new();
        OwnerSessionResolver resolver = new(
            new OwnerProbe(_lockName, _pipeName),
            () => new NamedPipeSessionClient(_pipeName),
            starter);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => resolver.ResolveAsync(CancellationToken.None).WaitAsync(CaseTimeout));

        Assert.AreEqual(SessionErrorCode.LockHeld, error.Code);
        Assert.IsFalse(error.Retryable, "The contract marks a held lock as not retryable.");
        Assert.AreEqual(0, starter.StartCount, "A held lock must never lead to a second session.");
        StringAssert.Contains(error.Message, "lock", "The message must name the condition that stopped the caller.");
        StringAssert.Contains(error.Message, "Task Manager", "The message must say how to look for the holding process.");
        Assert.IsFalse(error.Message.Contains(_lockName, StringComparison.Ordinal), "A message must not echo a machine object name.");
    }

    [TestMethod]
    public async Task AFreeLockWithoutAStarterFailsWithNotReady()
    {
        OwnerSessionResolver resolver = new(
            new OwnerProbe(_lockName, _pipeName),
            () => new NamedPipeSessionClient(_pipeName),
            null);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => resolver.ResolveAsync(CancellationToken.None).WaitAsync(CaseTimeout));

        Assert.AreEqual(SessionErrorCode.NotReady, error.Code);
        StringAssert.Contains(error.Message, "Host", "The message must point at the only way out in this version.");
    }

    private sealed class RecordingSessionStarter : IOwnerSessionStarter
    {
        internal int StartCount { get; private set; }

        public Task<ISessionClient> StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            throw new InvalidOperationException(
                "No session starter exists in this version, so a start attempt is a defect rather than a slow path.");
        }
    }

    private sealed class StubOwnerToolHandler : ISessionToolHandler
    {
        internal const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";

        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<SessionToolResult> HandleAsync(SessionTool tool, System.Text.Json.JsonElement arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);

            if (tool != SessionTool.GetStatus)
            {
                throw new InvalidOperationException("The discovery stub only serves the status tool.");
            }

            SessionStatus status = new(
                "ready",
                SessionId,
                AuthTier.SystemCredential,
                GateStrength.Login,
                new SessionScreen(1920, 1080),
                null);
            return Task.FromResult(SessionToolResult.Success(SessionToolPayloads.WriteStatus(status)));
        }
    }
}
