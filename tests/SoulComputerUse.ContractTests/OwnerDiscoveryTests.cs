using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The pure discovery table of section 3 and the failure semantics of the resolver: an answering owner
/// is always used, a free lock without an answer may start a session, a held lock whose record shows a
/// live starting owner is retryable, and any other held lock without an answer fails with actionable
/// guidance instead of a claim about leftover records.
/// </summary>
[TestClass]
public sealed class OwnerDiscoveryTests
{
    private const string LockHeldGuidance = "Task Manager";
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";

    [TestMethod]
    public void AHeldLockWithAnAnsweringPipeAttachesToTheExistingOwner()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.UseExistingOwner,
            OwnerDiscovery.Decide(lockExists: true, ownerPipeReachable: true, liveOwnerStarting: false));
    }

    [TestMethod]
    public void AHeldLockWithoutAnAnswerRefusesWithLockHeld()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.RefuseLockHeld,
            OwnerDiscovery.Decide(lockExists: true, ownerPipeReachable: false, liveOwnerStarting: false));
    }

    [TestMethod]
    public void AnAnsweringPipeWinsWhileTheLockIsNotYetVisible()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.UseExistingOwner,
            OwnerDiscovery.Decide(lockExists: false, ownerPipeReachable: true, liveOwnerStarting: false));
    }

    [TestMethod]
    public void AFreeLockWithoutAnAnswerAllowsStartingAnOwnSession()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.StartOwnSession,
            OwnerDiscovery.Decide(lockExists: false, ownerPipeReachable: false, liveOwnerStarting: false));
    }

    [TestMethod]
    public void AHeldLockThatALiveOwnerIsStillStartingIsRetryableNotLockHeld()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.RefuseStartInProgress,
            OwnerDiscovery.Decide(lockExists: true, ownerPipeReachable: false, liveOwnerStarting: true));
    }

    [TestMethod]
    public void AHeldLockWithADeadRecordStillRefusesWithLockHeld()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.RefuseLockHeld,
            OwnerDiscovery.Decide(lockExists: true, ownerPipeReachable: false, liveOwnerStarting: false));
    }

    [TestMethod]
    public void AnAnsweringPipeWinsOverAStartingRecord()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.UseExistingOwner,
            OwnerDiscovery.Decide(lockExists: true, ownerPipeReachable: true, liveOwnerStarting: true));
    }

    [TestMethod]
    public void AFreeLockWithAStartingRecordStillAllowsStartingAnOwnSession()
    {
        Assert.AreEqual(
            OwnerDiscoveryDecision.StartOwnSession,
            OwnerDiscovery.Decide(lockExists: false, ownerPipeReachable: false, liveOwnerStarting: true),
            "A record without a lock belongs to a previous attempt, so it must not block a fresh start.");
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
            new StubOwnerProbe(lockExists: true, ownerPipeReachable: false, liveOwnerStarting: false),
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
    public async Task AStartingOwnerFailsWithRetryableNotReadyAndNoMachineData()
    {
        RecordingSessionStarter starter = new(new RecordingSessionClient());
        OwnerSessionResolver resolver = new(
            new StubOwnerProbe(lockExists: true, ownerPipeReachable: false, liveOwnerStarting: true),
            static () => new RecordingSessionClient(),
            starter);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => resolver.ResolveAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.NotReady, error.Code);
        Assert.IsTrue(
            SessionErrorCatalog.IsRetryable(error.Code),
            "A session owner that is still starting must be retryable, not a reason to kill a process.");
        StringAssert.Contains(error.Message, "starting", "The message must say that the owner is still starting.");
        Assert.AreEqual(0, starter.StartCount, "A starting owner must never lead to a second session.");
        AssertNoMachineData(error.Message);
    }

    [TestMethod]
    public async Task AFreeLockWithoutAStarterFailsWithNotReadyInsteadOfAnUnavailableClient()
    {
        OwnerSessionResolver resolver = new(
            new StubOwnerProbe(lockExists: false, ownerPipeReachable: false, liveOwnerStarting: false),
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
            new StubOwnerProbe(lockExists: false, ownerPipeReachable: false, liveOwnerStarting: false),
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
            new StubOwnerProbe(lockExists: true, ownerPipeReachable: true, liveOwnerStarting: false),
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

    [TestMethod]
    public void ARecordThatNamesThisProcessAsStartingIsALiveStartingOwner()
    {
        using TemporaryOwnerRecord record = TemporaryOwnerRecord.Create(SessionState.Starting);

        Assert.IsTrue(
            record.Probe.IsLiveOwnerStarting(),
            "A record of a running process in the starting state is the logon window discovery must recognise.");
    }

    [TestMethod]
    public void ARecordThatNamesThisProcessAsConnectingIsALiveStartingOwner()
    {
        using TemporaryOwnerRecord record = TemporaryOwnerRecord.Create(SessionState.Connecting);

        Assert.IsTrue(
            record.Probe.IsLiveOwnerStarting(),
            "A session that is connecting has not reached its pipe either, so it is still starting for discovery.");
    }

    [TestMethod]
    public void ARecordThatNamesAReadyOwnerIsNotStarting()
    {
        using TemporaryOwnerRecord record = TemporaryOwnerRecord.Create(SessionState.Ready);

        Assert.IsFalse(
            record.Probe.IsLiveOwnerStarting(),
            "A ready owner is expected to answer on the pipe, so its record must not excuse a silent lock.");
    }

    [TestMethod]
    public void AStartingRecordOfAProcessThatIsGoneIsNotALiveStartingOwner()
    {
        int exitedPid = StartAndWaitForExit(out DateTimeOffset exitedStartTime);
        using TemporaryOwnerRecord record = TemporaryOwnerRecord.CreateForExitedProcess(
            SessionState.Starting,
            exitedPid,
            exitedStartTime);

        Assert.IsFalse(
            record.Probe.IsLiveOwnerStarting(),
            "A record can outlive its process, so a dead process must not be reported as a starting owner.");
    }

    [TestMethod]
    public void AStartingRecordWithAMismatchedStartTimeIsNotALiveStartingOwner()
    {
        using TemporaryOwnerRecord record = TemporaryOwnerRecord.Create(
            SessionState.Starting,
            DateTimeOffset.UtcNow.AddHours(-1));

        Assert.IsFalse(
            record.Probe.IsLiveOwnerStarting(),
            "A start time that does not match means the process id was reused, so the record names no live owner.");
    }

    [TestMethod]
    public void AMissingRecordIsNotALiveStartingOwner()
    {
        using TemporaryOwnerRecord record = TemporaryOwnerRecord.CreateWithoutRecord();

        Assert.IsFalse(
            record.Probe.IsLiveOwnerStarting(),
            "An absent record must fall back to the held-lock refusal instead of claiming a starting owner.");
    }

    [TestMethod]
    public void AnUnreadableRecordIsNotALiveStartingOwner()
    {
        using TemporaryOwnerRecord record = TemporaryOwnerRecord.CreateUnreadable();

        Assert.IsFalse(
            record.Probe.IsLiveOwnerStarting(),
            "A record that cannot be parsed must mean 'not observed as starting', never an exception.");
    }

    private static int StartAndWaitForExit(out DateTimeOffset startTime)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("The short-lived helper process could not be started.");

        // Read the start time while the process is still alive, so the record under test differs from a
        // live owner in the liveness criterion only.
        startTime = new DateTimeOffset(process.StartTime.ToUniversalTime());
        process.WaitForExit();
        return process.Id;
    }

    /// <summary>The start time the record must carry for the running test process to count as its owner.</summary>
    private static DateTimeOffset CurrentProcessStartTime()
    {
        using Process process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime());
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
        private readonly bool _liveOwnerStarting;

        internal StubOwnerProbe(bool lockExists, bool ownerPipeReachable, bool liveOwnerStarting)
        {
            _lockExists = lockExists;
            _ownerPipeReachable = ownerPipeReachable;
            _liveOwnerStarting = liveOwnerStarting;
        }

        public bool LockExists()
        {
            return _lockExists;
        }

        public Task<bool> IsOwnerPipeReachableAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_ownerPipeReachable);
        }

        public bool IsLiveOwnerStarting()
        {
            return _liveOwnerStarting;
        }
    }

    /// <summary>
    /// A per-case ownership record file plus the probe that reads it, so the liveness observation is
    /// proved against a real record instead of a stub of its own.
    /// </summary>
    private sealed class TemporaryOwnerRecord : IDisposable
    {
        private readonly string _root;
        private readonly SessionOwnerStore _store;

        private TemporaryOwnerRecord(string root, SessionOwnerStore store, OwnerProbe probe)
        {
            _root = root;
            _store = store;
            Probe = probe;
        }

        internal OwnerProbe Probe { get; }

        internal static TemporaryOwnerRecord Create(SessionState state)
        {
            TemporaryOwnerRecord record = CreateEmpty();
            record.Write(state, CurrentProcessStartTime(), Environment.ProcessId);
            return record;
        }

        internal static TemporaryOwnerRecord Create(SessionState state, DateTimeOffset hostProcessStartTime)
        {
            TemporaryOwnerRecord record = CreateEmpty();
            record.Write(state, hostProcessStartTime, Environment.ProcessId);
            return record;
        }

        internal static TemporaryOwnerRecord CreateForExitedProcess(
            SessionState state,
            int hostPid,
            DateTimeOffset hostProcessStartTime)
        {
            TemporaryOwnerRecord record = CreateEmpty();
            record.Write(state, hostProcessStartTime, hostPid);
            return record;
        }

        internal static TemporaryOwnerRecord CreateWithoutRecord()
        {
            return CreateEmpty();
        }

        internal static TemporaryOwnerRecord CreateUnreadable()
        {
            TemporaryOwnerRecord record = CreateEmpty();
            string directory = Path.GetDirectoryName(record._store.RecordPath)
                ?? throw new InvalidOperationException("The test record path has no directory part.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(record._store.RecordPath, "{ this is not a record }");
            return record;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static TemporaryOwnerRecord CreateEmpty()
        {
            string root = Path.Combine(Path.GetTempPath(), "scu-owner-discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            SessionOwnerStore store = new(root, TestSid);
            OwnerProbe probe = new(
                @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N"),
                "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N"),
                store);
            return new TemporaryOwnerRecord(root, store, probe);
        }

        private void Write(SessionState state, DateTimeOffset startTime, int hostPid)
        {
            SessionOwnerRecord record = new(
                Guid.NewGuid().ToString("D"),
                hostPid,
                startTime,
                0,
                false,
                state,
                DateTimeOffset.UtcNow);
            _store.Write(record);
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
