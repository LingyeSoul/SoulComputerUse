using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.Hosting.Startup;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.Rdp.Launch;
using SoulComputerUse.Windows.Rdp.SessionLaunch;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the two segments that replace the single-pass start, for the connection opener and
/// for the wait that proves the operator logged on.
/// </summary>
/// <remarks>
/// Every port here is a fake, so a case never takes a real lock, never reads a real ownership record,
/// never touches a policy value, never prompts for elevation and never opens a connection. What the cases
/// pin is the order: the split only earns its existence if the agent is attached strictly after the
/// connection, and if every refusal leaves the machine exactly as it found it.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class SessionStartSplitTests
{
    private const string SessionId = "b3f1c2d4-0000-4000-8000-000000000000";
    private const string Nonce = "0123456789ABCDEF0123456789ABCDEF";
    private const int AgentProcessId = 4242;
    private const int ChildSessionId = 15;

    private const string RdpInboundPolicyCall = "gate.policy:fDenyTSConnections";
    private const string ChildConnectionsPolicyCall = "gate.policy:fDenyChildConnections";

    private static readonly DateTimeOffset HostStartTime = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AcquiredAt = new(2026, 10, 26, 8, 0, 1, TimeSpan.Zero);

    [TestMethod]
    public void TheBeforeConnectSegmentRunsEveryStageInTheContractOrder()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Gate.Enabled = true;

        BeforeConnectResult result = fixture.Segment.Execute();

        CollectionAssert.AreEqual(
            new List<string>
            {
                "lock",
                "record.read",
                "record.write:starting:0:False",
                "tier",
                "sweep",
                "gate.probe",
                RdpInboundPolicyCall,
                ChildConnectionsPolicyCall,
                "launch.start-program",
                "launch.working-directory",
            },
            fixture.Calls,
            "The before-connect segment must take the lock, judge the record, write starting, probe the tier, sweep, settle the switch and only then write the alternate shell.");

        Assert.IsTrue(result.SweepCompleted);
        Assert.IsFalse(result.ChildSessionsOpenedByUs, "A switch that was already on is not ours to turn off later.");
        Assert.IsTrue(result.AlternateShellConfigured);
        Assert.AreSame(fixture.Handle, result.LockHandle, "A completed segment hands its lock handle to the caller.");
        Assert.IsFalse(fixture.Handle.Disposed, "A completed segment keeps the lock it took.");
        Assert.AreEqual(AuthTier.SystemCredential, fixture.Segment.AuthTier);
        Assert.AreEqual(0, fixture.Gate.EnableCalls, "An already-on switch must never be touched.");

        AgentLaunchRequest request = CreateLaunchRequest();
        Assert.AreEqual(
            AgentLaunchCommandLine.Build(request.ExecutablePath, request.SessionId, request.Nonce, request.AuthTier, request.GateStrength),
            ChildSessionAgentBootstrap.Read(),
            "A completed segment leaves the start-up entry mounted with the same command line the alternate-shell property carries, because a child session ignores StartProgram and its shell starts the agent from this entry.");

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
                SessionStartStage.WriteStartingRecord,
                SessionStartStage.AuthTier,
                SessionStartStage.Sweep,
                SessionStartStage.ChildSessions,
            },
            Stages(fixture.Segment.SessionStartSteps));

        Assert.AreEqual(1, fixture.Records.WriteCount, "A start that did not enable the switch rewrites no record.");
    }

    [TestMethod]
    public void AStartThatEnabledTheSwitchRewritesTheRecordWithTheMarkerAfterTheElevation()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Gate.Enabled = false;

        BeforeConnectResult result = fixture.Segment.Execute();

        CollectionAssert.AreEqual(
            new List<string>
            {
                "lock",
                "record.read",
                "record.write:starting:0:False",
                "tier",
                "sweep",
                "gate.probe",
                RdpInboundPolicyCall,
                ChildConnectionsPolicyCall,
                "gate.enable",
                "gate.probe",
                "record.write:starting:0:True",
                "launch.start-program",
                "launch.working-directory",
            },
            fixture.Calls,
            "The marker may only be written after the elevation was read back as enabled, and only when this session enabled the switch.");

        Assert.IsTrue(result.ChildSessionsOpenedByUs);
        Assert.AreEqual(1, fixture.Gate.EnableCalls, "The switch must be enabled exactly once.");
        Assert.AreEqual(
            SessionStartStage.RecordChildSessions,
            fixture.Segment.SessionStartSteps[^1].Stage,
            "The marked record must be the last stage before the alternate shell, which has no stage of its own.");
    }

    [TestMethod]
    public void TheNoneTierRefusesReleasesTheLockAndDeletesTheRecordItWrote()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Tier.Tier = AuthTier.None;

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.ManualIntervention, error.Code, error.Message);
        StringAssert.Contains(error.Message, "verification", "The refusal must say that no verification path exists.");
        Assert.IsTrue(fixture.Handle.Disposed, "A refused start must close the lock handle it took.");
        Assert.AreEqual(1, fixture.Records.DeleteCount, "A refused start must delete the record it wrote.");
        Assert.IsFalse(fixture.Records.Exists, "No record may outlive the lock that made it authoritative.");

        Assert.AreEqual(0, fixture.Sweep.Count, "The none tier refuses before anything is swept.");
        Assert.AreEqual(0, fixture.Gate.ProbeCount, "A refused tier must not read the machine's switch.");
        Assert.AreEqual(0, fixture.Gate.EnableCalls);
        Assert.IsEmpty(fixture.Launch.Writes, "A refused start must never write the alternate-shell properties.");
        Assert.IsNull(ChildSessionAgentBootstrap.Read(), "A refused start must leave no start-up entry behind: the tier refuses before the entry is mounted.");
        Assert.IsNull(fixture.Segment.AuthTier, "A refused tier is not reported as a probed one.");

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
                SessionStartStage.WriteStartingRecord,
                SessionStartStage.AuthTier,
            },
            Stages(fixture.Segment.SessionStartSteps));
    }

    [TestMethod]
    public void AFailedSweepRefusesWithManualInterventionAndRunsNothingAfterIt()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Sweep.Failure = new InvalidOperationException(
            "manual_intervention_required: a child session survived the logoff request.");

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.ManualIntervention, error.Code, error.Message);
        Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
        Assert.AreEqual(0, fixture.Gate.ProbeCount, "A failed sweep must stop before the switch is read.");
        Assert.AreEqual(0, fixture.Gate.EnableCalls);
        Assert.IsEmpty(fixture.Launch.Writes, "A failed sweep must never reach the alternate-shell write.");
        Assert.IsTrue(fixture.Handle.Disposed);
        Assert.IsFalse(fixture.Records.Exists);

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
                SessionStartStage.WriteStartingRecord,
                SessionStartStage.AuthTier,
                SessionStartStage.Sweep,
            },
            Stages(fixture.Segment.SessionStartSteps));
    }

    [TestMethod]
    public void ADeclinedElevationRefusesWithItsOwnCodeAndCleansUp()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Gate.Enabled = false;
        fixture.Gate.Elevation = new ElevationResult(false, ElevationFailure.Declined, 1223);

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.ChildSessionsDeclined, error.Code, error.Message);
        Assert.IsTrue(fixture.Handle.Disposed);
        Assert.IsFalse(fixture.Records.Exists);
        Assert.IsEmpty(fixture.Launch.Writes);
        Assert.AreEqual(
            SessionStartStage.EnableChildSessions,
            fixture.Segment.SessionStartSteps[^1].Stage);
        Assert.IsFalse(fixture.Segment.SessionStartSteps[^1].Succeeded);
    }

    [TestMethod]
    public void AnUnavailableElevationRefusesWithItsOwnCode()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Gate.Enabled = false;
        fixture.Gate.Elevation = new ElevationResult(false, ElevationFailure.Unavailable, 2);

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.ElevationUnavailable, error.Code, error.Message);
        Assert.IsTrue(fixture.Handle.Disposed);
        Assert.IsFalse(fixture.Records.Exists);
        Assert.IsEmpty(fixture.Launch.Writes);
    }

    [TestMethod]
    public void AHelperFailureRefusesAsChildSessionsDisabled()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Gate.Enabled = false;
        fixture.Gate.Elevation = new ElevationResult(false, ElevationFailure.HelperFailed, 3);

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.ChildSessionsDisabled, error.Code, error.Message);
        Assert.IsTrue(fixture.Handle.Disposed);
        Assert.IsFalse(fixture.Records.Exists);
        Assert.IsEmpty(fixture.Launch.Writes);
    }

    [TestMethod]
    public void AHelperThatReportedSuccessButLeftTheSwitchOffIsRefused()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Gate.Enabled = false;
        fixture.Gate.EnablesOnSuccess = false;

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.ChildSessionsDisabled, error.Code, error.Message);
        Assert.AreEqual(1, fixture.Records.WriteCount, "The marker must not be written when the read-back says the switch is off.");
        Assert.IsTrue(fixture.Handle.Disposed);
        Assert.IsFalse(fixture.Records.Exists);
    }

    [TestMethod]
    public void AHeldLockRefusesBeforeTheRecordIsRead()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Lock.HeldElsewhere = true;

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.LockHeld, error.Code, error.Message);
        Assert.AreEqual(0, fixture.Records.ReadCount, "A held lock must be refused before the record is read.");
        Assert.AreEqual(0, fixture.Records.DeleteCount, "This attempt wrote nothing, so there is nothing to delete.");
        Assert.IsEmpty(fixture.Segment.SessionStartSteps, "A refused lock attempt executes no stage.");
    }

    [TestMethod]
    public void ARecordThatCannotBeTakenOverRefusesAndIsNeverDeleted()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Records.ReadStatus = OwnerRecordStatus.NotTakeable;

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.LockStaleRecord, error.Code, error.Message);
        Assert.AreEqual(0, fixture.Records.DeleteCount, "A record that fails the takeover criteria is evidence, not garbage.");
        Assert.AreEqual(0, fixture.Records.WriteCount);
        Assert.IsTrue(fixture.Handle.Disposed);
        Assert.AreEqual(0, fixture.Sweep.Count);
    }

    [TestMethod]
    public void ATakeOverRecordIsDeletedBeforeTheStartingRecordReplacesIt()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Records.ReadStatus = OwnerRecordStatus.TakeOver;

        BeforeConnectResult result = fixture.Segment.Execute();

        CollectionAssert.AreEqual(
            new List<string>
            {
                "lock",
                "record.read",
                "record.delete",
                "record.write:starting:0:False",
                "tier",
                "sweep",
                "gate.probe",
                RdpInboundPolicyCall,
                ChildConnectionsPolicyCall,
                "launch.start-program",
                "launch.working-directory",
            },
            fixture.Calls,
            "A takeover deletes the leftover before the replacement is written, never after.");

        Assert.IsTrue(result.SweepCompleted);
        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
                SessionStartStage.TakeOverRecord,
                SessionStartStage.WriteStartingRecord,
                SessionStartStage.AuthTier,
                SessionStartStage.Sweep,
                SessionStartStage.ChildSessions,
            },
            Stages(fixture.Segment.SessionStartSteps));
    }

    [TestMethod]
    public void ADeniedPolicyValueRefusesBeforeElevationAndNamesTheValue()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Gate.Enabled = false;
        fixture.Gate.Policy[PolicyValue.RdpInbound] = PolicyState.Denied;

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.RdpInboundDenied, error.Code, error.Message);
        StringAssert.Contains(error.Message, ChildSessionsPolicy.RdpInboundValueName);
        Assert.AreEqual(0, fixture.Gate.EnableCalls, "A denied policy refuses before consent is asked for.");
        Assert.IsEmpty(fixture.Launch.Writes);
        Assert.IsTrue(fixture.Handle.Disposed);
    }

    [TestMethod]
    public void AnAlternateShellWriteThatDidNotStickRefusesAndCleansUp()
    {
        using BeforeConnectFixture fixture = new();
        fixture.Launch.IgnoreWrites = true;

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.ManualIntervention, error.Code, error.Message);
        Assert.IsTrue(fixture.Handle.Disposed, "A start that never configured the shell must not keep the lock.");
        Assert.IsFalse(fixture.Records.Exists);
    }

    [TestMethod]
    public void ASecondRunOfTheBeforeConnectSegmentIsRefused()
    {
        using BeforeConnectFixture fixture = new();

        fixture.Segment.Execute();

        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Segment.Execute());
    }

    [TestMethod]
    public void TheAfterLogonSegmentRunsEveryStageInTheContractOrder()
    {
        AfterLogonFixture fixture = new();

        AfterLogonResult result = fixture.Segment.Execute();

        CollectionAssert.AreEqual(
            new List<string>
            {
                "agent.connect",
                "session.lookup:" + AgentProcessId,
                "record.write:starting:" + ChildSessionId + ":False",
                "pipe.start",
                "record.write:connecting:" + ChildSessionId + ":False",
                "record.write:ready:" + ChildSessionId + ":False",
            },
            fixture.Calls,
            "The after-logon segment must attach the agent, record its Windows session, start the owner pipe and only then report ready.");

        Assert.IsTrue(result.AgentAttached);
        Assert.IsTrue(result.OwnerPipeStarted);
        Assert.AreEqual(ChildSessionId, result.SubagentWtsSessionId);
        Assert.IsTrue(result.RecordReady);
        Assert.IsNotNull(fixture.Segment.AgentConnection);
        Assert.AreSame(
            fixture.Agent.Connection,
            fixture.Segment.AgentConnection!.Client,
            "The segment must expose the connection the connector handed it, so the caller can answer tool calls.");

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.ConnectAgent,
                SessionStartStage.WriteSubagentSession,
                SessionStartStage.OwnerPipe,
                SessionStartStage.WriteConnecting,
                SessionStartStage.WriteReady,
            },
            Stages(fixture.Segment.SessionStartSteps));
    }

    [TestMethod]
    public void AFailedAgentAttachmentNeverStartsTheOwnerPipeAndLeavesTheLockAlone()
    {
        AfterLogonFixture fixture = new();
        fixture.Agent.Outcome = AgentAttachOutcome.Failed(SessionErrorCode.AgentTimeout);

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code, error.Message);
        Assert.AreEqual(0, fixture.Pipe.Count, "The owner pipe must not start when no agent can serve calls.");
        Assert.AreEqual(0, fixture.SessionLookup.Count, "No child session may be recorded without an attachment.");
        Assert.AreEqual(1, fixture.Records.DeleteCount, "The record the start wrote must be deleted.");
        Assert.IsFalse(fixture.Records.Exists);
        Assert.IsFalse(
            fixture.Handle.Disposed,
            "The lock belongs to the caller, so a failed after-logon segment must never close it.");
        Assert.IsNull(fixture.Segment.AgentConnection, "A failed segment exposes no connection to call.");
    }

    [TestMethod]
    public void AnAgentThatThrowsIsReportedAsDisconnected()
    {
        AfterLogonFixture fixture = new();
        fixture.Agent.Failure = new InvalidOperationException("the pipe never appeared");

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.Disconnected, error.Code, error.Message);
        Assert.AreEqual(0, fixture.Pipe.Count);
        Assert.IsFalse(fixture.Records.Exists);
        Assert.IsFalse(fixture.Handle.Disposed);
    }

    [TestMethod]
    public void AnAgentSessionThatCannotBeReadFailsAsAgentDeadAndClosesTheConnection()
    {
        AfterLogonFixture fixture = new();
        fixture.SessionLookup.Failure = new InvalidOperationException("the process exited");

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.AgentDead, error.Code, error.Message);
        Assert.AreEqual(0, fixture.Pipe.Count);
        Assert.IsTrue(fixture.Agent.Connection!.Disposed, "A failed segment must close the agent connection it opened.");
        Assert.IsFalse(fixture.Records.Exists);
        Assert.IsFalse(fixture.Handle.Disposed);
    }

    [TestMethod]
    public void AnOwnerPipeThatDoesNotStartDeletesTheRecordAndKeepsTheLock()
    {
        AfterLogonFixture fixture = new();
        fixture.Pipe.Failure = new InvalidOperationException("the pipe never accepted a probe");

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => fixture.Segment.Execute());

        Assert.AreEqual(SessionErrorCode.ManualIntervention, error.Code, error.Message);
        Assert.IsFalse(fixture.Records.Exists, "A session that never served calls must not leave a record claiming it did.");
        Assert.IsTrue(fixture.Agent.ConnectionClosed, "The agent connection must be closed by the failing segment.");
        Assert.IsFalse(fixture.Handle.Disposed, "The lock stays with the caller after a failed after-logon segment.");
        Assert.AreEqual(
            SessionStartStage.OwnerPipe,
            fixture.Segment.SessionStartSteps[^1].Stage,
            "The failed stage must be the owner pipe, which is the step that refused.");
    }

    [TestMethod]
    public void ASecondRunOfTheAfterLogonSegmentIsRefused()
    {
        AfterLogonFixture fixture = new();

        fixture.Segment.Execute();

        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Segment.Execute());
    }

    [TestMethod]
    public void TheWaiterAcceptsOnlyAChainThatShowedTheLogonScreenFirst()
    {
        RecordingWaitClock clock = new();
        SessionConnectionWaiter waiter = CreateWaiter([2, 3, 1], clock);

        waiter.Await();

        Assert.IsTrue(waiter.SawAwaitingCredentials, "The logon screen was reported, so the chain is the only acceptable one.");
        Assert.AreEqual(2, clock.Waits, "The waiter waits between observations and never after the last one.");
        CollectionAssert.AreEqual(
            new List<string> { "wait", "wait" },
            new List<string>(clock.Calls),
            "The waiter must use the injected clock instead of sleeping on its own.");
    }

    [TestMethod]
    public void AConnectionThatSkippedTheLogonScreenIsAcceptedWhenAChildSessionHasAUser()
    {
        RecordingWaitClock clock = new();
        Queue<int> remaining = new([2, 1]);
        SessionConnectionWaiter waiter = new(
            () => remaining.Dequeue(),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(500),
            clock,
            new ScriptedLogonEvidence(createdSessionId: 15, hasCreatedSession: true, [true]));

        waiter.Await();

        Assert.IsFalse(waiter.SawAwaitingCredentials, "The client never painted the session's own logon screen.");
        Assert.IsTrue(
            waiter.SawLogonThroughSessionUser,
            "The session's user name is the remaining proof that Windows accepted a logon, so the gate must accept it.");
    }

    [TestMethod]
    public void AConnectedClientKeepsWaitingWhileItsOwnChildSessionShowsTheLogonScreen()
    {
        RecordingWaitClock clock = new();
        Queue<int> remaining = new([2, 1, 1, 1, 1, 1]);
        SessionConnectionWaiter waiter = new(
            () => remaining.Dequeue(),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(10),
            clock,
            new ScriptedLogonEvidence(createdSessionId: 15, hasCreatedSession: true, [false, false, false, true]));

        waiter.Await();

        Assert.IsTrue(
            waiter.SawLogonScreenInCreatedSession,
            "A session of this run with no user name yet is sitting on its own Windows logon screen, which the client reports as connected.");
        Assert.IsTrue(
            waiter.SawLogonThroughSessionUser,
            "The wait must end when Windows publishes the user of the session this run created.");
        Assert.IsFalse(waiter.SawAwaitingCredentials, "This machine's client never reports the logon screen as state 3.");
    }

    [TestMethod]
    public void AConnectionThatSkippedTheLogonScreenIsRefusedAsAStateViolation()
    {
        RecordingWaitClock clock = new();
        Queue<int> remaining = new([2, 1, 1]);
        SessionConnectionWaiter waiter = new(
            () => remaining.Dequeue(),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(500),
            clock,
            new ScriptedLogonEvidence(createdSessionId: 0, hasCreatedSession: false, [false, false]),
            TimeSpan.FromMilliseconds(100));

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => waiter.Await());

        Assert.AreEqual(SessionErrorCode.State, error.Code, error.Message);
        StringAssert.Contains(
            error.Message,
            "logon",
            "The refusal must name the logon step that was bypassed, because that is what the operator has to investigate.");
        Assert.IsFalse(waiter.SawAwaitingCredentials);
        Assert.AreEqual(
            2,
            clock.Waits,
            "The grace period is one observation, so the refusal happens on the second connected observation.");
    }

    [TestMethod]
    public void AConnectionThatDroppedIsRefusedAsDisconnected()
    {
        RecordingWaitClock clock = new();
        SessionConnectionWaiter waiter = CreateWaiter([2, 0], clock);

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => waiter.Await());

        Assert.AreEqual(SessionErrorCode.Disconnected, error.Code, error.Message);
        Assert.AreEqual(1, clock.Waits, "The drop is only a drop after the client began connecting.");
    }

    [TestMethod]
    public void AFirstIdleObservationIsNotReadAsADrop()
    {
        RecordingWaitClock clock = new();
        SessionConnectionWaiter waiter = CreateWaiter([0, 2, 3, 1], clock);

        waiter.Await();

        Assert.IsTrue(
            waiter.SawAwaitingCredentials,
            "The client reports idle until its transport starts, so the first idle must not end the wait.");
    }

    [TestMethod]
    public void AConnectionThatNeverArrivesTimesOutAsAnAgentTimeout()
    {
        RecordingWaitClock clock = new();
        SessionConnectionWaiter waiter = CreateWaiter([2, 2, 2, 2, 2, 2, 2], clock);

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(() => waiter.Await());

        Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code, error.Message);
        Assert.AreEqual(4, clock.Waits, "A five-observation timeout waits between the observations only.");
    }

    [TestMethod]
    public void AConnectedStateTheClientNeverProducesIsRefusedInsteadOfGuessed()
    {
        RecordingWaitClock clock = new();
        SessionConnectionWaiter waiter = CreateWaiter([9], clock);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => waiter.Await());
    }

    [TestMethod]
    public void TheWaiterNeedsAPositiveIntervalAndTimeout()
    {
        RecordingWaitClock clock = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionConnectionWaiter(() => 1, TimeSpan.Zero, TimeSpan.FromSeconds(1), clock));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionConnectionWaiter(() => 1, TimeSpan.FromSeconds(1), TimeSpan.Zero, clock));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SessionConnectionWaiter(null!, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), clock));
    }

    [TestMethod]
    public void AnOpenerThatCannotJudgeTheGateNeverAsksTheControlToConnect()
    {
        FakeControlSurface control = new();
        FakeSessionConnector connector = new();
        connector.GateFailure = new InvalidOperationException("the policy value could not be read");
        SessionConnectionOpener opener = new(control, connector, 1920, 1080);

        Assert.ThrowsExactly<InvalidOperationException>(() => opener.Open());

        Assert.AreEqual(1, connector.PrepareGateCalls);
        Assert.AreEqual(0, connector.OpenCalls, "A refused gate must leave the control untouched.");
    }

    [TestMethod]
    public void TheOpenerPassesTheClientSizeAndTheJudgedGateToTheConnector()
    {
        FakeControlSurface control = new();
        FakeSessionConnector connector = new();
        SessionConnectionOpener opener = new(control, connector, 1920, 1080);

        opener.Open();

        Assert.AreEqual(1, connector.OpenCalls);
        Assert.AreSame(control.Ocx, connector.LastOcx);
        Assert.AreEqual(1920, connector.LastWidth);
        Assert.AreEqual(1080, connector.LastHeight);
        Assert.AreSame(connector.Gate, connector.LastGate);
    }

    [TestMethod]
    public void TheOpenerRefusesAnUnusableDesktopSize()
    {
        FakeControlSurface control = new();
        FakeSessionConnector connector = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionConnectionOpener(control, connector, 0, 1080));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionConnectionOpener(control, connector, 1920, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionConnectionOpener(control, connector, 100_000, 1080));
    }

    private static SessionConnectionWaiter CreateWaiter(int[] states, IWaitClock clock)
    {
        Queue<int> remaining = new(states);
        return new SessionConnectionWaiter(
            () => remaining.Count > 0
                ? remaining.Dequeue()
                : throw new InvalidOperationException("The case ran out of scripted connected states."),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(500),
            clock);
    }

    private static List<SessionStartStage> Stages(IReadOnlyList<SessionStartStepResult> steps)
    {
        List<SessionStartStage> stages = new(steps.Count);
        foreach (SessionStartStepResult step in steps)
        {
            stages.Add(step.Stage);
        }

        return stages;
    }

    private static AgentLaunchRequest CreateLaunchRequest()
    {
        string executablePath = Path.Combine(Path.GetTempPath(), AgentExecutablePath.FileName);
        string workingDirectory = Path.GetTempPath();
        return new AgentLaunchRequest(executablePath, workingDirectory, SessionId, Nonce, AuthTier: null, GateStrength: null);
    }

    /// <summary>
    /// The before-connect segment plus the fakes it is composed from and the call log of one case.
    /// </summary>
    /// <remarks>
    /// The segment mounts the real start-up entry in the current user's <c>Run</c> key, so the fixture
    /// starts from a removed entry and removes it again when the case ends: a test that leaked the entry
    /// would start an agent on this account's next logon.
    /// </remarks>
    private sealed class BeforeConnectFixture : IDisposable
    {
        internal BeforeConnectFixture()
        {
            ChildSessionAgentBootstrap.Remove();
            Calls = [];
            Handle = new TrackingHandle(Calls);
            Lock = new FakeLockAcquire(Handle, Calls);
            Records = new FakeOwnerRecordStore(Calls);
            Tier = new FakeAuthTierSource(Calls);
            Sweep = new FakeSweep(Calls);
            Gate = new FakeChildSessionsGate(Calls);
            Launch = new RecordingLaunchSurface(Calls);
            Control = new FakeControlSurface();
            Segment = new BeforeConnectSegment(
                new SessionStartOptions(SessionId, Environment.ProcessId, HostStartTime, AcquiredAt),
                Lock,
                Records,
                Tier,
                Sweep,
                Gate,
                CreateLaunchRequest(),
                Control,
                Launch);
        }

        public void Dispose()
        {
            ChildSessionAgentBootstrap.Remove();
        }

        internal List<string> Calls { get; }

        internal TrackingHandle Handle { get; }

        internal FakeLockAcquire Lock { get; }

        internal FakeOwnerRecordStore Records { get; }

        internal FakeAuthTierSource Tier { get; }

        internal FakeSweep Sweep { get; }

        internal FakeChildSessionsGate Gate { get; }

        internal RecordingLaunchSurface Launch { get; }

        internal FakeControlSurface Control { get; }

        internal BeforeConnectSegment Segment { get; }
    }

    /// <summary>
    /// The after-logon segment plus the fakes it is composed from and the call log of one case.
    /// </summary>
    private sealed class AfterLogonFixture
    {
        internal AfterLogonFixture()
        {
            Calls = [];
            Handle = new TrackingHandle(Calls);
            Records = new FakeOwnerRecordStore(Calls);
            Agent = new FakeAgentConnector(Calls);
            Pipe = new FakeOwnerPipeHost(Calls);
            SessionLookup = new FakeProcessSessionLookup(Calls);
            BeforeConnect = new BeforeConnectResult(
                sweepCompleted: true,
                childSessionsOpenedByUs: false,
                alternateShellConfigured: true,
                lockHandle: Handle);
            Segment = new AfterLogonSegment(
                new SessionStartOptions(SessionId, Environment.ProcessId, HostStartTime, AcquiredAt),
                Records,
                Agent,
                Pipe,
                SessionLookup,
                BeforeConnect);
        }

        internal List<string> Calls { get; }

        internal TrackingHandle Handle { get; }

        internal FakeOwnerRecordStore Records { get; }

        internal FakeAgentConnector Agent { get; }

        internal FakeOwnerPipeHost Pipe { get; }

        internal FakeProcessSessionLookup SessionLookup { get; }

        internal BeforeConnectResult BeforeConnect { get; }

        internal AfterLogonSegment Segment { get; }
    }

    /// <summary>A lock handle that records whether the segment closed it.</summary>
    private sealed class TrackingHandle : IDisposable
    {
        private readonly List<string> _calls;

        internal TrackingHandle(List<string> calls)
        {
            _calls = calls;
        }

        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            _calls.Add("lock.dispose");
        }
    }

    private sealed class FakeLockAcquire : ISingleInstanceLockAcquire
    {
        private readonly TrackingHandle _handle;
        private readonly List<string> _calls;

        internal FakeLockAcquire(TrackingHandle handle, List<string> calls)
        {
            _handle = handle;
            _calls = calls;
        }

        internal bool HeldElsewhere { get; set; }

        public LockAttempt Acquire()
        {
            _calls.Add("lock");
            return HeldElsewhere ? LockAttempt.HeldElsewhere() : LockAttempt.Acquired(_handle);
        }
    }

    /// <summary>
    /// An ownership record store that keeps the written record in memory, so a case can prove that a
    /// failed start leaves no record behind.
    /// </summary>
    private sealed class FakeOwnerRecordStore : IOwnerRecordStore
    {
        private readonly List<string> _calls;

        internal FakeOwnerRecordStore(List<string> calls)
        {
            _calls = calls;
        }

        internal OwnerRecordStatus ReadStatus { get; set; } = OwnerRecordStatus.Absent;

        internal int ReadCount { get; private set; }

        internal int WriteCount { get; private set; }

        internal int DeleteCount { get; private set; }

        internal SessionOwnerRecord? Record { get; private set; }

        internal bool Exists => Record is not null;

        public OwnerRecordRead Read()
        {
            ReadCount++;
            _calls.Add("record.read");
            return ReadStatus switch
            {
                OwnerRecordStatus.Absent => OwnerRecordRead.Absent(),
                OwnerRecordStatus.TakeOver => OwnerRecordRead.TakeOver(new SessionOwnerRecord(
                    SessionId,
                    Environment.ProcessId,
                    HostStartTime,
                    ChildSessionId,
                    false,
                    SessionState.Disconnected,
                    AcquiredAt)),
                OwnerRecordStatus.NotTakeable => OwnerRecordRead.NotTakeable(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(ReadStatus),
                    ReadStatus,
                    "Unknown ownership record status."),
            };
        }

        public void Write(SessionOwnerRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            WriteCount++;
            Record = record;
            _calls.Add(
                "record.write:"
                + SessionStateNames.ToWireName(record.State)
                + ":"
                + record.SubagentWtsSessionId
                + ":"
                + record.ChildSessionsOpenedByUs);
        }

        public void Delete()
        {
            DeleteCount++;
            Record = null;
            _calls.Add("record.delete");
        }
    }

    private sealed class FakeAuthTierSource : IAuthTierSource
    {
        private readonly List<string> _calls;

        internal FakeAuthTierSource(List<string> calls)
        {
            _calls = calls;
        }

        internal AuthTier Tier { get; set; } = AuthTier.SystemCredential;

        public AuthTier Detect()
        {
            _calls.Add("tier");
            return Tier;
        }
    }

    private sealed class FakeSweep : ILeftoverSweep
    {
        private readonly List<string> _calls;

        internal FakeSweep(List<string> calls)
        {
            _calls = calls;
        }

        internal Exception? Failure { get; set; }

        internal int Count { get; private set; }

        public LeftoverSweepReport Sweep()
        {
            Count++;
            _calls.Add("sweep");
            if (Failure is not null)
            {
                throw Failure;
            }

            return new LeftoverSweepReport(Array.Empty<int>(), Array.Empty<int>());
        }
    }

    private sealed class FakeChildSessionsGate : IChildSessionsGate
    {
        private readonly List<string> _calls;
        private bool _enabled;

        internal FakeChildSessionsGate(List<string> calls)
        {
            _calls = calls;
            _enabled = true;
            Policy = new Dictionary<PolicyValue, PolicyState>
            {
                [PolicyValue.RdpInbound] = PolicyState.Allowed,
                [PolicyValue.ChildConnections] = PolicyState.Allowed,
            };
        }

        internal Dictionary<PolicyValue, PolicyState> Policy { get; }

        internal bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        internal bool EnablesOnSuccess { get; set; } = true;

        internal ElevationResult? Elevation { get; set; }

        internal int ProbeCount { get; private set; }

        internal int EnableCalls { get; private set; }

        public bool IsEnabled()
        {
            ProbeCount++;
            _calls.Add("gate.probe");
            return _enabled;
        }

        public PolicyState ReadPolicy(PolicyValue value)
        {
            _calls.Add("gate.policy:" + PolicyName(value));
            return Policy[value];
        }

        public ElevationResult Enable()
        {
            EnableCalls++;
            _calls.Add("gate.enable");
            ElevationResult result = Elevation ?? new ElevationResult(true, null, 0);
            if (!result.Failed && EnablesOnSuccess)
            {
                _enabled = true;
            }

            return result;
        }

        private static string PolicyName(PolicyValue value)
        {
            return value switch
            {
                PolicyValue.RdpInbound => ChildSessionsPolicy.RdpInboundValueName,
                PolicyValue.ChildConnections => ChildSessionsPolicy.ChildConnectionsValueName,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown policy value."),
            };
        }
    }

    /// <summary>
    /// A launch surface that records each property write, so a case can prove both the order and that a
    /// refused start wrote nothing at all.
    /// </summary>
    private sealed class RecordingLaunchSurface : IRdpLaunchSurface
    {
        private readonly List<string> _calls;
        private string _startProgram = string.Empty;
        private string _workingDirectory = string.Empty;

        internal RecordingLaunchSurface(List<string> calls)
        {
            _calls = calls;
        }

        internal bool IgnoreWrites { get; set; }

        internal IReadOnlyList<string> Writes => _calls.FindAll(call => call.StartsWith("launch.", StringComparison.Ordinal));

        public string StartProgram
        {
            get => IgnoreWrites ? string.Empty : _startProgram;
            set
            {
                _calls.Add("launch.start-program");
                _startProgram = IgnoreWrites ? string.Empty : value;
            }
        }

        public string WorkingDirectory
        {
            get => IgnoreWrites ? string.Empty : _workingDirectory;
            set
            {
                _calls.Add("launch.working-directory");
                _workingDirectory = IgnoreWrites ? string.Empty : value;
            }
        }
    }

    /// <summary>A hosted control stand-in; only the raw object identity travels to the connector.</summary>
    private sealed class FakeControlSurface : IRdpControlSurface
    {
        internal FakeControlSurface()
        {
            Ocx = new object();
        }

        public object Ocx { get; }

        public nint WindowHandle => 0;
    }

    private sealed class FakeSessionConnector : ISessionConnector
    {
        internal FakeSessionConnector()
        {
            Gate = new ConnectionGate(LeftoverVerdict.NoLeftover, Array.Empty<SessionCandidate>());
        }

        internal Exception? GateFailure { get; set; }

        internal int PrepareGateCalls { get; private set; }

        internal int OpenCalls { get; private set; }

        internal ConnectionGate Gate { get; }

        internal object? LastOcx { get; private set; }

        internal int LastWidth { get; private set; }

        internal int LastHeight { get; private set; }

        internal ConnectionGate? LastGate { get; private set; }

        public ConnectionGate PrepareGate()
        {
            PrepareGateCalls++;
            if (GateFailure is not null)
            {
                throw GateFailure;
            }

            return Gate;
        }

        public void Open(object ocx, int width, int height, ConnectionGate gate)
        {
            OpenCalls++;
            LastOcx = ocx;
            LastWidth = width;
            LastHeight = height;
            LastGate = gate;
        }
    }

    /// <summary>
    /// A fake agent connector whose client can be closed, so a case can prove that a failed segment closes
    /// the connection it opened.
    /// </summary>
    private sealed class FakeAgentConnector : IAgentConnector
    {
        private readonly List<string> _calls;

        internal FakeAgentConnector(List<string> calls)
        {
            _calls = calls;
        }

        internal AgentAttachOutcome? Outcome { get; set; }

        internal Exception? Failure { get; set; }

        internal DisposableSessionClient? Connection { get; private set; }

        internal bool ConnectionClosed => Connection is null || Connection.Disposed;

        public AgentAttachOutcome Attach()
        {
            _calls.Add("agent.connect");
            if (Failure is not null)
            {
                throw Failure;
            }

            if (Outcome is not null)
            {
                return Outcome;
            }

            Connection = new DisposableSessionClient();
            return AgentAttachOutcome.Succeeded(new AgentConnection(Connection, AgentProcessId));
        }
    }

    /// <summary>
    /// An agent client that records its own disposal. It never talks to a pipe, so no case can reach a real
    /// agent even by accident.
    /// </summary>
    private sealed class DisposableSessionClient : ISessionClient, IAsyncDisposable
    {
        internal bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            throw Unavailable();
        }

        public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
        {
            throw Unavailable();
        }

        public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
        {
            throw Unavailable();
        }

        public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
        {
            throw Unavailable();
        }

        public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            throw Unavailable();
        }

        public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
        {
            throw Unavailable();
        }

        public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
        {
            throw Unavailable();
        }

        private static SessionOperationException Unavailable()
        {
            return new SessionOperationException(
                SessionErrorCode.Disconnected,
                "The fake agent client has no transport, so the call was not sent.",
                false);
        }
    }

    private sealed class FakeOwnerPipeHost : IOwnerPipeHost
    {
        private readonly List<string> _calls;

        internal FakeOwnerPipeHost(List<string> calls)
        {
            _calls = calls;
        }

        internal Exception? Failure { get; set; }

        internal int Count { get; private set; }

        public void Start(ISessionToolHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Count++;
            _calls.Add("pipe.start");
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class FakeProcessSessionLookup : IProcessSessionLookup
    {
        private readonly List<string> _calls;

        internal FakeProcessSessionLookup(List<string> calls)
        {
            _calls = calls;
        }

        internal Exception? Failure { get; set; }

        internal int Count { get; private set; }

        public int SessionIdFor(int processId)
        {
            Count++;
            _calls.Add("session.lookup:" + processId);
            if (Failure is not null)
            {
                throw Failure;
            }

            return ChildSessionId;
        }
    }

    /// <summary>A wait clock that returns at once and records every wait.</summary>
    private sealed class RecordingWaitClock : IWaitClock
    {
        private readonly List<string> _calls = [];

        internal IReadOnlyList<string> Calls => _calls;

        internal int Waits => _calls.Count;
        public void Wait(TimeSpan duration)
        {
            Assert.IsGreaterThan(TimeSpan.Zero, duration, "The waiter must only ever ask for a positive interval.");
            _calls.Add("wait");
        }
    }

    /// <summary>
    /// The two facts the logon gate reads from the machine, answered from a script so one case can drive
    /// them: whether a session of this run appeared, and whether Windows published a user for it.
    /// </summary>
    private sealed class ScriptedLogonEvidence : ILogonEvidence
    {
        private readonly bool _hasCreatedSession;
        private readonly Queue<bool> _loggedOnAnswers;

        internal ScriptedLogonEvidence(int createdSessionId, bool hasCreatedSession, IEnumerable<bool> loggedOnAnswers)
        {
            ArgumentNullException.ThrowIfNull(loggedOnAnswers);
            CreatedSessionId = createdSessionId;
            _hasCreatedSession = hasCreatedSession;
            _loggedOnAnswers = new Queue<bool>(loggedOnAnswers);
        }

        public int CreatedSessionId { get; }

        public bool HasCreatedSession()
        {
            return _hasCreatedSession;
        }

        public bool HasLoggedOnUser()
        {
            return _loggedOnAnswers.Count > 0 && _loggedOnAnswers.Dequeue();
        }
    }
}
