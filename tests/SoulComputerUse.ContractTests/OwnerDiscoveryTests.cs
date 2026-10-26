using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The pure discovery table of section 3 and the failure semantics of the resolver: an answering owner
/// is always used, a free lock without an answer may start a session, and a held lock without an
/// answer fails with actionable guidance instead of a claim about leftover records.
/// </summary>
[TestClass]
public sealed class OwnerDiscoveryTests
{
    private const string LockHeldGuidance = "Task Manager";

    [TestMethod]
    public void AHeldLockWithAnAnsweringPipeAttachesToTheExistingOwner()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.UseExistingOwner,
            OwnerDiscovery.Decide(lockExists: true, ownerPipeReachable: true));
    }

    [TestMethod]
    public void AHeldLockWithoutAnAnswerRefusesWithLockHeld()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.RefuseLockHeld,
            OwnerDiscovery.Decide(lockExists: true, ownerPipeReachable: false));
    }

    [TestMethod]
    public void AnAnsweringPipeWinsWhileTheLockIsNotYetVisible()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.UseExistingOwner,
            OwnerDiscovery.Decide(lockExists: false, ownerPipeReachable: true));
    }

    [TestMethod]
    public void AFreeLockWithoutAnAnswerAllowsStartingAnOwnSession()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.StartOwnSession,
            OwnerDiscovery.Decide(lockExists: false, ownerPipeReachable: false));
    }

    [TestMethod]
    public void ThePipeProbeGivesUpAfterTheContractProbeTimeout()
    {
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(300),
            OwnerProbe.PipeProbeTimeout,
            "Discovery must not wait longer than the probe timeout before deciding an owner is silent.");
    }

    [TestMethod]
    public async Task AHeldLockWithoutAnAnswerFailsWithLockHeldAndOperatorGuidance()
    {
        RecordingSessionStarter starter = new(new RecordingSessionClient());
        OwnerSessionResolver resolver = new(
            new StubOwnerProbe(lockExists: true, ownerPipeReachable: false),
            static () => new RecordingSessionClient(),
            starter);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => resolver.ResolveAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.LockHeld, error.Code);
        Assert.IsFalse(error.Retryable, "A held lock is not retryable in the contract.");
        Assert.AreEqual(0, starter.StartCount, "A held lock must never start a second session.");

        StringAssert.Contains(error.Message, "holds the single-instance lock", "The message must say that a process holds the lock.");
        StringAssert.Contains(error.Message, LockHeldGuidance, "The message must say how to look for the holder.");
        Assert.DoesNotContain("stale", error.Message, "A silent lock holder is not proof of a stale record.");
        Assert.DoesNotContain("delete", error.Message, "Deleting a record cannot release a foreign handle.");
        AssertNoMachineData(error.Message);
    }

    [TestMethod]
    public async Task AFreeLockWithoutAStarterFailsWithNotReadyInsteadOfAnUnavailableClient()
    {
        OwnerSessionResolver resolver = new(
            new StubOwnerProbe(lockExists: false, ownerPipeReachable: false),
            static () => new RecordingSessionClient(),
            null);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => resolver.ResolveAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.NotReady, error.Code);
        Assert.IsTrue(error.Retryable, "The contract marks E_NOT_READY as retryable.");
        StringAssert.Contains(error.Message, "Host", "The message must name the way out: start the host first.");
        AssertNoMachineData(error.Message);
    }

    [TestMethod]
    public async Task AFreeLockWithAStarterReturnsTheStartedClient()
    {
        RecordingSessionClient started = new();
        RecordingSessionStarter starter = new(started);
        OwnerSessionResolver resolver = new(
            new StubOwnerProbe(lockExists: false, ownerPipeReachable: false),
            static () => new RecordingSessionClient(),
            starter);

        await using ResolvedSession session = await resolver.ResolveAsync(CancellationToken.None);

        Assert.AreSame(started, session.Client, "The resolver must hand back the client the starter produced.");
        Assert.AreEqual(1, starter.StartCount);
    }

    [TestMethod]
    public async Task AnAnsweringOwnerIsPreferredEvenWhenAStarterIsInjected()
    {
        RecordingSessionClient existingOwner = new();
        RecordingSessionStarter starter = new(new RecordingSessionClient());
        OwnerSessionResolver resolver = new(
            new StubOwnerProbe(lockExists: true, ownerPipeReachable: true),
            () => existingOwner,
            starter);

        await using ResolvedSession session = await resolver.ResolveAsync(CancellationToken.None);

        Assert.AreSame(existingOwner, session.Client);
        Assert.AreEqual(0, starter.StartCount, "An answering owner means no second session may be started.");
    }

    [TestMethod]
    public async Task ResolvedSessionReleasesTheClientAndTheCallbackOnlyOnce()
    {
        RecordingSessionClient client = new();
        int releaseCount = 0;
        ResolvedSession session = new(
            client,
            () =>
            {
                releaseCount++;
                return ValueTask.CompletedTask;
            });

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.AreEqual(1, client.DisposeCount, "Closing the connection twice must be prevented by the wrapper.");
        Assert.AreEqual(1, releaseCount, "A release step must run exactly once even after repeated disposal.");
    }

    [TestMethod]
    public async Task AFailedClientCloseStillRunsTheReleaseStep()
    {
        RecordingSessionClient client = new() { ThrowOnDispose = true };
        int releaseCount = 0;
        ResolvedSession session = new(
            client,
            () =>
            {
                releaseCount++;
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await session.DisposeAsync());

        Assert.AreEqual(1, releaseCount, "A failed close must not skip the release of the session's own state.");
    }

    private static void AssertNoMachineData(string message)
    {
        Assert.DoesNotContain("\\", message, "An error message must not carry a path.");
        Assert.DoesNotContain("/", message, "An error message must not carry a path.");
        Assert.DoesNotContain("S-1-", message, "An error message must not carry an account identifier.");
    }

    private sealed class StubOwnerProbe : IOwnerProbe
    {
        private readonly bool _lockExists;
        private readonly bool _ownerPipeReachable;

        internal StubOwnerProbe(bool lockExists, bool ownerPipeReachable)
        {
            _lockExists = lockExists;
            _ownerPipeReachable = ownerPipeReachable;
        }

        public bool LockExists()
        {
            return _lockExists;
        }

        public Task<bool> IsOwnerPipeReachableAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_ownerPipeReachable);
        }
    }

    private sealed class RecordingSessionStarter : IOwnerSessionStarter
    {
        private readonly ISessionClient _client;

        internal RecordingSessionStarter(ISessionClient client)
        {
            _client = client;
        }

        internal int StartCount { get; private set; }

        public Task<ISessionClient> StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.FromResult(_client);
        }
    }

    private sealed class RecordingSessionClient : ISessionClient, IAsyncDisposable
    {
        internal int DisposeCount { get; private set; }

        internal bool ThrowOnDispose { get; init; }

        public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException("The recording client does not answer status calls.");
        }

        public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException("The recording client does not answer screenshot calls.");
        }

        public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("The recording client does not send pointer moves.");
        }

        public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("The recording client does not send clicks.");
        }

        public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("The recording client does not send scrolls.");
        }

        public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("The recording client does not send text.");
        }

        public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("The recording client does not send key presses.");
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("The connection close failed.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
