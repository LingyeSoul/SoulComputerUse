using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Launch;
using SoulComputerUse.Windows.Hosting.Startup.Split;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The launcher's decision table: an answering owner is idempotent success, a live starting owner is
/// retryable, a silent held lock stays a hard refusal, both launch guards run before any process starts,
/// and the wait ends only when the lock appears, the host exits, or thirty seconds pass.
/// </summary>
/// <remarks>
/// Every port is a fake, so no case takes a real lock, starts a real process, reads a real record or
/// sleeps. The wait clock returns at once, which makes the thirty-second bound a counted sequence of
/// polls rather than a slow test.
/// </remarks>
[TestClass]
public sealed class HostProcessLauncherTests
{
    private const string NewSessionArguments = "--new-session";
    private const int StartupPollCount = 150;

    private static readonly CompositeFormat HostExitedBeforeLockFormat =
        CompositeFormat.Parse(HostLaunchMessages.HostExitedBeforeLock);
    private static readonly CompositeFormat TimedOutFormat =
        CompositeFormat.Parse(HostLaunchMessages.TimedOut);

    [TestMethod]
    public async Task AnAnsweringOwnerMakesStartIdempotent()
    {
        using LaunchFixture fixture = new();
        fixture.Probe.LockExistsResult = true;
        fixture.Probe.PipeReachable = true;

        HostStartOutcome outcome = await fixture.CreateLauncher().StartAsync(CancellationToken.None);

        Assert.AreEqual(HostStartOutcome.AlreadyRunning, outcome);
        Assert.AreEqual(0, fixture.Starter.StartCount, "An answering owner must never lead to a second host.");
        Assert.AreEqual(0, fixture.Clock.WaitCount, "A healthy owner must not wait for anything.");
        Assert.AreEqual(
            0,
            fixture.Probe.LiveOwnerStartingCalls,
            "A reachable pipe is enough; reading the ownership record would make the healthy path expensive.");
    }

    [TestMethod]
    public async Task AStartingOwnerAsksTheCallerToRetryInsteadOfLaunchingASecondWindow()
    {
        using LaunchFixture fixture = new();
        fixture.Probe.LockExistsResult = true;
        fixture.Probe.PipeReachable = false;
        fixture.Probe.LiveOwnerStarting = true;

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => fixture.CreateLauncher().StartAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.NotReady, error.Code);
        Assert.IsTrue(error.Retryable, "A session owner that is still starting is retryable in the contract.");
        Assert.AreEqual(OwnerSessionResolver.StartInProgressMessage, error.Message);
        Assert.AreEqual(0, fixture.Starter.StartCount, "A starting owner must never lead to a second host.");
        Assert.AreEqual(1, fixture.Probe.LiveOwnerStartingCalls);
        Assert.AreEqual(0, fixture.Clock.WaitCount);
    }

    [TestMethod]
    public async Task AHeldLockWithADeadRecordStillFailsWithLockHeld()
    {
        using LaunchFixture fixture = new();
        fixture.Probe.LockExistsResult = true;
        fixture.Probe.PipeReachable = false;
        fixture.Probe.LiveOwnerStarting = false;

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => fixture.CreateLauncher().StartAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.LockHeld, error.Code);
        Assert.IsFalse(error.Retryable, "The contract marks a held lock as not retryable.");
        Assert.AreEqual(
            OwnerSessionResolver.LockHeldMessage,
            error.Message,
            "The launcher and owner discovery must report a held lock with the same fixed text.");
        Assert.AreEqual(0, fixture.Starter.StartCount, "A held lock must never start a second host.");
        Assert.AreEqual(1, fixture.Probe.LiveOwnerStartingCalls);
        Assert.AreEqual(0, fixture.Clock.WaitCount);
    }

    [TestMethod]
    public async Task AnElevatedCallerIsRefusedBeforeAnyProcessStarts()
    {
        using LaunchFixture fixture = new();
        fixture.ElevationGuard.CallerElevated = true;

        SessionOperationException error = await AssertHostLaunchFailureAsync(
            fixture.CreateLauncher(),
            HostLaunchMessages.CallerElevated);

        StringAssert.Contains(error.Message, "elevated");
        Assert.AreEqual(1, fixture.ConsoleGuard.CallCount, "The console session is checked before elevation.");
        Assert.AreEqual(1, fixture.ElevationGuard.CallCount);
        Assert.AreEqual(0, fixture.Starter.StartCount, "An elevated caller must be refused before any process starts.");
    }

    [TestMethod]
    public async Task ACallerOutsideTheConsoleSessionIsRefused()
    {
        using LaunchFixture fixture = new();
        fixture.ConsoleGuard.OnConsoleSession = false;

        SessionOperationException error = await AssertHostLaunchFailureAsync(
            fixture.CreateLauncher(),
            HostLaunchMessages.NotOnConsoleSession);

        StringAssert.Contains(error.Message, "console session");
        Assert.AreEqual(1, fixture.ConsoleGuard.CallCount);
        Assert.AreEqual(0, fixture.ElevationGuard.CallCount, "A refusal must stop at the first failed guard.");
        Assert.AreEqual(0, fixture.Starter.StartCount, "A caller off the console session must not start a process.");
    }

    [TestMethod]
    public async Task AMissingHostExecutableFailsWithTheFixedFileName()
    {
        using LaunchFixture fixture = new();
        fixture.RemoveHostExecutable();

        SessionOperationException error = await AssertHostLaunchFailureAsync(
            fixture.CreateLauncher(),
            HostLaunchMessages.HostExecutableMissing);

        StringAssert.Contains(error.Message, HostExecutablePath.FileName);
        Assert.DoesNotContain(fixture.ApplicationDirectory, error.Message, "The message must not carry an installation path.");
        Assert.AreEqual(0, fixture.Starter.StartCount);
    }

    [TestMethod]
    public async Task AHostThatCannotBeCreatedFailsTheStart()
    {
        await AssertCreationFailureAsync(new InvalidOperationException("The job forbids breakaway."));
        await AssertCreationFailureAsync(new Win32Exception(5));
    }

    [TestMethod]
    public async Task AHostThatExitsBeforeTakingTheLockFailsTheStart()
    {
        using LaunchFixture fixture = new();
        fixture.Starter.TakeLockOnStart = false;
        fixture.Starter.ServePipeOnStart = false;
        fixture.Starter.Handle = new FakeHandle(hasExited: true, exitCode: 3);

        SessionOperationException error = await AssertHostLaunchFailureAsync(
            fixture.CreateLauncher(),
            string.Format(CultureInfo.InvariantCulture, HostExitedBeforeLockFormat, 3));

        StringAssert.Contains(error.Message, "3", "The message must carry the exit code.");
        Assert.AreEqual(1, fixture.Starter.StartCount);
        Assert.AreEqual(1, fixture.Clock.WaitCount, "The first poll observes the exit before a second wait.");
        Assert.AreEqual(2, fixture.Probe.PipeProbeCalls, "The exit path re-probes the pipe once before failing.");
        Assert.AreEqual(1, fixture.Starter.Handle.DisposeCount, "A failed start must still close the process handle.");
    }

    [TestMethod]
    public async Task AHostThatExitsWithoutAnExitCodeReportsUnknown()
    {
        using LaunchFixture fixture = new();
        fixture.Starter.TakeLockOnStart = false;
        fixture.Starter.ServePipeOnStart = false;
        fixture.Starter.Handle = new FakeHandle(hasExited: true, exitCode: null);

        SessionOperationException error = await AssertHostLaunchFailureAsync(
            fixture.CreateLauncher(),
            string.Format(CultureInfo.InvariantCulture, HostExitedBeforeLockFormat, "unknown"));

        StringAssert.Contains(error.Message, "unknown");
        Assert.AreEqual(1, fixture.Starter.Handle.DisposeCount);
    }

    [TestMethod]
    public async Task AHostThatLosesTheCrossProcessRaceIsAlreadyRunning()
    {
        using LaunchFixture fixture = new();
        fixture.Starter.TakeLockOnStart = false;
        fixture.Starter.ServePipeOnStart = false;
        fixture.Starter.Handle = new FakeHandle(hasExited: true, exitCode: 1);
        fixture.Starter.OnStart = () => fixture.Probe.PipeReachable = true;

        HostStartOutcome outcome = await fixture.CreateLauncher().StartAsync(CancellationToken.None);

        Assert.AreEqual(
            HostStartOutcome.AlreadyRunning,
            outcome,
            "A host that exited because another owner won the lock is success for the caller.");
        Assert.AreEqual(1, fixture.Starter.StartCount);
        Assert.AreEqual(1, fixture.Starter.Handle.DisposeCount);
    }

    [TestMethod]
    public async Task TheLockAppearingMeansTheStartSucceeded()
    {
        using LaunchFixture fixture = new();

        HostStartOutcome outcome = await fixture.CreateLauncher().StartAsync(CancellationToken.None);

        Assert.AreEqual(HostStartOutcome.Started, outcome);
        Assert.AreEqual(1, fixture.Starter.StartCount);
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(fixture.ApplicationDirectory, HostExecutablePath.FileName)),
            fixture.Starter.ExecutablePath);
        Assert.AreEqual(NewSessionArguments, fixture.Starter.Arguments);
        Assert.AreEqual(1, fixture.Clock.WaitCount, "The lock is observed on the first poll after starting.");
        Assert.AreEqual(
            0,
            fixture.Probe.LiveOwnerStartingCalls,
            "A free lock must never cost an ownership-record read.");
        Assert.AreEqual(1, fixture.Starter.Handle.DisposeCount, "The success path closes the handle without killing the host.");
    }

    [TestMethod]
    public async Task WaitingForTheLockStopsAfterThirtySeconds()
    {
        using LaunchFixture fixture = new();
        fixture.Starter.TakeLockOnStart = false;
        fixture.Starter.ServePipeOnStart = false;
        fixture.Starter.Handle = new FakeHandle(hasExited: false, exitCode: null);

        SessionOperationException error = await AssertHostLaunchFailureAsync(
            fixture.CreateLauncher(),
            string.Format(CultureInfo.InvariantCulture, TimedOutFormat, 30));

        StringAssert.Contains(error.Message, "30", "The message must carry the timeout in seconds.");
        Assert.AreEqual(StartupPollCount, fixture.Clock.WaitCount, "The wait must stop after exactly thirty seconds of 200 ms polls.");
        Assert.AreEqual(
            StartupPollCount + 1,
            fixture.Probe.LockExistsCalls,
            "Every wait must be followed by a fresh lock read: one before starting and one per poll.");
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), fixture.Clock.LastDuration);
        Assert.AreEqual(1, fixture.Starter.Handle.DisposeCount);
    }

    [TestMethod]
    public async Task ConcurrentStartsInOneProcessQueueInsteadOfOpeningTwoWindows()
    {
        using LaunchFixture fixture = new();
        using ManualResetEventSlim startEntered = new(false);
        using ManualResetEventSlim releaseStart = new(false);
        fixture.Starter.OnStart = () =>
        {
            startEntered.Set();
            releaseStart.Wait(TimeSpan.FromSeconds(30));
        };
        HostProcessLauncher launcher = fixture.CreateLauncher();

        Task<HostStartOutcome> first = Task.Run(() => launcher.StartAsync(CancellationToken.None));
        Task<HostStartOutcome> second;
        try
        {
            Assert.IsTrue(startEntered.Wait(TimeSpan.FromSeconds(5)), "The first start must reach the process creation step.");
            second = launcher.StartAsync(CancellationToken.None);

            Assert.IsFalse(
                second.IsCompleted,
                "A second call in the same process must wait for the first instead of launching another host.");
        }
        finally
        {
            releaseStart.Set();
        }

        HostStartOutcome[] outcomes = await Task.WhenAll(first, second);

        Assert.AreEqual(HostStartOutcome.Started, outcomes[0]);
        Assert.AreEqual(HostStartOutcome.AlreadyRunning, outcomes[1]);
        Assert.AreEqual(1, fixture.Starter.StartCount, "One process must never open two host windows.");
        Assert.AreEqual(1, fixture.Starter.Handle.DisposeCount);
    }

    private static async Task AssertCreationFailureAsync(Exception failure)
    {
        using LaunchFixture fixture = new();
        fixture.Starter.Failure = failure;

        SessionOperationException error = await AssertHostLaunchFailureAsync(
            fixture.CreateLauncher(),
            HostLaunchMessages.ProcessCreationFailed);

        StringAssert.Contains(error.Message, "could not be created");
        StringAssert.Contains(
            error.Message,
            "start the host once by hand with '" + HostExecutablePath.FileName + " --new-session'",
            "The refused-job case must name the by-hand start the operator still has.");
        Assert.AreEqual(1, fixture.Starter.StartCount);
        Assert.AreEqual(
            1,
            fixture.Probe.LockExistsCalls,
            "A refused start must stop before the wait re-reads the lock, so only the initial probe reads it.");
    }

    private static async Task<SessionOperationException> AssertHostLaunchFailureAsync(
        HostProcessLauncher launcher,
        string expectedMessage)
    {
        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => launcher.StartAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.HostLaunchFailed, error.Code);
        Assert.IsFalse(error.Retryable, "The contract marks E_HOST_LAUNCH_FAILED as not retryable.");
        Assert.AreEqual(expectedMessage, error.Message);
        return error;
    }

    /// <summary>One temporary application directory plus every port the launcher takes.</summary>
    private sealed class LaunchFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory;

        internal LaunchFixture()
        {
            _directory = new TemporaryDirectory();
            File.WriteAllBytes(Path.Combine(_directory.Path, HostExecutablePath.FileName), [0x4D, 0x5A]);
            Probe = new FakeProbe(lockExists: false, pipeReachable: false, liveOwnerStarting: false);
            Starter = new FakeStarter(Probe);
            Clock = new FakeClock();
            ConsoleGuard = new FakeConsoleSessionGuard(onConsoleSession: true);
            ElevationGuard = new FakeElevationGuard(callerElevated: false);
        }

        internal FakeProbe Probe { get; }

        internal FakeStarter Starter { get; }

        internal FakeClock Clock { get; }

        internal FakeConsoleSessionGuard ConsoleGuard { get; }

        internal FakeElevationGuard ElevationGuard { get; }

        internal string ApplicationDirectory => _directory.Path;

        internal HostProcessLauncher CreateLauncher()
        {
            return new HostProcessLauncher(Probe, ConsoleGuard, ElevationGuard, Starter, Clock, ApplicationDirectory);
        }

        internal void RemoveHostExecutable()
        {
            File.Delete(Path.Combine(_directory.Path, HostExecutablePath.FileName));
        }

        public void Dispose()
        {
            _directory.Dispose();
        }
    }

    /// <summary>A probe whose three observations can be set per case and that counts every read.</summary>
    private sealed class FakeProbe : IOwnerProbe
    {
        internal FakeProbe(bool lockExists, bool pipeReachable, bool liveOwnerStarting)
        {
            LockExistsResult = lockExists;
            PipeReachable = pipeReachable;
            LiveOwnerStarting = liveOwnerStarting;
        }

        internal bool LockExistsResult { get; set; }

        internal bool PipeReachable { get; set; }

        internal bool LiveOwnerStarting { get; set; }

        internal int LockExistsCalls { get; private set; }

        internal int PipeProbeCalls { get; private set; }

        internal int LiveOwnerStartingCalls { get; private set; }

        public bool LockExists()
        {
            LockExistsCalls++;
            return LockExistsResult;
        }

        public Task<bool> IsOwnerPipeReachableAsync(CancellationToken cancellationToken)
        {
            PipeProbeCalls++;
            return Task.FromResult(PipeReachable);
        }

        public bool IsLiveOwnerStarting()
        {
            LiveOwnerStartingCalls++;
            return LiveOwnerStarting;
        }
    }

    /// <summary>A starter that records the call and can model success, exit or creation failure.</summary>
    private sealed class FakeStarter : IHostProcessStarter
    {
        private readonly FakeProbe _probe;
        private int _startCount;

        internal FakeStarter(FakeProbe probe)
        {
            _probe = probe;
        }

        internal int StartCount => Volatile.Read(ref _startCount);

        internal string? ExecutablePath { get; private set; }

        internal string? Arguments { get; private set; }

        internal Exception? Failure { get; set; }

        internal bool TakeLockOnStart { get; set; } = true;

        internal bool ServePipeOnStart { get; set; } = true;

        internal Action? OnStart { get; set; }

        internal FakeHandle Handle { get; set; } = new(hasExited: false, exitCode: null);

        public IHostProcessHandle Start(string executablePath, string arguments)
        {
            Interlocked.Increment(ref _startCount);
            ExecutablePath = executablePath;
            Arguments = arguments;

            if (Failure is not null)
            {
                throw Failure;
            }

            if (TakeLockOnStart)
            {
                _probe.LockExistsResult = true;
            }

            if (ServePipeOnStart)
            {
                _probe.PipeReachable = true;
            }

            OnStart?.Invoke();
            return Handle;
        }
    }

    /// <summary>A process handle whose exit state is fixed by the case and that counts disposal.</summary>
    private sealed class FakeHandle : IHostProcessHandle
    {
        internal FakeHandle(bool hasExited, int? exitCode)
        {
            HasExited = hasExited;
            ExitCode = exitCode;
        }

        public int ProcessId => 4242;

        public bool HasExited { get; set; }

        public int? ExitCode { get; set; }

        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    /// <summary>A clock that returns at once but records how many waits the launcher asked for.</summary>
    private sealed class FakeClock : IWaitClock
    {
        internal int WaitCount { get; private set; }

        internal TimeSpan LastDuration { get; private set; }

        public void Wait(TimeSpan duration)
        {
            WaitCount++;
            LastDuration = duration;
        }
    }

    private sealed class FakeConsoleSessionGuard : IConsoleSessionGuard
    {
        internal FakeConsoleSessionGuard(bool onConsoleSession)
        {
            OnConsoleSession = onConsoleSession;
        }

        internal bool OnConsoleSession { get; set; }

        internal int CallCount { get; private set; }

        public bool IsCurrentProcessOnConsoleSession()
        {
            CallCount++;
            return OnConsoleSession;
        }
    }

    private sealed class FakeElevationGuard : IElevationGuard
    {
        internal FakeElevationGuard(bool callerElevated)
        {
            CallerElevated = callerElevated;
        }

        internal bool CallerElevated { get; set; }

        internal int CallCount { get; private set; }

        public bool IsCallerElevated()
        {
            CallCount++;
            return CallerElevated;
        }
    }
}
