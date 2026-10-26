using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Independent adversarial verification of the single-instance lock and the ownership record. These
/// cases deliberately probe the shapes the feature's own tests leave out: the abandoned lock without
/// a keeper handle, concurrent acquisition in one process and across processes, a record whose bytes
/// must survive a failed read, a numeric <c>state</c>, and the contract wire names.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SingleInstanceAdversarialTests
{
    // All-zero tail keeps the SID obviously fictitious: no real account data belongs in the repo.
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";

    /// <summary>How long a helper gets to exit before it is killed again and finally reported.</summary>
    private const int HelperExitTimeoutMilliseconds = 5000;

    private static readonly string[] ContractWireNames =
    [
        "starting",
        "awaiting-credentials",
        "connecting",
        "ready",
        "disconnected",
        "blocked",
        "closing",
    ];

    private readonly List<string> _temporaryRoots = [];

    private string _lockName = string.Empty;

    /// <summary>
    /// Every case uses its own lock name, never the machine-wide one: a helper that outlives a test must
    /// not be able to disturb a real session on this machine.
    /// </summary>
    [TestInitialize]
    public void CreateLockName()
    {
        _lockName = @"Global\SoulComputerUse-Adversarial-" + Guid.NewGuid().ToString("N");
    }

    [TestCleanup]
    public void DeleteTemporaryRoots()
    {
        foreach (string root in _temporaryRoots)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        _temporaryRoots.Clear();
    }

    /// <summary>
    /// The production shape has no second handle on the mutex, so the kernel object dies with its
    /// owner and the next acquisition is a plain one. This pins the measured counter-example showing
    /// that the recovery rules cannot rely on the abandonment outcome alone: contract section 3
    /// criterion 1 counts a newly created object as an acquisition too.
    /// </summary>
    [TestMethod]
    public void OwnerThatDiesWithoutAKeeperHandleLeavesTheLockRecoverable()
    {
        using (Process holder = StartHolder(400))
        {
            Assert.IsTrue(holder.WaitForExit(TimeSpan.FromSeconds(30)), "The holder did not exit in time.");
        }

        using SingleInstanceLock recovered = SingleInstanceLock.Acquire(_lockName);

        Assert.IsTrue(recovered.IsHeld, "A lock whose owner died must still be recoverable.");
        Assert.AreEqual(
            LockAcquireOutcome.Acquired,
            recovered.Outcome,
            "Measured: with no keeper handle the kernel object is destroyed and recreated, so the plain acquisition is reported; a recovery path that only handles the abandonment outcome would never run in production.");
    }

    /// <summary>
    /// Exactly one in-process racer may end up owning the lock; the rest must fail fast. The test
    /// host is shared with the feature's own lock tests, so the process-level owner may already be
    /// held here: that state is detected first instead of being assumed away, and the invariant
    /// that must never break is "never more than one owner".
    /// </summary>
    [TestMethod]
    public void ConcurrentAcquisitionsInThisProcessNeverYieldMoreThanOneOwner()
    {
        const int Racers = 8;
        bool processAlreadyOwnsTheLock;
        using (SingleInstanceLock precondition = SingleInstanceLock.Acquire(_lockName))
        {
            processAlreadyOwnsTheLock = !precondition.IsHeld;
        }

        object gate = new();
        List<SingleInstanceLock> results = [];
        List<Thread> threads = [];
        using CountdownEvent startTogether = new(Racers);

        for (int i = 0; i < Racers; i++)
        {
            Thread racer = new(() =>
            {
                startTogether.Signal();
                startTogether.Wait();
                SingleInstanceLock acquired = SingleInstanceLock.Acquire(_lockName);
                lock (gate)
                {
                    results.Add(acquired);
                }
            });
            threads.Add(racer);
            racer.Start();
        }

        foreach (Thread racer in threads)
        {
            Assert.IsTrue(racer.Join(TimeSpan.FromSeconds(15)), "A racer never finished, so it was waiting instead of failing fast.");
        }

        Assert.HasCount(Racers, results, "Every racer must report an outcome.");

        int expectedOwners = processAlreadyOwnsTheLock ? 0 : 1;
        Assert.AreEqual(
            expectedOwners,
            results.Count(result => result.IsHeld),
            $"Racing threads must never produce more than one owner (process already owned the lock: {processAlreadyOwnsTheLock}).");

        foreach (SingleInstanceLock result in results)
        {
            result.Dispose();
        }
    }

    /// <summary>Exactly one real process may win a race for the machine-wide lock.</summary>
    [TestMethod]
    public void ConcurrentProcessesYieldExactlyOneOwner()
    {
        const int Racers = 4;
        List<Process> racers = [];
        try
        {
            for (int i = 0; i < Racers; i++)
            {
                racers.Add(StartHolder(3000));
            }

            foreach (Process racer in racers)
            {
                Assert.IsTrue(racer.WaitForExit(TimeSpan.FromSeconds(30)), "A racer process did not finish in time.");
            }

            string codes = string.Join(",", racers.Select(racer => racer.ExitCode.ToString(CultureInfo.InvariantCulture)));
            Assert.AreEqual(
                1,
                racers.Count(racer => racer.ExitCode == 0),
                $"Exactly one process may take the machine-wide lock; the losers must exit without it. Exit codes were [{codes}] where 0 means held and 2 means already held.");
        }
        finally
        {
            foreach (Process racer in racers)
            {
                if (!racer.HasExited)
                {
                    racer.Kill(entireProcessTree: true);
                }

                racer.Dispose();
            }
        }
    }

    /// <summary>A refused record must be left byte for byte, not rewritten or truncated.</summary>
    [TestMethod]
    public void CorruptRecordSurvivesAFailedReadByteForByte()
    {
        SessionOwnerStore store = NewStore();
        WriteRawRecord(store, "{ this is not json");

        byte[] before = File.ReadAllBytes(store.RecordPath);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Read());
        byte[] after = File.ReadAllBytes(store.RecordPath);

        CollectionAssert.AreEqual(
            before,
            after,
            "A record that could not be parsed must be preserved exactly so the operator can inspect it.");
    }

    /// <summary>A numeric <c>state</c> must be refused with the contract's explicit unparsable-record error.</summary>
    [TestMethod]
    public void RecordWithANumericStateIsRefusedAsAnUnparsableRecord()
    {
        SessionOwnerStore store = NewStore();
        WriteRawRecord(
            store,
            """
            {
              "sessionId": "b3f1c2d4-0000-4000-8000-000000000000",
              "hostPid": 4242,
              "hostProcessStartTime": "2026-10-25T14:00:00+00:00",
              "subagentWtsSessionId": 3,
              "childSessionsOpenedByUs": false,
              "state": 3,
              "acquiredAt": "2026-10-25T14:00:01+00:00"
            }
            """);

        InvalidOperationException? failure = null;
        try
        {
            store.Read();
        }
        catch (InvalidOperationException error)
        {
            failure = error;
        }

        Assert.IsNotNull(failure, "A numeric state must be refused, never accepted as a contract wire name.");
        Assert.IsNotNull(
            failure.InnerException,
            "PROBE: the contract wants the record-unparsable error; a bare serializer error means the read path did not wrap it.");
        Assert.IsTrue(File.Exists(store.RecordPath), "The refused record must be kept for inspection.");
    }

    /// <summary>Every state must be written with the wire name pinned in the contract.</summary>
    [TestMethod]
    public void EveryStateIsWrittenWithItsContractWireName()
    {
        SessionOwnerStore store = NewStore();
        SessionState[] states = Enum.GetValues<SessionState>();

        List<string> written = [];
        foreach (SessionState state in states)
        {
            store.Write(
                new SessionOwnerRecord(
                    "b3f1c2d4-0000-4000-8000-000000000000",
                    Environment.ProcessId,
                    new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero),
                    3,
                    ChildSessionsOpenedByUs: false,
                    state,
                    new DateTimeOffset(2026, 10, 25, 14, 0, 1, TimeSpan.Zero)));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(store.RecordPath));
            written.Add(document.RootElement.GetProperty("state").GetString() ?? "<null>");
        }

        CollectionAssert.AreEquivalent(
            ContractWireNames,
            written,
            "The written state values must be exactly the contract wire names.");
    }

    /// <summary>
    /// The lock must be releasable from a thread other than the one that took it, including after that
    /// thread has already exited: the shutdown sequence does not run on the startup thread. The
    /// release is proved by letting a second real process take the lock afterwards.
    /// </summary>
    [TestMethod]
    public void LockTakenOnAThreadThatThenExitsIsReleasedByAnotherThread()
    {
        SingleInstanceLock? taken = null;
        Thread acquirer = new(() => taken = SingleInstanceLock.Acquire(_lockName));
        acquirer.Start();
        Assert.IsTrue(acquirer.Join(TimeSpan.FromSeconds(15)), "The acquiring thread did not finish.");

        SingleInstanceLock owned = taken
            ?? throw new InvalidOperationException("The acquiring thread did not report a lock.");
        Assert.IsTrue(owned.IsHeld, "The acquiring thread must have taken the lock.");

        owned.Dispose();

        using Process holder = StartHolderHolding(4000);
        KillHelper(holder);
    }

    /// <summary>
    /// Pins how the record validator treats the subagent session id sentinel: negative is refused while
    /// zero is accepted. The shape is asserted so that the choice is visible rather than incidental.
    /// </summary>
    [TestMethod]
    public void RecordValidatorRefusesNegativeSessionIdsButAcceptsZero()
    {
        SessionOwnerStore store = NewStore();
        WriteRawRecord(store, RecordWithSubagentSessionId(-1));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Read(),
            "A negative subagent session id must be refused.");

        WriteRawRecord(store, RecordWithSubagentSessionId(0));
        SessionOwnerRecord? accepted = store.Read();

        Assert.IsNotNull(
            accepted,
            "A zero subagent session id is accepted today; if that is not intended, the validator must be changed, not this test.");
        Assert.AreEqual(0, accepted.SubagentWtsSessionId, "The accepted record must carry the zero session id unchanged.");
    }

    private static string RecordWithSubagentSessionId(int sessionId)
    {
        return $$"""
        {
          "sessionId": "b3f1c2d4-0000-4000-8000-000000000000",
          "hostPid": 4242,
          "hostProcessStartTime": "2026-10-25T14:00:00+00:00",
          "subagentWtsSessionId": {{sessionId}},
          "childSessionsOpenedByUs": false,
          "state": "ready",
          "acquiredAt": "2026-10-25T14:00:01+00:00"
        }
        """;
    }

    /// <summary>
    /// The recovery rules must recover leftovers only when the owner is gone and the record is terminal
    /// or stale. Checked exhaustively over every state, owner shape and age, including the exact
    /// 24-hour boundary.
    /// </summary>
    [TestMethod]
    public void RecoveryRulesRecoverOnlyForAGoneOwnerWithATerminalOrStaleRecord()
    {
        DateTimeOffset now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        (bool Alive, bool StartTimeMatches)[] ownerShapes =
        [
            (true, true),
            (true, false),
            (false, true),
            (false, false),
        ];

        foreach (SessionState state in Enum.GetValues<SessionState>())
        {
            foreach ((bool Alive, bool StartTimeMatches) owner in ownerShapes)
            {
                foreach (bool stale in new[] { false, true })
                {
                    RecoveryEvidence evidence = new(
                        RecordExists: true,
                        OwnerProcessAlive: owner.Alive,
                        OwnerProcessStartTimeMatches: owner.StartTimeMatches,
                        RecordedState: state,
                        AcquiredAt: stale ? now.AddHours(-25) : now.AddHours(-1),
                        Now: now);

                    bool ownerGone = !owner.Alive || !owner.StartTimeMatches;
                    bool terminal = state is SessionState.Disconnected or SessionState.Closing;
                    RecoveryDecision expected = ownerGone && (terminal || stale)
                        ? RecoveryDecision.RecoverLeftovers
                        : RecoveryDecision.ManualInterventionRequired;

                    Assert.AreEqual(
                        expected,
                        SessionRecoveryPolicy.Evaluate(evidence),
                        $"state={state}, alive={owner.Alive}, startTimeMatches={owner.StartTimeMatches}, stale={stale} must follow the contract's criteria.");
                }
            }
        }

        Assert.AreEqual(
            RecoveryDecision.NormalStart,
            SessionRecoveryPolicy.Evaluate(new RecoveryEvidence(false, false, false, SessionState.Ready, now.AddDays(-30), now)),
            "No record at all must be a normal start, never a recovery.");

        RecoveryEvidence exactlyTwentyFourHours = new(true, false, false, SessionState.Ready, now.AddHours(-24), now);
        Assert.AreEqual(
            RecoveryDecision.ManualInterventionRequired,
            SessionRecoveryPolicy.Evaluate(exactlyTwentyFourHours),
            "Exactly 24 hours is not yet stale, because the contract says more than 24 hours.");
    }

    /// <summary>
    /// The end-to-end leftovers path: a real holder is force-killed, the lock must come back for free,
    /// and the record left behind with that process's real PID and start time must NOT be cleaned up
    /// automatically while it still looks like a live session.
    /// </summary>
    [TestMethod]
    public void ForceKilledOwnerLeavesTheLockFreeAndTheStaleRecordForManualIntervention()
    {
        SessionOwnerStore store = NewStore();
        using Process holder = StartHolderHolding(4000);
        try
        {
            int killedPid = holder.Id;
            DateTimeOffset startedAt = new(holder.StartTime);
            store.Write(
                new SessionOwnerRecord(
                    "b3f1c2d4-0000-4000-8000-000000000000",
                    killedPid,
                    startedAt,
                    0,
                    ChildSessionsOpenedByUs: false,
                    SessionState.Ready,
                    DateTimeOffset.UtcNow));

            holder.Kill(entireProcessTree: true);
            Assert.IsTrue(holder.WaitForExit(TimeSpan.FromSeconds(15)), "The killed holder did not die in time.");

            using SingleInstanceLock reacquired = SingleInstanceLock.Acquire(_lockName);
            Assert.AreEqual(
                LockAcquireOutcome.Acquired,
                reacquired.Outcome,
                "A force-killed owner must leave the object gone, so the next process gets a plain acquisition.");

            SessionOwnerRecord? leftover = store.Read();
            Assert.IsNotNull(leftover, "The stale record must survive the kill so the recovery rules can read it.");
            Assert.AreEqual(killedPid, leftover.HostPid, "The leftover must still name the process that died holding the lock.");

            DateTimeOffset now = DateTimeOffset.UtcNow;
            RecoveryEvidence evidence = new(
                RecordExists: true,
                OwnerProcessAlive: false,
                OwnerProcessStartTimeMatches: false,
                RecordedState: leftover.State,
                AcquiredAt: leftover.AcquiredAt,
                Now: now);

            Assert.AreEqual(
                RecoveryDecision.ManualInterventionRequired,
                SessionRecoveryPolicy.Evaluate(evidence),
                "A fresh record whose state still claims a live session must go to a human, not be cleaned up automatically.");

            Assert.AreEqual(
                RecoveryDecision.RecoverLeftovers,
                SessionRecoveryPolicy.Evaluate(evidence with { RecordedState = SessionState.Disconnected }),
                "The same dead owner with a terminal state may be recovered.");
        }
        finally
        {
            KillHelper(holder);
        }
    }

    /// <summary>
    /// A probe must report ownership without ever creating or occupying the object: a probe that used
    /// CreateMutex would briefly hold the machine-wide lock and, worse, would report an owner from then
    /// on. Proved against the real kernel object, including with a foreign owner.
    /// </summary>
    [TestMethod]
    public void ProbingOwnershipNeitherCreatesNorOccupiesTheLock()
    {
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "On a free machine the probe must report nobody.");
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "A second probe must agree; a probe that created the object would report an owner from now on.");

        using (SingleInstanceLock owned = SingleInstanceLock.Acquire(_lockName))
        {
            Assert.IsTrue(owned.IsHeld, "The lock must be free before this case can mean anything.");
            Assert.IsTrue(
                SingleInstanceLock.IsHeldByAnyone(_lockName),
                "A probe must see a lock held by this very process.");
        }

        using Process holder = StartHolderHolding(4000);
        try
        {
            Assert.IsTrue(
                SingleInstanceLock.IsHeldByAnyone(_lockName),
                "A probe must see a lock held by another real process.");
        }
        finally
        {
            KillHelper(holder);
        }

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "After the holder exits, the probe must report nobody again: probing must not leave the object alive.");
    }

    /// <summary>
    /// The recovery rules must tell a live owner apart from a reused process id. Both cases use a real
    /// process and a real record on disk; only the recorded start time differs.
    /// </summary>
    [TestMethod]
    public void RecoveryDistinguishesTheLiveOwnerFromAReusedProcessId()
    {
        SessionOwnerStore store = NewStore();
        using Process holder = StartHolderHolding(4000);
        try
        {
            int holderPid = holder.Id;
            DateTimeOffset realStartTime = new(holder.StartTime);
            DateTimeOffset otherStartTime = realStartTime.AddHours(-3);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            store.Write(
                new SessionOwnerRecord(
                    "b3f1c2d4-0000-4000-8000-000000000000",
                    holderPid,
                    realStartTime,
                    0,
                    ChildSessionsOpenedByUs: false,
                    SessionState.Disconnected,
                    now));
            SessionOwnerRecord? liveRecord = store.Read();
            Assert.IsNotNull(liveRecord, "The record of the live holder must be readable.");

            bool ownerAlive = !holder.HasExited;
            bool startTimeMatches = new DateTimeOffset(holder.StartTime) == liveRecord.HostProcessStartTime;
            Assert.IsTrue(ownerAlive && startTimeMatches, "The live holder must look exactly like the recorded owner.");

            Assert.AreEqual(
                RecoveryDecision.ManualInterventionRequired,
                SessionRecoveryPolicy.Evaluate(
                    new RecoveryEvidence(true, ownerAlive, startTimeMatches, liveRecord.State, liveRecord.AcquiredAt, now)),
                "A record whose owner process is still alive must never be taken over, even in a terminal state.");

            store.Write(
                new SessionOwnerRecord(
                    "b3f1c2d4-0000-4000-8000-000000000000",
                    holderPid,
                    otherStartTime,
                    0,
                    ChildSessionsOpenedByUs: false,
                    SessionState.Disconnected,
                    now));
            SessionOwnerRecord? reusedRecord = store.Read();
            Assert.IsNotNull(reusedRecord, "The record of the reused id must be readable.");

            bool reusedStartTimeMatches = new DateTimeOffset(holder.StartTime) == reusedRecord.HostProcessStartTime;
            Assert.IsFalse(reusedStartTimeMatches, "A start time that does not match the live process is the reused-id shape.");

            Assert.AreEqual(
                RecoveryDecision.RecoverLeftovers,
                SessionRecoveryPolicy.Evaluate(
                    new RecoveryEvidence(true, ownerAlive, reusedStartTimeMatches, reusedRecord.State, reusedRecord.AcquiredAt, now)),
                "When the recorded start time does not match the live process, the recorded owner is gone and the leftovers may be recovered.");
        }
        finally
        {
            KillHelper(holder);
        }
    }

    /// <summary>
    /// The probe reports whether the lock OBJECT exists, which is not the same as whether anybody owns
    /// the lock. A plain handle opened by an unrelated component keeps the object alive without owning
    /// anything, so the probe still reports an owner and the next session is refused. Both halves are
    /// pinned here so the trade-off stays visible instead of being assumed away.
    /// </summary>
    [TestMethod]
    public void ProbeReportsAnOwnerWhileOnlyAForeignHandleKeepsTheObjectAlive()
    {
        using Mutex foreignHandle = new(initiallyOwned: false, _lockName);

        Assert.IsTrue(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "A handle that owns nothing still keeps the object alive, so the probe reports an owner.");

        using (SingleInstanceLock blocked = SingleInstanceLock.Acquire(_lockName))
        {
            Assert.AreEqual(
                LockAcquireOutcome.HeldElsewhere,
                blocked.Outcome,
                "While the foreign handle is open, a session must be refused: this is the cost of the handle-existence model.");
        }

        foreignHandle.Dispose();
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "Once the foreign handle closes, the object is gone and the probe reports nobody.");

        using SingleInstanceLock free = SingleInstanceLock.Acquire(_lockName);
        Assert.AreEqual(
            LockAcquireOutcome.Acquired,
            free.Outcome,
            "After the foreign handle closes, a session must be able to start again without a restart.");
    }

    private SessionOwnerStore NewStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "scu-adversarial-" + Guid.NewGuid().ToString("N"));
        _temporaryRoots.Add(root);
        return new SessionOwnerStore(root, TestSid);
    }

    private static void WriteRawRecord(SessionOwnerStore store, string payload)
    {
        string directory = Path.GetDirectoryName(store.RecordPath)
            ?? throw new InvalidOperationException("The test record path has no directory part.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.RecordPath, payload);
    }

    /// <summary>
    /// Starts a helper that must take the lock, and fails loudly with the helper's own diagnostics when
    /// it cannot. A caller that leaves such a helper running would keep the machine-wide lock looking
    /// occupied for every later process, so callers must kill it before returning.
    /// </summary>
    private Process StartHolderHolding(int holdMilliseconds)
    {
        Process holder = StartHolder(holdMilliseconds);
        string? readiness = holder.StandardOutput.ReadLine();
        if (readiness != "held")
        {
            string error = holder.StandardError.ReadToEnd();
            KillHelper(holder);
            throw new InvalidOperationException(
                $"The lock holder did not take the lock: first line '{readiness}', stderr '{error}'.");
        }

        return holder;
    }


    private Process StartHolder(int holdMilliseconds)
    {
        string supportPath = Path.Combine(AppContext.BaseDirectory, "SoulComputerUse.TestSupport.exe");
        ProcessStartInfo startInfo = new()
        {
            FileName = supportPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--hold-lock");
        startInfo.ArgumentList.Add(_lockName);
        startInfo.ArgumentList.Add(holdMilliseconds.ToString(CultureInfo.InvariantCulture));

        Process started = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The lock holder process did not start.");

        // Tracked here rather than at each call site, so a failing assertion still gets the helper cleaned
        // up by the suite-level cleanup instead of leaving a holder behind for every later case.
        HelperProcessRegistry.Register(started);
        return started;
    }

    /// <summary>
    /// Stops a helper and proves it is really gone. A helper that refuses to exit keeps its lock handle
    /// open and makes later cases fail for the wrong reason, so that is reported as a failure here
    /// instead of being left to poison the next run.
    /// </summary>
    private static void KillHelper(Process helper)
    {
        if (!helper.HasExited)
        {
            helper.Kill(entireProcessTree: true);
            if (!helper.WaitForExit(HelperExitTimeoutMilliseconds))
            {
                helper.Kill(entireProcessTree: true);
                if (!helper.WaitForExit(HelperExitTimeoutMilliseconds))
                {
                    throw new InvalidOperationException(
                        $"Helper {helper.Id.ToString(CultureInfo.InvariantCulture)} would not exit after being killed twice, so it still holds a lock handle.");
                }
            }
        }
    }
}