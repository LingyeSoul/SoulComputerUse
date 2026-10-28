using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Launch;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The launch path against real machine state: the real launcher's bounded wait ending because a real
/// helper process exited, the same-process start gate through the real launcher construction path, a
/// real held test lock explained by a real ownership record, and the job breakaway of the real
/// process starter repeated at suite level.
/// </summary>
/// <remarks>
/// Every case uses per-run names of the form <c>Global\SoulComputerUse-Test-{guid}</c> and
/// <c>SoulComputerUse.owner.test-{guid}</c>, so the contract-pinned production lock and the
/// production owner pipe are never touched. Every helper process is registered with
/// <see cref="HelperProcessRegistry"/> so the suite's own cleanup collects it, and the case cleanup
/// stops helpers by their own <see cref="Process"/> objects and reports every process that refuses
/// to exit instead of stopping the sweep at the first failure.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class HostLaunchIntegrationTests
{
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";
    private const string NewSessionArgument = "--new-session";
    private const string StartedPrefix = "started ";

    private const int ChildSessionId = 15;
    private const int HelperSleepMilliseconds = 1500;
    private const int ChildLifetimeMilliseconds = 3000;
    private const int LockHolderHoldMilliseconds = 5000;
    private const int HelperExitTimeoutMilliseconds = 10000;

    /// <summary>
    /// The launcher's wait budget is thirty seconds, so a start that took longer than this could not
    /// have been ended by the helper's exit; it would be the bounded timeout instead.
    /// </summary>
    private const int WellInsideTheWaitMilliseconds = 25000;

    /// <summary><c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>: the job allows a child to leave it.</summary>
    private const uint BreakawayAllowed = 0x00000800;

    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: closing the last handle kills its members.</summary>
    private const uint KillOnJobClose = 0x00002000;

    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(30);

    private static readonly CompositeFormat HostExitedBeforeLockFormat =
        CompositeFormat.Parse(HostLaunchMessages.HostExitedBeforeLock);

    private readonly List<Process> _startedHelpers = [];

    private string _temporaryRoot = string.Empty;
    private string _lockName = string.Empty;
    private string _pipeName = string.Empty;

    private static string TestSupportPath =>
        Path.Combine(AppContext.BaseDirectory, "SoulComputerUse.TestSupport.exe");

    [TestInitialize]
    public void CreatePerRunState()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "scu-host-launch-" + Guid.NewGuid().ToString("N"));
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
        _pipeName = "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_temporaryRoot);
    }

    /// <summary>
    /// Stops every helper this case started and then proves the case's own lock object is gone. A
    /// helper that refuses to exit is reported, but it never stops the sweep of the others.
    /// </summary>
    [TestCleanup]
    public void StopHelpersAndAssertNothingIsLeftBehind()
    {
        List<string> stuck = StopStartedHelpers();
        try
        {
            Assert.IsFalse(
                SingleInstanceLock.IsHeldByAnyone(_lockName),
                "The case's own lock object must be gone afterwards, otherwise a helper leaked a handle.");
        }
        finally
        {
            if (Directory.Exists(_temporaryRoot))
            {
                Directory.Delete(_temporaryRoot, recursive: true);
            }
        }

        if (stuck.Count > 0)
        {
            throw new InvalidOperationException(
                $"Helpers {string.Join(", ", stuck)} did not exit, so they would outlive this run.");
        }
    }

    /// <summary>
    /// A real helper process started through the real process starter that exits before taking the
    /// lock must end the launcher's bounded wait with <c>E_HOST_LAUNCH_FAILED</c> carrying the
    /// helper's exit code, not a success and not the timeout.
    /// </summary>
    [TestMethod]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Every helper this case starts is owned and disposed by the case cleanup, which also stops it by its process object.")]
    public async Task AStartedHelperThatExitsBeforeTakingTheLockFailsTheStart()
    {
        RecordingProbe probe = new(lockExists: false, pipeReachable: false, liveOwnerStarting: false);
        HelperAsHostStarter starter = new(new HostProcessStarter(), this);
        string applicationDirectory = CreateApplicationDirectory();
        HostProcessLauncher launcher = CreateLauncher(probe, starter, new BlockingWaitClock(), applicationDirectory);

        Stopwatch elapsed = Stopwatch.StartNew();
        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => launcher.StartAsync(CancellationToken.None));
        elapsed.Stop();

        Assert.AreEqual(SessionErrorCode.HostLaunchFailed, error.Code);
        Assert.IsFalse(error.Retryable, "The contract marks E_HOST_LAUNCH_FAILED as not retryable.");
        Assert.AreEqual(
            string.Format(CultureInfo.InvariantCulture, HostExitedBeforeLockFormat, 0),
            error.Message,
            "The message must carry the helper's real exit code, which is the only failure evidence a "
            + "starter without inherited handles can offer.");
        Assert.IsLessThan(
            WellInsideTheWaitMilliseconds,
            elapsed.ElapsedMilliseconds,
            "The start must fail as soon as the helper exits instead of waiting out the thirty seconds.");
        Assert.AreEqual(1, starter.StartCount);
        Assert.AreEqual(NewSessionArgument, starter.SeenArguments, "The launcher must keep passing its one production argument.");
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(applicationDirectory, HostExecutablePath.FileName)),
            starter.SeenExecutablePath,
            "The launcher must resolve the host executable from its application directory itself.");
        Assert.IsNotNull(starter.LastHandle, "The failed start must have produced one real process handle.");
        Assert.AreEqual(1, starter.LastHandle.DisposeCount, "A failed start must still close the process handle.");
        Assert.AreEqual(2, probe.PipeProbeCalls, "The exit path must re-probe the pipe exactly once before failing.");
        Assert.AreEqual(0, probe.LiveOwnerStartingCalls, "A free lock must never cost an ownership-record read.");
    }

    /// <summary>
    /// Two starts on one launcher must queue inside the process: the first one launches the host, and
    /// the second one observes the lock and the pipe that host took and reports
    /// <see cref="HostStartOutcome.AlreadyRunning"/> instead of opening a second host window.
    /// </summary>
    [TestMethod]
    public async Task TwoConcurrentStartsProduceOneLaunch()
    {
        RecordingProbe probe = new(lockExists: false, pipeReachable: false, liveOwnerStarting: false);
        using ManualResetEventSlim starterEntered = new(false);
        using ManualResetEventSlim releaseStarter = new(false);
        FlipOnStartStarter starter = new(probe)
        {
            OnStart = () =>
            {
                starterEntered.Set();
                releaseStarter.Wait();
            },
        };
        HostProcessLauncher launcher = CreateLauncher(probe, starter, new ImmediateWaitClock(), CreateApplicationDirectory());

        Task<HostStartOutcome> first = Task.Run(() => launcher.StartAsync(CancellationToken.None));
        Assert.IsTrue(
            starterEntered.Wait(TimeSpan.FromSeconds(5)),
            "The first start must reach the process creation step before the second one begins.");
        Task<HostStartOutcome> second = launcher.StartAsync(CancellationToken.None);

        try
        {
            Assert.IsFalse(
                second.IsCompleted,
                "A second call in the same process must wait behind the first instead of launching another host.");
        }
        finally
        {
            releaseStarter.Set();
        }

        HostStartOutcome[] outcomes = await Task.WhenAll(first, second);

        Assert.AreEqual(1, starter.StartCount, "One process must never open two host windows.");
        CollectionAssert.AreEquivalent(
            new List<HostStartOutcome> { HostStartOutcome.Started, HostStartOutcome.AlreadyRunning },
            outcomes,
            "The first start owns the launch, and the queued second one observes the host that launch created.");
        Assert.AreEqual(HostStartOutcome.Started, outcomes[0]);
        Assert.AreEqual(HostStartOutcome.AlreadyRunning, outcomes[1]);
    }

    /// <summary>
    /// A real held test lock explained by a real ownership record that names the live helper must read
    /// as a session that is still starting, so discovery refuses with the retryable in-progress
    /// decision; once the helper has ended, the same record must stop counting as a live owner.
    /// </summary>
    [TestMethod]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The lock holder is owned and disposed by the case cleanup, which also stops it by its process object.")]
    public async Task AHeldTestLockOwnedByALiveProcessIsReportedAsStarting()
    {
        Process holder = StartLockHolder(LockHolderHoldMilliseconds);
        SessionOwnerStore store = new(Path.Combine(_temporaryRoot, "records"), TestSid);
        store.Write(new SessionOwnerRecord(
            Guid.NewGuid().ToString("D"),
            holder.Id,
            new DateTimeOffset(holder.StartTime.ToUniversalTime()),
            ChildSessionId,
            false,
            SessionState.Starting,
            DateTimeOffset.UtcNow));
        OwnerProbe probe = new(_lockName, _pipeName, store);

        Assert.IsTrue(probe.LockExists(), "The helper must really hold the per-run test lock.");
        bool pipeReachable = await probe.IsOwnerPipeReachableAsync(CancellationToken.None).WaitAsync(CaseTimeout);
        Assert.IsFalse(pipeReachable, "No pipe server exists on the per-run pipe name, so the pipe must stay silent.");
        Assert.IsTrue(
            probe.IsLiveOwnerStarting(),
            "A held lock plus a record naming a live process in the starting state is a start in progress.");

        OwnerDiscoveryDecision inProgress = OwnerDiscovery.Decide(
            probe.LockExists(),
            pipeReachable,
            probe.IsLiveOwnerStarting());
        Assert.AreEqual(
            OwnerDiscoveryDecision.RefuseStartInProgress,
            inProgress,
            "A live owner that is still starting must be the retryable in-progress refusal.");

        StopHelper(holder);

        Assert.IsFalse(
            probe.IsLiveOwnerStarting(),
            "Once the recorded owner has ended, the same record must no longer answer as a live starting owner.");
        Assert.IsNotNull(
            store.Read(),
            "The probe must observe the record without taking over, deleting or rewriting it.");

        using SingleInstanceLock reacquired = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(reacquired.IsHeld, "The test process must be able to take the lock the dead helper released.");

        OwnerDiscoveryDecision heldByNobody = OwnerDiscovery.Decide(
            probe.LockExists(),
            pipeReachable,
            probe.IsLiveOwnerStarting());
        Assert.AreEqual(
            OwnerDiscoveryDecision.RefuseLockHeld,
            heldByNobody,
            "A held lock whose record no longer names a live starter must stay the strict held-lock refusal.");
    }

    /// <summary>
    /// The job case of the process-starter suite repeated here: the child the real process starter
    /// creates for a job-bound caller must not remain inside a <c>KILL_ON_JOB_CLOSE</c> job, so
    /// closing that job can never kill the host and leave a logged-on child session behind.
    /// </summary>
    [TestMethod]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The supervisor and the child are owned and disposed by the case cleanup, which also stops them by their process objects.")]
    public void TheStartedProcessIsNotInsideTheCallersJob()
    {
        using TestJob job = TestJob.Create(NewJobName(), KillOnJobClose | BreakawayAllowed);
        Process supervisor = RunSupervisor(job.Name, holdMilliseconds: 0);

        string? outcomeLine = supervisor.StandardOutput.ReadLine();
        if (outcomeLine is null)
        {
            string error = ReadAvailableError(supervisor);
            throw new InvalidOperationException($"The job supervisor helper reported no outcome. Stderr: {error}");
        }

        Process child = TrackHelper(Process.GetProcessById(ReadStartedProcessId(outcomeLine)));

        bool answered = NativeMethods.IsProcessInJob(child.Handle, job.Handle, out bool inJob);

        Assert.IsTrue(answered, "IsProcessInJob must answer for a live child and a live job.");
        Assert.IsFalse(inJob, "The started process must not remain in the caller's job.");
    }

    private static HostProcessLauncher CreateLauncher(
        RecordingProbe probe,
        IHostProcessStarter starter,
        IWaitClock clock,
        string applicationDirectory)
    {
        return new HostProcessLauncher(
            probe,
            new AllowingConsoleSessionGuard(),
            new AllowingElevationGuard(),
            starter,
            clock,
            applicationDirectory);
    }

    /// <summary>
    /// Creates a per-run application directory holding a stub file under the host executable's name.
    /// The launcher's resolution requires exactly that file; the starter ports in these cases start
    /// the TestSupport helper instead, so the stub is never executed.
    /// </summary>
    private string CreateApplicationDirectory()
    {
        string directory = Path.Combine(_temporaryRoot, "app");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, HostExecutablePath.FileName), [0x4D, 0x5A]);
        return directory;
    }

    /// <summary>
    /// Starts the real lock-holder helper and proves it holds the per-run test lock before returning.
    /// </summary>
    private Process StartLockHolder(int holdMilliseconds)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = TestSupportPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--hold-lock");
        startInfo.ArgumentList.Add(_lockName);
        startInfo.ArgumentList.Add(holdMilliseconds.ToString(CultureInfo.InvariantCulture));

        Process holder = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The lock holder helper did not start.");
        TrackHelper(holder);

        string? readiness = holder.StandardOutput.ReadLine();
        if (readiness != "held")
        {
            string error = ReadAvailableError(holder);
            throw new InvalidOperationException(
                $"The lock holder helper did not take the test lock. First line: '{readiness}'. Stderr: {error}");
        }

        return holder;
    }

    /// <summary>
    /// Runs the real job supervisor helper: it joins <paramref name="jobName"/>, starts the child
    /// through the real <see cref="HostProcessStarter"/>, reports <c>started &lt;pid&gt;</c> and stays
    /// inside the job for <paramref name="holdMilliseconds"/>.
    /// </summary>
    private Process RunSupervisor(string jobName, int holdMilliseconds)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = TestSupportPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--start-in-job");
        startInfo.ArgumentList.Add(jobName);
        startInfo.ArgumentList.Add(TestSupportPath);
        startInfo.ArgumentList.Add("--sleep " + ChildLifetimeMilliseconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(holdMilliseconds.ToString(CultureInfo.InvariantCulture));

        Process supervisor = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The job supervisor helper did not start.");
        return TrackHelper(supervisor);
    }

    private static int ReadStartedProcessId(string outcomeLine)
    {
        if (!outcomeLine.StartsWith(StartedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The job supervisor helper reported an unexpected outcome: '{outcomeLine}'.");
        }

        string idText = outcomeLine[StartedPrefix.Length..];
        if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId) || processId <= 0)
        {
            throw new InvalidOperationException($"The job supervisor helper reported an unreadable process id: '{outcomeLine}'.");
        }

        return processId;
    }

    /// <summary>
    /// Reads whatever the helper has written to stderr, bounded so a helper that is still holding a
    /// long sleep cannot stall the failure report.
    /// </summary>
    private static string ReadAvailableError(Process helper)
    {
        Task<string> reading = helper.StandardError.ReadToEndAsync();
        return reading.Wait(TimeSpan.FromSeconds(2)) ? reading.Result : "<still running>";
    }

    /// <summary>
    /// Takes ownership of a helper for the rest of the case and registers it with the suite-level
    /// cleanup, which collects exactly the helpers this run started.
    /// </summary>
    private Process TrackHelper(Process helper)
    {
        HelperProcessRegistry.Register(helper);
        _startedHelpers.Add(helper);
        return helper;
    }

    /// <summary>
    /// Stops every helper this case started, by its own <see cref="Process"/> object rather than a
    /// re-resolved process id, and reports every process that refuses to exit instead of aborting the
    /// sweep at the first failure. A stuck helper is left undisposed so the suite-level cleanup can
    /// try again and report it too.
    /// </summary>
    private List<string> StopStartedHelpers()
    {
        List<string> stuck = [];
        foreach (Process helper in _startedHelpers)
        {
            try
            {
                if (helper.HasExited)
                {
                    helper.Dispose();
                    continue;
                }

                helper.Kill(entireProcessTree: true);
                if (!helper.WaitForExit(HelperExitTimeoutMilliseconds))
                {
                    stuck.Add(Describe(helper) + " did not exit after being killed, so it still holds its lock handle");
                    continue;
                }

                helper.Dispose();
            }
            catch (InvalidOperationException)
            {
                // The process object no longer refers to a live process, so there is nothing to stop.
                helper.Dispose();
            }
            catch (Win32Exception error)
            {
                // The kill was refused while the process may still be running, so this is a stuck helper.
                stuck.Add(
                    Describe(helper)
                    + " could not be killed (Win32 error "
                    + error.NativeErrorCode.ToString(CultureInfo.InvariantCulture)
                    + ")");
            }
        }

        _startedHelpers.Clear();
        return stuck;
    }

    /// <summary>
    /// Stops one helper by its own process object and proves it exited. The object stays in the
    /// case's list so the cleanup still disposes it.
    /// </summary>
    private static void StopHelper(Process helper)
    {
        if (helper.HasExited)
        {
            return;
        }

        helper.Kill(entireProcessTree: true);
        if (!helper.WaitForExit(HelperExitTimeoutMilliseconds))
        {
            throw new InvalidOperationException(
                $"Helper process {Describe(helper)} did not exit after being killed, so it still holds its lock handle.");
        }
    }

    private static string Describe(Process helper)
    {
        try
        {
            return "process id " + helper.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "a helper whose process object no longer refers to a live process";
        }
    }

    private static string NewJobName()
    {
        return @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
    }

    /// <summary>A probe whose three observations are fixed by the case and that counts every read.</summary>
    private sealed class RecordingProbe : IOwnerProbe
    {
        internal RecordingProbe(bool lockExists, bool pipeReachable, bool liveOwnerStarting)
        {
            LockExistsResult = lockExists;
            PipeReachable = pipeReachable;
            LiveOwnerStarting = liveOwnerStarting;
        }

        internal bool LockExistsResult { get; set; }

        internal bool PipeReachable { get; set; }

        internal bool LiveOwnerStarting { get; set; }

        internal int PipeProbeCalls { get; private set; }

        internal int LiveOwnerStartingCalls { get; private set; }

        public bool LockExists()
        {
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

    /// <summary>
    /// Runs the real <see cref="HostProcessStarter"/> on the TestSupport helper instead of the product
    /// host, and records what the launcher asked for. The helper is not a host, so it cannot understand
    /// the production <c>--new-session</c> argument; this port starts it with its own <c>--sleep</c>
    /// verb, which is the real process whose exit the launcher then observes.
    /// </summary>
    private sealed class HelperAsHostStarter : IHostProcessStarter
    {
        private readonly HostProcessStarter _real;
        private readonly HostLaunchIntegrationTests _owner;
        private int _startCount;

        internal HelperAsHostStarter(HostProcessStarter real, HostLaunchIntegrationTests owner)
        {
            _real = real;
            _owner = owner;
        }

        internal int StartCount => Volatile.Read(ref _startCount);

        internal string? SeenExecutablePath { get; private set; }

        internal string? SeenArguments { get; private set; }

        internal ObservedHandle? LastHandle { get; private set; }

        public IHostProcessHandle Start(string executablePath, string arguments)
        {
            Interlocked.Increment(ref _startCount);
            SeenExecutablePath = executablePath;
            SeenArguments = arguments;

            ObservedHandle handle = new(
                _real.Start(
                    TestSupportPath,
                    "--sleep " + HelperSleepMilliseconds.ToString(CultureInfo.InvariantCulture)));
            LastHandle = handle;
            _owner.TrackHelper(Process.GetProcessById(handle.ProcessId));
            return handle;
        }
    }

    /// <summary>The real process handle plus a count of how often the launcher disposed it.</summary>
    private sealed class ObservedHandle : IHostProcessHandle
    {
        private readonly IHostProcessHandle _inner;

        internal ObservedHandle(IHostProcessHandle inner)
        {
            _inner = inner;
        }

        internal int DisposeCount { get; private set; }

        public int ProcessId => _inner.ProcessId;

        public bool HasExited => _inner.HasExited;

        public int? ExitCode => _inner.ExitCode;

        public void Dispose()
        {
            DisposeCount++;
            _inner.Dispose();
        }
    }

    /// <summary>
    /// A starter that models the first caller's host taking both the lock and the pipe and reports a
    /// process that stays alive. The case's gate decides when its one recorded start returns.
    /// </summary>
    private sealed class FlipOnStartStarter : IHostProcessStarter
    {
        private readonly RecordingProbe _probe;
        private int _startCount;

        internal FlipOnStartStarter(RecordingProbe probe)
        {
            _probe = probe;
        }

        internal int StartCount => Volatile.Read(ref _startCount);

        internal Action? OnStart { get; set; }

        public IHostProcessHandle Start(string executablePath, string arguments)
        {
            Interlocked.Increment(ref _startCount);

            // The started host took the lock and began serving the pipe before this call returned;
            // the queued second caller must observe exactly that.
            _probe.LockExistsResult = true;
            _probe.PipeReachable = true;

            OnStart?.Invoke();
            return new FakeHandle();
        }
    }

    /// <summary>A process handle for a host the case models as running for the whole start.</summary>
    private sealed class FakeHandle : IHostProcessHandle
    {
        public int ProcessId => 4242;

        public bool HasExited => false;

        public int? ExitCode => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A wait clock that returns at once, so the queued-caller case never really sleeps.</summary>
    private sealed class ImmediateWaitClock : IWaitClock
    {
        public void Wait(TimeSpan duration)
        {
        }
    }

    /// <summary>The console-session guard that allows, so the case reaches the launch step itself.</summary>
    private sealed class AllowingConsoleSessionGuard : IConsoleSessionGuard
    {
        public bool IsCurrentProcessOnConsoleSession() => true;
    }

    /// <summary>The elevation guard that allows, so the case reaches the launch step itself.</summary>
    private sealed class AllowingElevationGuard : IElevationGuard
    {
        public bool IsCallerElevated() => false;
    }

    /// <summary>
    /// A per-run job object the test owns. The supervisor helper joins it and starts the child; the
    /// child must not remain inside it.
    /// </summary>
    private sealed class TestJob : IDisposable
    {
        private nint _handle;

        private TestJob(string name, nint handle)
        {
            Name = name;
            _handle = handle;
        }

        internal string Name { get; }

        internal nint Handle => _handle;

        internal static TestJob Create(string name, uint limitFlags)
        {
            nint handle = JobObjectNative.CreateJobObjectW(0, name);
            if (handle == 0)
            {
                throw NativeMethods.InteropFailure(
                    "CreateJobObjectW for the test job",
                    Marshal.GetLastWin32Error());
            }

            JobObjectNative.ExtendedLimitInformation information = new();
            information.BasicLimitInformation.LimitFlags = limitFlags;
            if (!JobObjectNative.SetInformationJobObject(
                    handle,
                    JobObjectNative.JobObjectExtendedLimitInformation,
                    ref information,
                    Marshal.SizeOf<JobObjectNative.ExtendedLimitInformation>()))
            {
                int errorCode = Marshal.GetLastWin32Error();
                NativeMethods.CloseHandle(handle);
                throw NativeMethods.InteropFailure("SetInformationJobObject for the test job", errorCode);
            }

            return new TestJob(name, handle);
        }

        internal void Close()
        {
            nint handle = _handle;
            _handle = 0;
            if (handle == 0)
            {
                return;
            }

            if (!NativeMethods.CloseHandle(handle))
            {
                throw NativeMethods.InteropFailure("CloseHandle for the test job", Marshal.GetLastWin32Error());
            }
        }

        public void Dispose()
        {
            Close();
        }
    }

    /// <summary>The kernel32 job-object calls the test job needs.</summary>
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use 'LibraryImport' attributes instead of 'DllImport'",
        Justification = "Two plain kernel32 calls in a test fixture, kept beside the job they manage.")]
    private static class JobObjectNative
    {
        internal const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern nint CreateJobObjectW(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            nint job,
            int informationClass,
            ref ExtendedLimitInformation information,
            int length);

        /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize;
            internal nuint MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        /// <summary><c>IO_COUNTERS</c>, part of the extended limit structure.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ExtendedLimitInformation
        {
            internal BasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal nuint ProcessMemoryLimit;
            internal nuint JobMemoryLimit;
            internal nuint PeakProcessMemoryUsed;
            internal nuint PeakJobMemoryUsed;
        }
    }
}
