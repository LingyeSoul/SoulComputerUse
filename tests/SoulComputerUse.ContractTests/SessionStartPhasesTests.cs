using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup.Phases;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the three-segment start and the phase decision that orders them. Every case is a
/// pure call over one fact snapshot or one run over three fake ports, so no case takes the machine-wide
/// lock, changes a policy value, prompts for elevation or opens a connection.
/// </summary>
/// <remarks>
/// The order these cases protect is the one a production machine really produces: the agent is the child
/// session's alternate shell, so it exists only after the operator has logged on. A pipeline that attached
/// the agent before opening the connection would be untestable against a real machine and wrong on every
/// one of them.
/// </remarks>
[TestClass]
public sealed class SessionStartPhasesTests
{
    /// <summary>A nonce-shaped value, used to prove that no explanation can carry one.</summary>
    private const string SampleNonce = "0123456789ABCDEF0123456789ABCDEF";

    [TestMethod]
    public void EveryPhaseHasAUniqueWireNameAndRoundTrips()
    {
        SessionStartPhase[] phases = Enum.GetValues<SessionStartPhase>();
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (SessionStartPhase phase in phases)
        {
            string wireName = SessionStartPhaseNames.ToWireName(phase);
            Assert.IsTrue(names.Add(wireName), $"The wire name '{wireName}' is used more than once.");
            Assert.AreEqual(phase, SessionStartPhaseNames.FromWireName(wireName));
            Assert.IsTrue(SessionStartPhaseNames.TryFromWireName(wireName, out SessionStartPhase parsed));
            Assert.AreEqual(phase, parsed);
        }

        CollectionAssert.AreEquivalent(
            new List<string> { "before-connect", "awaiting-credentials", "after-logon", "ready" },
            names.ToList());
    }

    [TestMethod]
    public void ValuesThatAreNotPhasesAreRefusedInsteadOfGuessed()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionStartPhaseNames.ToWireName((SessionStartPhase)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionStartPhaseNames.FromWireName("starting"));
        Assert.ThrowsExactly<ArgumentNullException>(() => SessionStartPhaseNames.FromWireName(null!));

        Assert.IsFalse(SessionStartPhaseNames.TryFromWireName("connecting", out SessionStartPhase unknown));
        Assert.IsFalse(SessionStartPhaseNames.TryFromWireName(null, out SessionStartPhase missing));
        Assert.AreEqual(default, unknown);
        Assert.AreEqual(default, missing);
    }

    [TestMethod]
    public void TheDecisionTableReturnsTheFirstStepThatIsNotDone()
    {
        AssertAction(
            new BooleanFacts(false, false, false, false, false, false, false),
            SessionStartPhaseAction.Sweep,
            SessionStartPhase.BeforeConnect);

        AssertAction(
            new BooleanFacts(true, false, false, false, false, false, false),
            SessionStartPhaseAction.EnableChildSessions,
            SessionStartPhase.BeforeConnect);

        AssertAction(
            new BooleanFacts(true, true, false, false, false, false, false),
            SessionStartPhaseAction.ConfigureAlternateShell,
            SessionStartPhase.BeforeConnect);

        AssertAction(
            new BooleanFacts(true, true, true, false, false, false, false),
            SessionStartPhaseAction.OpenConnection,
            SessionStartPhase.BeforeConnect);

        AssertAction(
            new BooleanFacts(true, true, true, true, false, false, false),
            SessionStartPhaseAction.AttachAgent,
            SessionStartPhase.AwaitingCredentials);

        AssertAction(
            new BooleanFacts(true, true, true, true, true, false, false),
            SessionStartPhaseAction.StartOwnerPipe,
            SessionStartPhase.AfterLogon);

        AssertAction(
            new BooleanFacts(true, true, true, true, true, true, false),
            SessionStartPhaseAction.MarkReady,
            SessionStartPhase.AfterLogon);
    }

    [TestMethod]
    public void AFinishedPipelineIsReadyAndAsksForNothing()
    {
        SessionStartPhaseDecision decision = Decide(new BooleanFacts(true, true, true, true, true, true, true));

        Assert.AreEqual(SessionStartPhase.Ready, decision.Phase);
        Assert.IsNull(decision.NextAction);
        Assert.IsTrue(decision.IsReady);
        Assert.IsFalse(decision.Reason.Contains(SampleNonce, StringComparison.Ordinal));
    }

    [TestMethod]
    public void AnAgentReportedBeforeTheConnectionSendsTheConnectionFirstAndSaysWhy()
    {
        SessionStartPhaseDecision decision = Decide(new BooleanFacts(true, true, true, false, true, true, true));

        Assert.AreEqual(SessionStartPhaseAction.OpenConnection, decision.NextAction);
        StringAssert.Contains(
            decision.Reason,
            "must precede",
            "An agent reported before the connection must be answered with the connection first, and the reason has to say which order is required.");
        StringAssert.Contains(decision.Reason, "agent", "The reason must name the step the connection comes before.");
    }

    [TestMethod]
    public void TheRunnerEntersTheFourPhasesInContractOrder()
    {
        Run run = new();

        SessionPhaseReport report = run.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

        CollectionAssert.AreEqual(
            new List<SessionStartPhase>
            {
                SessionStartPhase.BeforeConnect,
                SessionStartPhase.AwaitingCredentials,
                SessionStartPhase.AfterLogon,
                SessionStartPhase.Ready,
            },
            run.Observer.Phases.ToList());

        CollectionAssert.AreEqual(
            new List<string> { "before-connect", "open", "after-logon" },
            run.Calls);

        CollectionAssert.AreEqual(
            new List<SessionStartPhase>
            {
                SessionStartPhase.BeforeConnect,
                SessionStartPhase.AwaitingCredentials,
                SessionStartPhase.AfterLogon,
                SessionStartPhase.Ready,
            },
            report.Phases.ToList());

        Assert.IsTrue(report.ObserversSucceeded, report.Describe());
        Assert.AreSame(run.BeforeConnect.Handle, report.LockHandle, "A ready session keeps the lock the before-connect segment took.");
        Assert.IsFalse(run.BeforeConnect.Handle.Disposed, "The runner must not close the lock handle of a session that is ready.");
    }

    [TestMethod]
    public void AFailedBeforeConnectNeverOpensTheConnectionOrRunsTheAfterLogonPhase()
    {
        Run run = new();
        run.BeforeConnect.Failure = new InvalidOperationException("the leftover sweep did not prove a clean machine");

        SessionPhaseException error = Assert.ThrowsExactly<SessionPhaseException>(
            () => run.RunAsync(CancellationToken.None).GetAwaiter().GetResult());

        Assert.AreEqual(SessionErrorCode.ManualIntervention, error.Code);
        Assert.AreEqual(SessionStartPhase.BeforeConnect, error.Phase);
        CollectionAssert.AreEqual(new List<string> { "before-connect" }, run.Calls);
        Assert.AreEqual(0, run.Opener.Count, "A refused before-connect must never open a connection.");
        Assert.AreEqual(0, run.AfterLogon.Count, "A refused before-connect must never look for an agent.");
        Assert.IsEmpty(run.Observer.Phases, "A failed before-connect never reaches a phase boundary.");
    }

    [TestMethod]
    public void AConnectionThatThrowsIsNeverFollowedByTheAfterLogonPhase()
    {
        Run run = new();
        run.Opener.Failure = new InvalidOperationException("the remote control refused to connect");

        SessionPhaseException error = Assert.ThrowsExactly<SessionPhaseException>(
            () => run.RunAsync(CancellationToken.None).GetAwaiter().GetResult());

        Assert.AreEqual(SessionStartPhase.BeforeConnect, error.Phase);
        CollectionAssert.AreEqual(new List<string> { "before-connect", "open" }, run.Calls);
        Assert.AreEqual(0, run.AfterLogon.Count, "A connection that did not open must never be followed by an agent attach.");
        CollectionAssert.AreEqual(
            new List<SessionStartPhase> { SessionStartPhase.BeforeConnect, SessionStartPhase.AwaitingCredentials },
            run.Observer.Phases.ToList(),
            "The window shows the logon screen while the connection is opened, so the caption announces that phase before the attempt; the run then stops there and no later phase may be announced.");

        Assert.AreSame(
            run.BeforeConnect.Handle,
            Assert.IsInstanceOfType<BeforeConnectResult>(
                error.Data[SessionStartPhaseRunner.BeforeConnectDataKey]).LockHandle,
            "The caller still has to be able to close the lock the before-connect segment took.");
    }

    [TestMethod]
    public void AStoppingFailureStillCarriesTheObserverFailuresThatAlreadyHappened()
    {
        Run run = new();
        run.Observer.ThrowFor(SessionStartPhase.BeforeConnect);
        run.Opener.Failure = new InvalidOperationException("the remote control refused to connect");

        SessionPhaseException error = Assert.ThrowsExactly<SessionPhaseException>(
            () => run.RunAsync(CancellationToken.None).GetAwaiter().GetResult());

        IReadOnlyList<SessionPhaseFailure> carried = Assert.IsInstanceOfType<IReadOnlyList<SessionPhaseFailure>>(
            error.Data[SessionStartPhaseRunner.ObserverFailuresDataKey]);

        Assert.HasCount(1, carried, "A stopping failure must not swallow the observer failure that already happened.");
        Assert.AreEqual(SessionStartPhase.BeforeConnect, carried[0].Phase);
    }

    [TestMethod]
    public void AnAfterLogonThatIsNotReadyRaisesNotReadyInsteadOfPretending()
    {
        Run run = new();
        run.AfterLogon.Result = new AfterLogonResult(
            agentAttached: true,
            ownerPipeStarted: true,
            subagentWtsSessionId: 15,
            recordReady: false);

        SessionPhaseException error = Assert.ThrowsExactly<SessionPhaseException>(
            () => run.RunAsync(CancellationToken.None).GetAwaiter().GetResult());

        Assert.AreEqual(SessionErrorCode.NotReady, error.Code);
        Assert.AreEqual(SessionStartPhase.AfterLogon, error.Phase);
        CollectionAssert.AreEqual(
            new List<SessionStartPhase>
            {
                SessionStartPhase.BeforeConnect,
                SessionStartPhase.AwaitingCredentials,
                SessionStartPhase.AfterLogon,
            },
            run.Observer.Phases.ToList(),
            "The ready boundary must not be announced when the record is not ready.");
    }

    [TestMethod]
    public void AnObserverThatThrowsDoesNotStopTheRunAndIsReportedInstead()
    {
        Run run = new();
        run.Observer.ThrowFor(SessionStartPhase.Ready);

        SessionPhaseReport report = run.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsFalse(report.ObserversSucceeded, "An observer that throws must be visible in the report.");
        Assert.HasCount(1, report.Failures);
        Assert.AreEqual(SessionStartPhase.Ready, report.Failures[0].Phase);
        StringAssert.Contains(report.Failures[0].Describe(), "ready");
        Assert.HasCount(4, report.Phases);
        CollectionAssert.AreEqual(
            new List<string> { "before-connect", "open", "after-logon" },
            run.Calls,
            "An observer is a reporting channel, so its failure must not change what the session did.");
    }

    [TestMethod]
    public void ABeforeConnectResultThatDidNotSweepIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new BeforeConnectResult(
                sweepCompleted: false,
                childSessionsOpenedByUs: false,
                alternateShellConfigured: true,
                lockHandle: new TrackedHandle()));

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new BeforeConnectResult(
                sweepCompleted: true,
                childSessionsOpenedByUs: false,
                alternateShellConfigured: true,
                lockHandle: null));
    }

    [TestMethod]
    public void AnAfterLogonResultThatClaimsReadyWithoutAPipeIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new AfterLogonResult(
                agentAttached: true,
                ownerPipeStarted: false,
                subagentWtsSessionId: 15,
                recordReady: true));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new AfterLogonResult(
                agentAttached: true,
                ownerPipeStarted: false,
                subagentWtsSessionId: 0,
                recordReady: false));
    }

    [TestMethod]
    public void EveryExplanationStaysFreeOfPathsAccountsAndNonces()
    {
        foreach (string text in AllExplanationText())
        {
            Assert.IsFalse(text.Contains("C:\\", StringComparison.OrdinalIgnoreCase), text);
            Assert.IsFalse(text.Contains('/', StringComparison.Ordinal), text);
            Assert.IsFalse(text.Contains("S-1-", StringComparison.OrdinalIgnoreCase), text);
            Assert.IsFalse(text.Contains("kf-", StringComparison.OrdinalIgnoreCase), text);
            Assert.IsFalse(
                Regex.IsMatch(text, "[0-9A-Fa-f]{32}", RegexOptions.CultureInvariant),
                "An explanation must not carry a nonce-shaped value: " + text);
        }
    }

    private static List<string> AllExplanationText()
    {
        List<string> texts = [];
        foreach (FieldInfo field in typeof(SessionStartPhaseMessages).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(string) && field.GetValue(null) is string value)
            {
                texts.Add(value);
            }
        }

        foreach (BooleanFacts facts in FactCombinations())
        {
            texts.Add(Decide(facts).Reason);
        }

        texts.Add(SessionStartPhaseNames.ToWireName(SessionStartPhase.BeforeConnect));
        texts.Add(SessionStartPhaseNames.ToWireName(SessionStartPhase.AwaitingCredentials));
        texts.Add(SessionStartPhaseNames.ToWireName(SessionStartPhase.AfterLogon));
        texts.Add(SessionStartPhaseNames.ToWireName(SessionStartPhase.Ready));

        Assert.IsGreaterThanOrEqualTo(14, texts.Count, "A scan of too few explanations would prove nothing.");
        return texts;
    }

    private static IEnumerable<BooleanFacts> FactCombinations()
    {
        // Every prefix of the ordered steps, from nothing done to everything done, reaches each template
        // of the decision table exactly once.
        for (int completed = 0; completed <= 7; completed++)
        {
            bool[] facts = new bool[7];
            for (int index = 0; index < completed; index++)
            {
                facts[index] = true;
            }

            yield return new BooleanFacts(facts[0], facts[1], facts[2], facts[3], facts[4], facts[5], facts[6]);
        }

        // The reversed-order snapshot, whose explanation names the required order.
        yield return new BooleanFacts(true, true, true, false, true, true, true);
    }

    private static SessionStartPhaseDecision Decide(BooleanFacts facts)
    {
        return new SessionStartPhasePlan(
            facts.SweepCompleted,
            facts.ChildSessionsSettled,
            facts.AlternateShellConfigured,
            facts.ConnectionOpened,
            facts.AgentAttached,
            facts.OwnerPipeStarted,
            facts.RecordReady).Decide();
    }

    private static void AssertAction(
        BooleanFacts facts,
        SessionStartPhaseAction expectedAction,
        SessionStartPhase expectedPhase)
    {
        SessionStartPhaseDecision decision = Decide(facts);

        Assert.AreEqual(expectedAction, decision.NextAction, decision.Reason);
        Assert.AreEqual(expectedPhase, decision.Phase, decision.Reason);
    }

    /// <summary>One seven-boolean fact snapshot, so a loop can build snapshots without a tuple.</summary>
    private sealed record BooleanFacts(
        bool SweepCompleted,
        bool ChildSessionsSettled,
        bool AlternateShellConfigured,
        bool ConnectionOpened,
        bool AgentAttached,
        bool OwnerPipeStarted,
        bool RecordReady);

    /// <summary>A lock handle that records whether it was closed.</summary>
    private sealed class TrackedHandle : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>The three fake ports, the observer and the call log of one case.</summary>
    private sealed class Run
    {
        internal Run()
        {
            Calls = [];
            BeforeConnect = new FakeBeforeConnect(Calls);
            Opener = new FakeConnectionOpener(Calls);
            AfterLogon = new FakeAfterLogon(Calls);
            Observer = new RecordingObserver();
        }

        internal List<string> Calls { get; }

        internal FakeBeforeConnect BeforeConnect { get; }

        internal FakeConnectionOpener Opener { get; }

        internal FakeAfterLogon AfterLogon { get; }

        internal RecordingObserver Observer { get; }

        internal Task<SessionPhaseReport> RunAsync(CancellationToken cancellationToken)
        {
            SessionStartPhaseRunner runner = new(BeforeConnect, Opener, AfterLogon, Observer);
            return runner.RunAsync(cancellationToken);
        }
    }

    private sealed class FakeBeforeConnect : ISessionStartBeforeConnect
    {
        private readonly List<string> _calls;

        internal FakeBeforeConnect(List<string> calls)
        {
            _calls = calls;
            Handle = new TrackedHandle();
        }

        internal TrackedHandle Handle { get; }

        internal Exception? Failure { get; set; }

        internal int Count { get; private set; }

        public BeforeConnectResult Execute()
        {
            Count++;
            _calls.Add("before-connect");

            if (Failure is not null)
            {
                throw Failure;
            }

            return new BeforeConnectResult(true, false, true, Handle);
        }
    }

    private sealed class FakeConnectionOpener : ISessionConnectionOpener
    {
        private readonly List<string> _calls;

        internal FakeConnectionOpener(List<string> calls)
        {
            _calls = calls;
        }

        internal Exception? Failure { get; set; }

        internal int Count { get; private set; }

        public void Open()
        {
            Count++;
            _calls.Add("open");

            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class FakeAfterLogon : ISessionStartAfterLogon
    {
        private readonly List<string> _calls;

        internal FakeAfterLogon(List<string> calls)
        {
            _calls = calls;
        }

        internal Exception? Failure { get; set; }

        internal AfterLogonResult Result { get; set; } =
            new(agentAttached: true, ownerPipeStarted: true, subagentWtsSessionId: 15, recordReady: true);

        internal int Count { get; private set; }

        public AfterLogonResult Execute()
        {
            Count++;
            _calls.Add("after-logon");

            if (Failure is not null)
            {
                throw Failure;
            }

            return Result;
        }
    }

    private sealed class RecordingObserver : ISessionPhaseObserver
    {
        private readonly List<SessionStartPhase> _phases = [];
        private readonly HashSet<SessionStartPhase> _throwFor = [];

        internal IReadOnlyList<SessionStartPhase> Phases => _phases;

        internal void ThrowFor(SessionStartPhase phase)
        {
            _throwFor.Add(phase);
        }

        public void OnPhaseChanged(SessionStartPhase phase)
        {
            _phases.Add(phase);

            if (_throwFor.Contains(phase))
            {
                throw new InvalidOperationException("the reporting channel refused the phase " + phase);
            }
        }
    }
}