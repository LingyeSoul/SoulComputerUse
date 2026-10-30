using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SessionLifecycle;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the execution layer of section 8. Every case runs against fake ports, so the
/// order, the failure aggregation and the refusal rules are asserted without logging off a real
/// session, changing the machine-wide child-session switch or exiting the test process.
/// </summary>
[TestClass]
public sealed class SessionTeardownExecutionTests
{
    private const int ChildSessionId = 15;

    [TestMethod]
    public void TheStepsRunInThePlannedOrderAndACompletePlanExitsCleanly()
    {
        List<string> calls = [];
        SessionTeardownExecutor executor = CreateExecutor(calls, new FakeLogOff(calls), new FakeSwitch(calls), new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: true,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        SessionTeardownReport report = Execute(
            executor,
            plan,
            new SessionTeardownContext(ChildSessionId, ChildSessionsOpenedByUs: true, LockIsHeld: true, ProcessExitRequested: true));

        CollectionAssert.AreEqual(
            new List<string> { "closing", "logoff:15", "restore", "release" },
            calls,
            "Entering closing must precede the logoff, and the logoff must precede the lock release.");

        Assert.IsFalse(report.AnyStepFailed);
        Assert.AreEqual(SessionLifecycleExitCodes.Success, report.ExitCode);
        Assert.IsTrue(report.LockWasHeld);
        Assert.IsTrue(report.ProcessExitRequested);
        CollectionAssert.AreEqual(
            new List<SessionShutdownStep>(plan.Steps),
            StepList(report),
            "The report must hold one result per planned step, in plan order.");

        SessionTeardownStepResult exit = report.Steps[^1];
        Assert.AreEqual(SessionShutdownStep.Exit, exit.Step);
        Assert.IsFalse(exit.Executed, "The exit step belongs to the caller; this layer must never exit the process itself.");
        Assert.IsTrue(exit.Succeeded, "Leaving the exit to the caller is the planned behaviour, not a failure.");
    }

    [TestMethod]
    public void ARefusedLogOffStillReleasesTheLockAndTurnsTheExitCodeNonZero()
    {
        List<string> calls = [];
        FakeLogOff logOff = new(calls) { FailOnSessionId = ChildSessionId };
        SessionTeardownExecutor executor = CreateExecutor(calls, logOff, new FakeSwitch(calls), new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: true,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        SessionTeardownReport report = Execute(
            executor,
            plan,
            new SessionTeardownContext(ChildSessionId, ChildSessionsOpenedByUs: true, LockIsHeld: true, ProcessExitRequested: true));

        CollectionAssert.AreEqual(
            new List<string> { "closing", "logoff:15", "restore", "release" },
            calls,
            "A failed step must not stop the sequence; the lock release in particular has to be attempted anyway.");

        Assert.IsTrue(report.AnyStepFailed);
        Assert.AreEqual(SessionLifecycleExitCodes.Failure, report.ExitCode, "A failed step must never be reported as a clean exit.");

        SessionTeardownStepResult logOffResult = Find(report, SessionShutdownStep.LogOffChildSession);
        Assert.IsTrue(logOffResult.Executed);
        Assert.IsFalse(logOffResult.Succeeded);
        Assert.AreEqual(TeardownFailureReason.LogOffFailed, logOffResult.Failure);

        SessionTeardownStepResult releaseResult = Find(report, SessionShutdownStep.ReleaseLock);
        Assert.IsTrue(releaseResult.Executed);
        Assert.IsTrue(releaseResult.Succeeded, "The release must still be attempted and can still succeed after a failed logoff.");
        Assert.AreEqual(TeardownFailureReason.None, releaseResult.Failure);
    }

    [TestMethod]
    public void AFailedStateTransitionDoesNotStopTheLogOffOrTheRelease()
    {
        List<string> calls = [];
        FakeStateTransition transition = new(calls) { Fail = true };
        SessionTeardownExecutor executor = new(transition, new FakeLogOff(calls), new FakeSwitch(calls), new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        SessionTeardownReport report = Execute(
            executor,
            plan,
            new SessionTeardownContext(ChildSessionId, ChildSessionsOpenedByUs: false, LockIsHeld: true, ProcessExitRequested: true));

        CollectionAssert.AreEqual(new List<string> { "closing", "logoff:15", "release" }, calls);
        Assert.AreEqual(TeardownFailureReason.StateTransitionFailed, Find(report, SessionShutdownStep.EnterClosing).Failure);
        Assert.IsTrue(Find(report, SessionShutdownStep.LogOffChildSession).Succeeded);
        Assert.IsTrue(Find(report, SessionShutdownStep.ReleaseLock).Succeeded);
        Assert.AreEqual(SessionLifecycleExitCodes.Failure, report.ExitCode);
    }

    [TestMethod]
    public void AFailedLockReleaseIsReportedAsAFailure()
    {
        List<string> calls = [];
        FakeLockRelease release = new(calls) { Fail = true };
        SessionTeardownExecutor executor = CreateExecutor(calls, new FakeLogOff(calls), new FakeSwitch(calls), release);
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: false,
            LockIsHeld: true));

        SessionTeardownReport report = Execute(
            executor,
            plan,
            new SessionTeardownContext(null, ChildSessionsOpenedByUs: false, LockIsHeld: true, ProcessExitRequested: true));

        SessionTeardownStepResult releaseResult = Find(report, SessionShutdownStep.ReleaseLock);
        Assert.IsFalse(releaseResult.Succeeded);
        Assert.AreEqual(TeardownFailureReason.ReleaseFailed, releaseResult.Failure);
        Assert.AreEqual(SessionLifecycleExitCodes.Failure, report.ExitCode);
    }

    [TestMethod]
    public void ARefusedSwitchRestoreStillReleasesTheLock()
    {
        List<string> calls = [];
        FakeSwitch childSessionsSwitch = new(calls) { Fail = true };
        SessionTeardownExecutor executor = CreateExecutor(calls, new FakeLogOff(calls), childSessionsSwitch, new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: true,
            ChildSessionNeedsLogOff: false,
            LockIsHeld: true));

        SessionTeardownReport report = Execute(
            executor,
            plan,
            new SessionTeardownContext(ChildWtsSessionId: null, ChildSessionsOpenedByUs: true, LockIsHeld: true, ProcessExitRequested: true));

        CollectionAssert.AreEqual(new List<string> { "closing", "restore", "release" }, calls);
        Assert.AreEqual(TeardownFailureReason.RestoreFailed, Find(report, SessionShutdownStep.RestoreChildSessionsSwitch).Failure);
        Assert.IsTrue(
            Find(report, SessionShutdownStep.ReleaseLock).Succeeded,
            "A switch that could not be restored must not keep the machine-wide lock held.");
        Assert.AreEqual(SessionLifecycleExitCodes.Failure, report.ExitCode);
    }

    [TestMethod]
    public void ALogOffStepWithoutAChildSessionIdFailsAndNeverCallsThePort()
    {
        List<string> calls = [];
        SessionTeardownExecutor executor = CreateExecutor(calls, new FakeLogOff(calls), new FakeSwitch(calls), new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        SessionTeardownReport report = Execute(
            executor,
            plan,
            new SessionTeardownContext(ChildWtsSessionId: null, ChildSessionsOpenedByUs: false, LockIsHeld: true, ProcessExitRequested: true));

        Assert.IsFalse(
            calls.Exists(call => call.StartsWith("logoff", StringComparison.Ordinal)),
            "An unknown session id must never reach the logoff port.");

        SessionTeardownStepResult logOffResult = Find(report, SessionShutdownStep.LogOffChildSession);
        Assert.IsFalse(logOffResult.Executed);
        Assert.IsFalse(logOffResult.Succeeded);
        Assert.AreEqual(
            TeardownFailureReason.MissingChildSession,
            logOffResult.Failure,
            "A plan that asks for a logoff without a child session is a state error, not a skipped step.");
        Assert.AreEqual(
            SessionErrorCode.State,
            TeardownFailureCodes.ToSessionErrorCode(logOffResult.Failure),
            "A plan that contradicts its own context has to be expressible as the contract's E_STATE.");
        Assert.AreEqual(SessionLifecycleExitCodes.Failure, report.ExitCode);
        Assert.IsTrue(Find(report, SessionShutdownStep.ReleaseLock).Succeeded, "A missing child session must not stop the release.");
    }

    [TestMethod]
    public void ANonPositiveChildSessionIdIsAlsoRefused()
    {
        List<string> calls = [];
        SessionTeardownExecutor executor = CreateExecutor(calls, new FakeLogOff(calls), new FakeSwitch(calls), new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        SessionTeardownReport report = Execute(
            executor,
            plan,
            new SessionTeardownContext(ChildWtsSessionId: 0, ChildSessionsOpenedByUs: false, LockIsHeld: true, ProcessExitRequested: true));

        Assert.IsFalse(calls.Exists(call => call.StartsWith("logoff", StringComparison.Ordinal)));
        Assert.AreEqual(TeardownFailureReason.MissingChildSession, Find(report, SessionShutdownStep.LogOffChildSession).Failure);
    }

    [TestMethod]
    public void ARestoreStepWithoutTheSwitchOpenedByUsRefusesTheWholePlan()
    {
        List<string> calls = [];
        SessionTeardownExecutor executor = CreateExecutor(calls, new FakeLogOff(calls), new FakeSwitch(calls), new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: true,
            ChildSessionNeedsLogOff: false,
            LockIsHeld: true));

        Assert.ThrowsExactly<InvalidOperationException>(() => Execute(
            executor,
            plan,
            new SessionTeardownContext(null, ChildSessionsOpenedByUs: false, LockIsHeld: true, ProcessExitRequested: true)));

        Assert.IsEmpty(calls, "The plan is a caller bug, so it must be refused before the first effect, not recorded as a failed step.");
    }

    [TestMethod]
    public void APlanWithoutALogOffOrARestoreRunsOnlyClosingAndRelease()
    {
        List<string> calls = [];
        SessionTeardownExecutor executor = CreateExecutor(calls, new FakeLogOff(calls), new FakeSwitch(calls), new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: false,
            LockIsHeld: true));

        SessionTeardownReport report = Execute(
            executor,
            plan,
            new SessionTeardownContext(null, ChildSessionsOpenedByUs: false, LockIsHeld: true, ProcessExitRequested: true));

        CollectionAssert.AreEqual(new List<string> { "closing", "release" }, calls);
        CollectionAssert.AreEqual(
            new List<SessionShutdownStep>
            {
                SessionShutdownStep.EnterClosing,
                SessionShutdownStep.ReleaseLock,
                SessionShutdownStep.Exit,
            },
            StepList(report));
        Assert.IsFalse(report.AnyStepFailed);
    }

    [TestMethod]
    public void AnAlreadyCancelledTokenRunsNothing()
    {
        List<string> calls = [];
        SessionTeardownExecutor executor = CreateExecutor(calls, new FakeLogOff(calls), new FakeSwitch(calls), new FakeLockRelease(calls));
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: false,
            LockIsHeld: true));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => executor
            .ExecuteAsync(
                plan,
                new SessionTeardownContext(null, ChildSessionsOpenedByUs: false, LockIsHeld: true, ProcessExitRequested: true),
                cancelled.Token)
            .GetAwaiter()
            .GetResult());

        Assert.IsEmpty(calls, "A cancelled shutdown must not have run a single step.");
    }

    [TestMethod]
    public void TheReportCarriesEnumeratedReasonsOnly()
    {
        CollectionAssert.AreEquivalent(
            new List<TeardownFailureReason>
            {
                TeardownFailureReason.None,
                TeardownFailureReason.LogOffFailed,
                TeardownFailureReason.RestoreFailed,
                TeardownFailureReason.ReleaseFailed,
                TeardownFailureReason.StateTransitionFailed,
                TeardownFailureReason.MissingChildSession,
            },
            new List<TeardownFailureReason>(Enum.GetValues<TeardownFailureReason>()),
            "The report's reason set is fixed; adding free text would break the sanitized reporting rule.");

        Assert.IsEmpty(
            StringMembers(typeof(SessionTeardownReport)),
            "The report must not carry a string member, because a failure may not include free text, a path or an account identifier.");
        Assert.IsEmpty(
            StringMembers(typeof(SessionTeardownStepResult)),
            "A step result must not carry a string member either.");
    }

    [TestMethod]
    public void OnlyTheMissingChildSessionFailureIsAStateError()
    {
        Assert.IsNull(TeardownFailureCodes.ToSessionErrorCode(TeardownFailureReason.None));
        Assert.AreEqual(
            SessionErrorCode.State,
            TeardownFailureCodes.ToSessionErrorCode(TeardownFailureReason.MissingChildSession));
        Assert.IsNull(
            TeardownFailureCodes.ToSessionErrorCode(TeardownFailureReason.LogOffFailed),
            "The contract assigns no error code to a refused logoff, so the exit code and the reason carry it.");
        Assert.IsNull(TeardownFailureCodes.ToSessionErrorCode(TeardownFailureReason.RestoreFailed));
        Assert.IsNull(TeardownFailureCodes.ToSessionErrorCode(TeardownFailureReason.ReleaseFailed));
        Assert.IsNull(TeardownFailureCodes.ToSessionErrorCode(TeardownFailureReason.StateTransitionFailed));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => TeardownFailureCodes.ToSessionErrorCode((TeardownFailureReason)99));
    }

    [TestMethod]
    public void AStepResultCannotContradictItself()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SessionTeardownStepResult(
            SessionShutdownStep.ReleaseLock,
            executed: true,
            succeeded: true,
            TeardownFailureReason.ReleaseFailed));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionTeardownStepResult(
            SessionShutdownStep.ReleaseLock,
            executed: true,
            succeeded: false,
            TeardownFailureReason.None));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionTeardownStepResult(
            SessionShutdownStep.LogOffChildSession,
            executed: true,
            succeeded: false,
            TeardownFailureReason.RestoreFailed));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SessionTeardownStepResult(
            (SessionShutdownStep)99,
            executed: true,
            succeeded: true,
            TeardownFailureReason.None));
    }

    [TestMethod]
    public void AReportCannotHoldResultsOutOfOrderOrForAnEmptyPlan()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SessionTeardownReport(
            new List<SessionTeardownStepResult>(),
            lockWasHeld: true,
            processExitRequested: true));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionTeardownReport(
            new List<SessionTeardownStepResult>
            {
                Succeeded(SessionShutdownStep.ReleaseLock),
                Succeeded(SessionShutdownStep.EnterClosing),
            },
            lockWasHeld: true,
            processExitRequested: true));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionTeardownReport(
            new List<SessionTeardownStepResult>
            {
                Succeeded(SessionShutdownStep.EnterClosing),
                Succeeded(SessionShutdownStep.EnterClosing),
            },
            lockWasHeld: true,
            processExitRequested: true));
    }

    [TestMethod]
    public void EveryPortIsRequired()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SessionTeardownExecutor(null!, new FakeLogOff([]), new FakeSwitch([]), new FakeLockRelease([])));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SessionTeardownExecutor(new FakeStateTransition([]), null!, new FakeSwitch([]), new FakeLockRelease([])));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SessionTeardownExecutor(new FakeStateTransition([]), new FakeLogOff([]), null!, new FakeLockRelease([])));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SessionTeardownExecutor(new FakeStateTransition([]), new FakeLogOff([]), new FakeSwitch([]), null!));
    }

    /// <summary>The repository marker the structural scan anchors on, so the working directory does not matter.</summary>
    private const string RepositoryMarker = "SoulComputerUse.slnx";

    /// <summary>
    /// The only production files allowed to ask the session table itself to log a session off: the
    /// reviewed logoff port, the sweep, and the experiment's own sweep, which proves its targets with the
    /// same policy and re-reads the console session before every request.
    /// </summary>
    private static readonly string[] DirectTableLogOffFiles =
    [
        "src/SoulComputerUse.Windows/SessionLifecycle/Teardown/WtsChildSessionLogOff.cs",
        "src/SoulComputerUse.Windows/SessionLifecycle/Teardown/LeftoverSweeper.cs",
        "src/SoulComputerUse.RdpCredentialExperiment/ConnectRun.cs",
    ];

    [TestMethod]
    public void TheSessionTableIsAskedToLogOffOnlyByTheTwoReviewedCallers()
    {
        // The reviewed logoff port proves its target on every call and the sweep proves its own, so a
        // third caller that names a session id and calls the table directly is exactly the blind logoff
        // the review rules forbid: an id that was freed and reused would sign an innocent session off.
        string root = FindRepositoryRoot();
        List<string> sources = CollectSources(Path.Combine(root, "src"));
        Assert.IsGreaterThanOrEqualTo(10, sources.Count, "Too few sources were read, so a clean scan would prove nothing.");

        List<string> violations = [];
        foreach (string source in sources)
        {
            foreach (string line in File.ReadAllLines(source))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
                {
                    continue;
                }

                if (trimmed.Contains("table.LogOff(", StringComparison.OrdinalIgnoreCase)
                    && !DirectTableLogOffFiles.Contains(Relative(root, source), StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add(Relative(root, source));
                    break;
                }
            }
        }

        Assert.IsEmpty(
            violations,
            "Files outside the reviewed logoff port and the sweep ask the session table to log a session off directly ("
            + string.Join(", ", violations)
            + "). Route them through WtsChildSessionLogOff, which re-reads the table and proves the target first.");
    }

    [TestMethod]
    public void TheHostSendsBothOfItsLogOffPathsThroughTheReviewedPort()
    {
        // The contract close and the abort-path cleanup are the two places that name a child session id
        // on the host; both have to go through the reviewed port, or one of them keeps the blind logoff.
        string root = FindRepositoryRoot();
        string hostFile = Path.Combine(
            root,
            "src/SoulComputerUse.Host/NewSessionHost.cs".Replace('/', Path.DirectorySeparatorChar));

        Assert.IsTrue(File.Exists(hostFile), "The host's session mode source is missing, so its logoff wiring cannot be proved.");
        string host = File.ReadAllText(hostFile);

        Assert.IsGreaterThanOrEqualTo(
            2,
            CountOccurrences(host, "new WtsChildSessionLogOff("),
            "The host must build the reviewed logoff port on both the contract close and the unfinished-start cleanup; fewer than two means one path still bypasses the re-check.");
        Assert.IsGreaterThanOrEqualTo(
            1,
            CountOccurrences(host, "executor"),
            "The contract close must run through the teardown executor for this wiring to mean anything.");
    }

    /// <summary>Counts how often one text contains a needle.</summary>
    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static List<string> CollectSources(string sourceRoot)
    {
        List<string> sources = [];
        if (!Directory.Exists(sourceRoot))
        {
            return sources;
        }

        foreach (string candidate in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(candidate))
            {
                continue;
            }

            sources.Add(candidate);
        }

        return sources;
    }

    private static bool IsBuildOutput(string path)
    {
        return path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepositoryMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new AssertFailedException(
            $"Could not find {RepositoryMarker} above the test binary, so the teardown wiring cannot be checked.");
    }

    private static string Relative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static SessionTeardownExecutor CreateExecutor(
        List<string> calls,
        IChildSessionLogOff logOff,
        IChildSessionsSwitch childSessionsSwitch,
        ISingleInstanceLockRelease lockRelease)
    {
        return new SessionTeardownExecutor(new FakeStateTransition(calls), logOff, childSessionsSwitch, lockRelease);
    }

    private static SessionTeardownReport Execute(
        SessionTeardownExecutor executor,
        SessionShutdownPlan plan,
        SessionTeardownContext context)
    {
        return executor.ExecuteAsync(plan, context, CancellationToken.None).GetAwaiter().GetResult();
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

    private static List<SessionShutdownStep> StepList(SessionTeardownReport report)
    {
        List<SessionShutdownStep> steps = new(report.Steps.Count);
        foreach (SessionTeardownStepResult result in report.Steps)
        {
            steps.Add(result.Step);
        }

        return steps;
    }

    private static List<string> StringMembers(Type type)
    {
        List<string> members = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType == typeof(string))
            {
                members.Add(property.Name);
            }
        }

        return members;
    }

    private static SessionTeardownStepResult Succeeded(SessionShutdownStep step)
    {
        return new SessionTeardownStepResult(step, executed: true, succeeded: true, TeardownFailureReason.None);
    }

    private sealed class FakeStateTransition : ISessionStateTransition
    {
        private readonly List<string> _calls;

        internal FakeStateTransition(List<string> calls)
        {
            _calls = calls;
        }

        internal bool Fail { get; init; }

        public void EnterClosing()
        {
            _calls.Add("closing");
            if (Fail)
            {
                throw new InvalidOperationException("The state machine refused the closing transition.");
            }
        }
    }

    private sealed class FakeLogOff : IChildSessionLogOff
    {
        private readonly List<string> _calls;

        internal FakeLogOff(List<string> calls)
        {
            _calls = calls;
        }

        internal int FailOnSessionId { get; init; } = -1;

        public void LogOff(int wtsSessionId)
        {
            _calls.Add("logoff:" + wtsSessionId);
            if (wtsSessionId == FailOnSessionId)
            {
                throw new InvalidOperationException("The system refused the logoff request.");
            }
        }
    }

    private sealed class FakeSwitch : IChildSessionsSwitch
    {
        private readonly List<string> _calls;

        internal FakeSwitch(List<string> calls)
        {
            _calls = calls;
        }

        internal bool Fail { get; init; }

        public void Restore()
        {
            _calls.Add("restore");
            if (Fail)
            {
                throw new InvalidOperationException("The machine-wide switch stayed on after the change.");
            }
        }
    }

    private sealed class FakeLockRelease : ISingleInstanceLockRelease
    {
        private readonly List<string> _calls;

        internal FakeLockRelease(List<string> calls)
        {
            _calls = calls;
        }

        internal bool Fail { get; init; }

        public void Release()
        {
            _calls.Add("release");
            if (Fail)
            {
                throw new InvalidOperationException("The lock object survived the handle close.");
            }
        }
    }
}
