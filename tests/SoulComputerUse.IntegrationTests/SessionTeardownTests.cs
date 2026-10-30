using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.SessionLifecycle;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Integration tests for the teardown execution layer against the real session table, the real kernel
/// lock and fake logoff and switch ports.
/// </summary>
/// <remarks>
/// Hard rule for this class: it never logs a real session off, never changes the machine-wide
/// child-session switch and never exits the process. The only real effects are reading the session
/// table and taking one private, per-run lock that is released again inside the case.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class SessionTeardownTests
{
    private const int ServiceSessionId = 0;
    private const int ChildSessionId = 15;

    /// <summary>The account the sweep's evidence names; the child sessions of this product run as it.</summary>
    private const string OperatorAccount = "operator";

    /// <summary>WTSDisconnected: the state a session holds when its client is gone but its logon is not.</summary>
    private const int DisconnectedState = LeftoverChildSessionPolicy.DisconnectedState;

    /// <summary>
    /// WTSActive: the state a session holds while a client is attached. A teardown may legitimately meet
    /// its own child session in this state, because nothing disconnects the client before the teardown.
    /// </summary>
    private const int ActiveState = 0;

    /// <summary>The sweep evidence of every case here: this account, and no recorded child session id.</summary>
    private static readonly LeftoverEvidence Evidence = new(0, OperatorAccount);

    private string _lockName = string.Empty;

    [TestInitialize]
    public void CreateIsolatedLockName()
    {
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
    }

    [TestCleanup]
    public void AssertNoLeftoverLock()
    {
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The case's own lock must be gone afterwards, otherwise a holder leaked.");
    }

    [TestMethod]
    public void TheRealSessionTableContainsTheCurrentProcessSessionAndTheConsoleIsKnown()
    {
        WtsSessionTable table = new();

        IReadOnlyList<WtsSessionEntry> sessions = table.Read();
        int currentSessionId = WtsSessionProbe.GetCurrentProcessSessionId();

        Assert.IsNotEmpty(sessions, "Enumerating the session table must return this machine's sessions.");
        Assert.IsTrue(
            ContainsSession(sessions, currentSessionId),
            "The session this test process runs in must appear in the session table, otherwise the enumeration is not reaching the real machine.");
        Assert.IsGreaterThan(
            0,
            table.ActiveConsoleSessionId(),
            "A machine that runs an interactive test session must report a positive console session id.");
    }

    [TestMethod]
    public void TheRealSessionTableAlwaysHoldsTheServiceSession()
    {
        IReadOnlyList<WtsSessionEntry> sessions = new WtsSessionTable().Read();

        Assert.IsTrue(
            ContainsSession(sessions, ServiceSessionId),
            "The service session is a fixed part of a Windows session table, so a table without it means the entries were read from the wrong offset.");
    }

    [TestMethod]
    public void TheRealSessionTableReadIsStableWithinOneRun()
    {
        WtsSessionTable table = new();

        IReadOnlyList<WtsSessionEntry> first = table.Read();
        IReadOnlyList<WtsSessionEntry> second = table.Read();

        Assert.HasCount(
            second.Count,
            first,
            "Enumerating twice without touching any session must return the same number of entries.");
        Assert.AreEqual(
            table.ActiveConsoleSessionId(),
            table.ActiveConsoleSessionId(),
            "The console session id must not change while the test does not change machine state.");
    }

    [TestMethod]
    public void ASweepWithNoLeftoverNeverCallsLogOff()
    {
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
            ]);

        LeftoverSweepReport report = new LeftoverSweeper(table, new ImmediateLogoffSettle()).Sweep(Evidence);

        Assert.IsEmpty(report.LoggedOffSessionIds, "Nothing of ours is left over, so nothing may be logged off.");
        Assert.IsEmpty(report.RemainingSessionIds);
        Assert.IsEmpty(table.LogOffCalls, "A sweep without a leftover must never ask the system to log a session off.");
        Assert.AreEqual(1, table.ReadCount, "One read is enough when there is nothing to remove.");
    }

    [TestMethod]
    public void ASweepLogsOneLeftoverOffAndConfirmsItIsGone()
    {
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
                new WtsSessionEntry(ChildSessionId, string.Empty, DisconnectedState, OperatorAccount),
            ]);

        LeftoverSweepReport report = new LeftoverSweeper(table, new ImmediateLogoffSettle()).Sweep(Evidence);

        CollectionAssert.AreEqual(new List<int> { ChildSessionId }, new List<int>(table.LogOffCalls));
        CollectionAssert.AreEqual(new List<int> { ChildSessionId }, new List<int>(report.LoggedOffSessionIds));
        Assert.IsEmpty(report.RemainingSessionIds);
        Assert.AreEqual(2, table.ReadCount, "The sweep must re-read the table after the logoff request, because a request is not a result.");
    }

    [TestMethod]
    public void ASweepLogsEveryLeftoverOffAndConfirmsEachOneIsGone()
    {
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
                new WtsSessionEntry(15, string.Empty, DisconnectedState, OperatorAccount),
                new WtsSessionEntry(22, string.Empty, DisconnectedState, OperatorAccount),
            ]);

        LeftoverSweepReport report = new LeftoverSweeper(table, new ImmediateLogoffSettle()).Sweep(Evidence);

        CollectionAssert.AreEqual(new List<int> { 15, 22 }, new List<int>(table.LogOffCalls));
        CollectionAssert.AreEqual(new List<int> { 15, 22 }, new List<int>(report.LoggedOffSessionIds));
        Assert.IsEmpty(report.RemainingSessionIds);
        Assert.AreEqual(2, table.ReadCount, "The sweep reads once to decide and once again to confirm the logoffs took effect.");
    }

    [TestMethod]
    public void ASweepRefusesBeforeItsFirstLogoffWhenASessionIsNeitherProvenOursNorProvenForeign()
    {
        // A person who closed the remote-desktop window while staying logged on looks exactly like this
        // entry, and so may this product's unmeasured leftover shape. The sweep may neither log it off
        // nor go past it, and it may not log anything else off on the way out either.
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4, null),
                new WtsSessionEntry(1, "Console", 0, OperatorAccount),
                new WtsSessionEntry(9, "RDP-Tcp#3", DisconnectedState, OperatorAccount),
                new WtsSessionEntry(15, string.Empty, DisconnectedState, OperatorAccount),
            ]);

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => new LeftoverSweeper(table, new ImmediateLogoffSettle()).Sweep(Evidence));

        StringAssert.StartsWith(
            error.Message,
            "manual_intervention_required:",
            "An undecidable session must be the manual-intervention refusal of the contract, never a warning and never a guess.");
        Assert.IsEmpty(
            table.LogOffCalls,
            "The sweep logged a session off even though another session could not be decided; a refusal must leave the machine exactly as it was found.");

        LeftoverSweepReport? attempt = error.Data[LeftoverSweeper.ReportDataKey] as LeftoverSweepReport;
        Assert.IsNotNull(attempt, "The refusal must carry the attempt's report, so the caller can see nothing was tried.");
        Assert.IsEmpty(attempt.LoggedOffSessionIds);
        Assert.IsEmpty(attempt.RemainingSessionIds);
    }

    [TestMethod]
    public void ASweepReReadsTheConsoleSessionBeforeEveryLogOffSoAFastUserSwitchCannotPoisonATarget()
    {
        // Between two logoff requests the operator may fast-switch the console onto a session the sweep
        // judged a moment earlier. A guard that checks the snapshot from before the first request would
        // clear the second target although it has become the operator's own desktop in the meantime.
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4, null),
                new WtsSessionEntry(1, "Console", 0, OperatorAccount),
                new WtsSessionEntry(15, string.Empty, DisconnectedState, OperatorAccount),
                new WtsSessionEntry(22, string.Empty, DisconnectedState, OperatorAccount),
            ])
        {
            ConsoleSessionIdAfterALogOff = 22,
        };

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => new LeftoverSweeper(table, new ImmediateLogoffSettle()).Sweep(Evidence));

        Assert.Contains(
            "active console session",
            error.Message,
            "The guard must refuse on the re-read console session id, not on the stale snapshot from before the first logoff.");
        CollectionAssert.AreEqual(
            new List<int> { 15 },
            new List<int>(table.LogOffCalls),
            "The first leftover may be logged off, but the session that became the console must never be requested.");
    }

    [TestMethod]
    public void ASweepRefusesToContinueWhenTheSessionSurvivesTheLogoffRequest()
    {
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
                new WtsSessionEntry(ChildSessionId, string.Empty, DisconnectedState, OperatorAccount),
            ])
        {
            SurvivesLogOff = true,
        };

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => new LeftoverSweeper(table, new ImmediateLogoffSettle()).Sweep(Evidence));

        StringAssert.StartsWith(
            error.Message,
            "manual_intervention_required:",
            "A leftover that survives the request must be the manual-intervention refusal of the contract, not a warning.");
        CollectionAssert.AreEqual(new List<int> { ChildSessionId }, new List<int>(table.LogOffCalls));

        LeftoverSweepReport? attempt = error.Data[LeftoverSweeper.ReportDataKey] as LeftoverSweepReport;
        Assert.IsNotNull(attempt, "The refusal must carry the attempted sweep's report, so the caller can see what was tried.");
        CollectionAssert.AreEqual(new List<int> { ChildSessionId }, new List<int>(attempt.LoggedOffSessionIds));
        CollectionAssert.AreEqual(new List<int> { ChildSessionId }, new List<int>(attempt.RemainingSessionIds));
    }

    [TestMethod]
    public void ASweepRaisesTheManualInterventionRefusalWhenTheLogOffItselfFails()
    {
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
                new WtsSessionEntry(ChildSessionId, string.Empty, DisconnectedState, OperatorAccount),
            ])
        {
            RefuseLogOff = true,
        };

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => new LeftoverSweeper(table, new ImmediateLogoffSettle()).Sweep(Evidence));

        StringAssert.StartsWith(error.Message, "manual_intervention_required:");
        Assert.AreEqual(1, table.ReadCount, "A refused request leaves nothing to verify, so the sweep must not claim a re-read happened.");
    }

    [TestMethod]
    public void ASweepNeverTouchesTheConsoleEvenWhenItsStationLooksLikeAChildSession()
    {
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, string.Empty, DisconnectedState, OperatorAccount),
            ]);

        LeftoverSweepReport report = new LeftoverSweeper(table, new ImmediateLogoffSettle()).Sweep(Evidence);

        Assert.IsEmpty(table.LogOffCalls, "The console session id always wins over the station name; signing the operator out is unrecoverable.");
        Assert.IsEmpty(report.LoggedOffSessionIds);
    }

    [TestMethod]
    public void TheSweepGuardRefusesTheServiceSessionTheConsoleAndNegativeIds()
    {
        // The policy already excludes these ids; this guard is the execution layer's own second line of
        // defence, so a future policy defect still cannot reach a real logoff call with them.
        Assert.ThrowsExactly<InvalidOperationException>(
            () => LogOffTargetGuard.EnsureRemovable(ServiceSessionId, activeConsoleSessionId: 1));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => LogOffTargetGuard.EnsureRemovable(1, activeConsoleSessionId: 1));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => LogOffTargetGuard.EnsureRemovable(-3, activeConsoleSessionId: 1));

        LogOffTargetGuard.EnsureRemovable(ChildSessionId, activeConsoleSessionId: 1);
    }

    [TestMethod]
    public void TheLogOffRefusesTheServiceSessionAndNegativeIdsWithoutCallingTheSystem()
    {
        WtsSessionTable table = new();
        WtsChildSessionLogOff port = new(table);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => table.LogOff(ServiceSessionId));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => table.LogOff(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => port.LogOff(ServiceSessionId));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => port.LogOff(-7));
    }

    [TestMethod]
    public void TheLogOffPortPassesAProvenTargetToTheTable()
    {
        // The shape a teardown normally meets: the child session the record names is disconnected — its
        // window is gone — and it is logged on as the account this process runs as, which is the proof
        // the port may act on.
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
                new WtsSessionEntry(ChildSessionId, string.Empty, DisconnectedState, OperatorAccount),
            ])
        {
            CurrentAccountName = OperatorAccount,
        };

        new WtsChildSessionLogOff(table).LogOff(ChildSessionId);

        CollectionAssert.AreEqual(new List<int> { ChildSessionId }, new List<int>(table.LogOffCalls));
    }

    [TestMethod]
    public void TheLogOffPortAlsoPassesAStillConnectedTargetTheRecordNames()
    {
        // The tearing-down process may itself still be the target's client — nothing disconnects the
        // control before the teardown runs — so a target that is still active is not a stranger's
        // session: the recorded id plus the same account is the proof, the state is not part of it.
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
                new WtsSessionEntry(ChildSessionId, "RDP-Tcp#7", ActiveState, OperatorAccount),
            ])
        {
            CurrentAccountName = OperatorAccount,
        };

        new WtsChildSessionLogOff(table).LogOff(ChildSessionId);

        CollectionAssert.AreEqual(new List<int> { ChildSessionId }, new List<int>(table.LogOffCalls));
    }

    [TestMethod]
    public void TheLogOffPortTreatsATargetThatAlreadyDisappearedAsSuccessWithoutCallingTheSystem()
    {
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries: [new WtsSessionEntry(0, "Services", 4), new WtsSessionEntry(1, "Console", 0)])
        {
            CurrentAccountName = OperatorAccount,
        };

        new WtsChildSessionLogOff(table).LogOff(ChildSessionId);

        Assert.IsEmpty(table.LogOffCalls, "A session the table no longer lists has already met the logoff's goal, so no request may be sent.");
    }

    [TestMethod]
    public void TheLogOffPortRefusesATargetThatBecameTheConsoleSession()
    {
        // The freed id was reused by a fast user switch and now names the operator's own desktop.
        FakeSessionTable table = new(
            consoleSessionId: ChildSessionId,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(ChildSessionId, "Console", 0, OperatorAccount),
            ])
        {
            CurrentAccountName = OperatorAccount,
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => new WtsChildSessionLogOff(table).LogOff(ChildSessionId));

        Assert.IsEmpty(table.LogOffCalls, "The current console session must never be the target of a logoff request.");
    }

    [TestMethod]
    public void TheLogOffPortRefusesATargetTheEvidenceCannotDecideAbout()
    {
        // The account query for the target was not answered, so neither ownership nor foreignness is
        // proven; the request must not guess in either direction.
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
                new WtsSessionEntry(ChildSessionId, string.Empty, DisconnectedState, null),
            ])
        {
            CurrentAccountName = OperatorAccount,
        };

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => new WtsChildSessionLogOff(table).LogOff(ChildSessionId));

        StringAssert.Contains(
            error.Message,
            ChildSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "The refusal names the session id, which carries no account, path or station name.");
        Assert.IsEmpty(table.LogOffCalls);
    }

    [TestMethod]
    public void TheLogOffPortRefusesATargetNowLoggedInAsAnotherAccount()
    {
        // The freed id was reused by another person's session; logging it off would destroy somebody
        // else's unsaved work, so the recorded id alone is not enough.
        FakeSessionTable table = new(
            consoleSessionId: 1,
            entries:
            [
                new WtsSessionEntry(0, "Services", 4),
                new WtsSessionEntry(1, "Console", 0),
                new WtsSessionEntry(ChildSessionId, string.Empty, DisconnectedState, "stranger"),
            ])
        {
            CurrentAccountName = OperatorAccount,
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => new WtsChildSessionLogOff(table).LogOff(ChildSessionId));

        Assert.IsEmpty(table.LogOffCalls, "A session of another account is never this product's child session, whatever id it reused.");
    }

    [TestMethod]
    public void TheSwitchRestoreTurnsTheSwitchOffAndReadsItBack()
    {
        FakeSettings settings = new(initiallyEnabled: true);

        new ChildSessionsSwitchRestore(settings).Restore();

        Assert.IsFalse(settings.Enabled, "The restore must ask for the switch to be turned off.");
        Assert.AreEqual(1, settings.ReadCount, "The write alone is not proof: the adapter must read the state back.");
    }

    [TestMethod]
    public void TheSwitchRestoreFailsWhenTheReadBackStillReportsItOn()
    {
        FakeSettings settings = new(initiallyEnabled: true)
        {
            AcceptWrites = false,
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => new ChildSessionsSwitchRestore(settings).Restore());

        Assert.AreEqual(1, settings.SetCalls, "The adapter must actually ask for the change before reporting a read-back problem.");
    }

    [TestMethod]
    public void TheSwitchRestoreReportsARefusedWriteAsAFailure()
    {
        FakeSettings settings = new(initiallyEnabled: true)
        {
            ThrowOnWrite = true,
        };

        Assert.ThrowsExactly<Win32Exception>(() => new ChildSessionsSwitchRestore(settings).Restore());
    }

    [TestMethod]
    public void TheLockReleaseClosesTheHandleAndConfirmsTheObjectIsGone()
    {
        SingleInstanceLock lockHandle = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(lockHandle.IsHeld, "The per-run lock must be acquired for this case to mean anything.");

        new SingleInstanceLockRelease(lockHandle, () => SingleInstanceLock.IsHeldByAnyone(_lockName)).Release();

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "After the release the lock object must be gone, so the next owner can take it.");
        using SingleInstanceLock reacquired = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(reacquired.IsHeld, "The released lock must be obtainable again.");
    }

    [TestMethod]
    public void TheLockReleaseRefusesAHandleThatNeverHeldTheLock()
    {
        using SingleInstanceLock owner = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(owner.IsHeld);

        using SingleInstanceLock refused = SingleInstanceLock.Acquire(_lockName);
        Assert.IsFalse(refused.IsHeld, "The nested acquisition must be refused for this case to mean anything.");

        Assert.ThrowsExactly<ArgumentException>(
            () => new SingleInstanceLockRelease(refused, () => SingleInstanceLock.IsHeldByAnyone(_lockName)));
        Assert.IsTrue(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The refused handle must not have closed the real owner's lock object.");
    }

    [TestMethod]
    public void TheLockReleaseFailsWhenTheObjectSurvivesTheClose()
    {
        SingleInstanceLock lockHandle = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(lockHandle.IsHeld);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => new SingleInstanceLockRelease(lockHandle, () => true).Release());
    }

    [TestMethod]
    public void APlanWithNoChildSessionIdFailsThatStepAndStillReleasesTheLock()
    {
        SingleInstanceLock lockHandle = SingleInstanceLock.Acquire(_lockName);
        CountingStateTransition transition = new();
        CountingLogOff logOff = new();
        CountingSwitch childSessionsSwitch = new();
        SessionTeardownExecutor executor = new(
            transition,
            logOff,
            childSessionsSwitch,
            new SingleInstanceLockRelease(lockHandle, () => SingleInstanceLock.IsHeldByAnyone(_lockName)));

        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        SessionTeardownReport report = executor
            .ExecuteAsync(
                plan,
                new SessionTeardownContext(
                    ChildWtsSessionId: null,
                    ChildSessionsOpenedByUs: false,
                    LockIsHeld: true,
                    ProcessExitRequested: true),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert.AreEqual(TeardownFailureReason.MissingChildSession, Find(report, SessionShutdownStep.LogOffChildSession).Failure);
        Assert.IsTrue(
            Find(report, SessionShutdownStep.ReleaseLock).Succeeded,
            "The lock must still be released after a failed logoff step.");
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The release step must have closed the real lock handle.");
        Assert.IsEmpty(logOff.CalledIds, "Without a child session id the logoff port must never be reached.");
        Assert.AreEqual(0, childSessionsSwitch.CallCount, "The switch was not opened by us, so it must not be touched.");
        Assert.AreEqual(SessionLifecycleExitCodes.Failure, report.ExitCode);
    }

    [TestMethod]
    public void ARealPlanLogsTheExactChildSessionIdOffAndThenReleasesTheLock()
    {
        SingleInstanceLock lockHandle = SingleInstanceLock.Acquire(_lockName);
        CountingLogOff logOff = new();
        CountingSwitch childSessionsSwitch = new();
        SessionTeardownExecutor executor = new(
            new CountingStateTransition(),
            logOff,
            childSessionsSwitch,
            new SingleInstanceLockRelease(lockHandle, () => SingleInstanceLock.IsHeldByAnyone(_lockName)));

        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        SessionTeardownReport report = executor
            .ExecuteAsync(
                plan,
                new SessionTeardownContext(
                    ChildWtsSessionId: 4242,
                    ChildSessionsOpenedByUs: false,
                    LockIsHeld: true,
                    ProcessExitRequested: true),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        CollectionAssert.AreEqual(new List<int> { 4242 }, new List<int>(logOff.CalledIds));
        Assert.IsFalse(report.AnyStepFailed);
        Assert.AreEqual(SessionLifecycleExitCodes.Success, report.ExitCode);
        Assert.AreEqual(0, childSessionsSwitch.CallCount);
    }

    private static bool ContainsSession(IReadOnlyList<WtsSessionEntry> sessions, int sessionId)
    {
        foreach (WtsSessionEntry session in sessions)
        {
            if (session.Id == sessionId)
            {
                return true;
            }
        }

        return false;
    }

    private static SessionTeardownStepResult Find(SessionTeardownReport report, SessionShutdownStep step)
    {
        foreach (SessionTeardownStepResult result in report.Steps)
        {
            if (result.Step == step)
            {
                return result;
            }
        }

        throw new AssertFailedException($"The report has no result for the step '{step}'.");
    }

    /// <summary>
    /// A session table that never reaches the system. It records every logoff request instead, and it can
    /// pretend that a requested logoff was accepted but did not take effect.
    /// </summary>
    private sealed class FakeSessionTable : IWtsSessionTable
    {
        private readonly List<WtsSessionEntry> _entries;

        internal FakeSessionTable(int consoleSessionId, IReadOnlyList<WtsSessionEntry> entries)
        {
            ConsoleSessionId = consoleSessionId;
            _entries = new List<WtsSessionEntry>(entries);
        }

        /// <summary>
        /// The console session id the fake reports right now, so a case can move the console between two
        /// reads the way a fast user switch does.
        /// </summary>
        internal int ConsoleSessionId { get; set; }

        /// <summary>The console session id the fake switches to after any logoff request, if a case sets one.</summary>
        internal int? ConsoleSessionIdAfterALogOff { get; init; }

        internal List<int> LogOffCalls { get; } = [];

        internal int ReadCount { get; private set; }

        internal bool SurvivesLogOff { get; init; }

        internal bool RefuseLogOff { get; init; }

        public IReadOnlyList<WtsSessionEntry> Read()
        {
            ReadCount++;
            return _entries.AsReadOnly();
        }

        public void LogOff(int sessionId)
        {
            LogOffCalls.Add(sessionId);
            if (ConsoleSessionIdAfterALogOff is int switched)
            {
                ConsoleSessionId = switched;
            }

            if (RefuseLogOff)
            {
                throw new Win32Exception(5, "The session table refused the logoff request.");
            }

            if (!SurvivesLogOff)
            {
                _entries.RemoveAll(entry => entry.Id == sessionId);
            }
        }

        public int ActiveConsoleSessionId()
        {
            return ConsoleSessionId;
        }

        /// <summary>
        /// The account this fake answers for the process's own session. The sweep's cases name the
        /// account in their evidence instead, so the fake keeps answering nothing until a case sets it.
        /// </summary>
        public string? CurrentSessionAccountName()
        {
            return CurrentAccountName;
        }

        /// <summary>The account name <see cref="CurrentSessionAccountName"/> answers; null until a case sets one.</summary>
        internal string? CurrentAccountName { get; set; }
    }

    /// <summary>
    /// The wait a logoff confirmation uses in a test: it returns at once, so the sweep's confirmation loop
    /// runs without spending the production timeout.
    /// </summary>
    private sealed class ImmediateLogoffSettle : ILogoffSettle
    {
        public void Wait(TimeSpan interval)
        {
            Assert.IsGreaterThan(TimeSpan.Zero, interval, "The sweep must only ever ask for a positive interval.");
        }
    }

    /// <summary>A machine-wide switch that records the requested state instead of changing it.</summary>
    private sealed class FakeSettings : IChildSessionsSetting
    {
        internal FakeSettings(bool initiallyEnabled)
        {
            Enabled = initiallyEnabled;
        }

        internal bool Enabled { get; private set; }

        internal int ReadCount { get; private set; }

        internal int SetCalls { get; private set; }

        internal bool AcceptWrites { get; init; } = true;

        internal bool ThrowOnWrite { get; init; }

        public bool IsEnabled()
        {
            ReadCount++;
            return Enabled;
        }

        public void SetEnabled(bool enabled)
        {
            SetCalls++;
            if (ThrowOnWrite)
            {
                throw new Win32Exception(5, "The system refused the child-session change.");
            }

            if (AcceptWrites)
            {
                Enabled = enabled;
            }
        }
    }

    private sealed class CountingStateTransition : ISessionStateTransition
    {
        public void EnterClosing()
        {
        }
    }

    private sealed class CountingLogOff : IChildSessionLogOff
    {
        internal List<int> CalledIds { get; } = [];

        public void LogOff(int wtsSessionId)
        {
            CalledIds.Add(wtsSessionId);
        }
    }

    private sealed class CountingSwitch : IChildSessionsSwitch
    {
        internal int CallCount { get; private set; }

        public void Restore()
        {
            CallCount++;
        }
    }
}
