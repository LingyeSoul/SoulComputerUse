using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Windows.Hosting.Launch;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The host process starter against real Windows process and job objects.
/// </summary>
/// <remarks>
/// <para>
/// A job object with <c>KILL_ON_JOB_CLOSE</c> kills every process inside it when its last handle
/// closes, including the process that opened it. The cases that need the caller to be inside a job
/// therefore run the caller as a separate supervisor helper: the test owns the job handle, the helper
/// joins the job and starts the child through <see cref="HostProcessStarter"/>, and the test closes
/// the job while it stays safely outside. Every job is per-run and named like a test lock, so no
/// production object is touched.
/// </para>
/// <para>
/// Each helper is started with <c>--sleep</c> and tracked for cleanup: a helper that outlived its case
/// would be a dirty machine for the next run, which the suite refuses to start on.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class HostProcessStarterTests
{
    private const int ChildLifetimeMilliseconds = 3000;
    private const int SurviveCheckMilliseconds = 300;
    private const int SupervisorHoldMilliseconds = 20000;
    private const int SupervisorTimeoutMilliseconds = 15000;
    private const int HelperExitTimeoutMilliseconds = 10000;

    private const string StartedPrefix = "started ";
    private const string RefusedPrefix = "refused ";

    /// <summary><c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>: the job allows a child to leave it.</summary>
    private const uint BreakawayAllowed = 0x00000800;

    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: closing the last handle kills its members.</summary>
    private const uint KillOnJobClose = 0x00002000;

    private readonly List<int> _startedProcessIds = [];

    private static string TestSupportPath =>
        Path.Combine(AppContext.BaseDirectory, "SoulComputerUse.TestSupport.exe");

    [TestCleanup]
    public void StopStartedHelpers()
    {
        foreach (int processId in _startedProcessIds)
        {
            StopHelper(processId);
        }

        _startedProcessIds.Clear();
    }

    [TestMethod]
    public void TheStartedProcessRunsWithItsOwnStandardHandles()
    {
        HostProcessStarter starter = new();
        using IHostProcessHandle process = starter.Start(TestSupportPath, "--sleep 3000");
        _startedProcessIds.Add(process.ProcessId);
        Assert.IsGreaterThan(0, process.ProcessId);
        Assert.IsFalse(process.HasExited, "The helper was started with a 3000 ms sleep, so it must still be running.");
        Assert.IsNull(process.ExitCode);
    }

    /// <summary>
    /// A disposed handle owns no process handle any more, so it must refuse to answer instead of
    /// reporting a wait-result failure that hides the real cause.
    /// </summary>
    [TestMethod]
    public void ADisposedHandleRefusesFurtherReads()
    {
        HostProcessStarter starter = new();
        using IHostProcessHandle process = starter.Start(TestSupportPath, SleepArguments());
        _startedProcessIds.Add(process.ProcessId);

        process.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            _ = process.HasExited;
        });
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            _ = process.ExitCode;
        });
    }

    [TestMethod]
    public void AStartedProcessSurvivesTheCallersJobBeingClosed()
    {
        using TestJobObject job = TestJobObject.Create(NewJobName(), KillOnJobClose | BreakawayAllowed);
        using SupervisorRun run = RunSupervisor(job.Name, TestSupportPath, SleepArguments(), SupervisorHoldMilliseconds);
        _startedProcessIds.Add(run.Outcome.ProcessId);
        using Process child = Process.GetProcessById(run.Outcome.ProcessId);
        Assert.IsFalse(child.HasExited, "The helper must be running before the job is closed.");

        // The supervisor is still inside the job, so closing the job must kill it. Without that, a
        // surviving child would prove nothing: the job could simply have been empty.
        Assert.IsFalse(run.Supervisor.HasExited, "The supervisor must still be inside the job when the job closes.");
        job.Close();

        Assert.IsTrue(
            run.Supervisor.WaitForExit(SupervisorTimeoutMilliseconds),
            "Closing a kill-on-close job must kill the supervisor still inside it.");
        Thread.Sleep(SurviveCheckMilliseconds);

        Assert.IsFalse(
            child.HasExited,
            "The child must have left the caller's job, so closing that job must not kill it.");
    }

    [TestMethod]
    public void TheStartedProcessIsNoLongerInTheCallersJob()
    {
        using TestJobObject job = TestJobObject.Create(NewJobName(), KillOnJobClose | BreakawayAllowed);
        using SupervisorRun run = RunSupervisor(job.Name, TestSupportPath, SleepArguments(), holdMilliseconds: 0);
        _startedProcessIds.Add(run.Outcome.ProcessId);
        using Process child = Process.GetProcessById(run.Outcome.ProcessId);

        bool answered = NativeMethods.IsProcessInJob(child.Handle, job.Handle, out bool inJob);

        Assert.IsTrue(answered, "IsProcessInJob must answer for a live child and a live job.");
        Assert.IsFalse(inJob, "The started process must not remain in the caller's job.");
    }

    /// <summary>
    /// A job that forbids breakaway makes <c>CreateProcessW</c> fail with <c>ERROR_ACCESS_DENIED</c>.
    /// The starter must refuse with the fixed message instead of retrying without the flag, because a
    /// host the caller can kill is the outcome the breakaway exists to prevent.
    /// </summary>
    [TestMethod]
    public void AJobThatForbidsBreakawayIsRefusedInsteadOfRetried()
    {
        using TestJobObject job = TestJobObject.Create(NewJobName(), KillOnJobClose);
        using SupervisorRun run = RunSupervisor(job.Name, TestSupportPath, SleepArguments(), holdMilliseconds: 0);

        Assert.IsFalse(
            run.Outcome.Started,
            "A job without BREAKAWAY_OK must refuse the start rather than produce a host it can kill.");
        Assert.AreEqual(
            "The calling process runs in a job that does not allow breakaway, so the host cannot outlive this process.",
            run.Outcome.Message,
            "The refusal message is a fixed template.");
    }

    /// <summary>
    /// The started process must not hold the caller's standard output pipe. The helper starts it and
    /// exits at once; if the child had inherited the pipe, reading that pipe to end of file would stay
    /// blocked for the child's whole lifetime, and an agent reading the MCP server's output would
    /// never learn the server had finished.
    /// </summary>
    [TestMethod]
    public void TheStartedProcessDoesNotHoldTheCallersStandardOutputOpen()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = TestSupportPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--start");
        startInfo.ArgumentList.Add(TestSupportPath);
        startInfo.ArgumentList.Add("--sleep " + ChildLifetimeMilliseconds.ToString(CultureInfo.InvariantCulture));

        using Process caller = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The starting helper did not start.");
        string? outcomeLine = caller.StandardOutput.ReadLine();
        _startedProcessIds.Add(ReadStartedProcessId(outcomeLine));

        Assert.IsTrue(
            caller.StandardOutput.ReadToEndAsync().Wait(TimeSpan.FromSeconds(1)),
            "Reading the caller's standard output must reach end of file as soon as the caller exits. "
            + "It stayed open, so the started process holds the caller's pipe.");
    }

    private static int ReadStartedProcessId(string? outcomeLine)
    {
        if (outcomeLine is null || !outcomeLine.StartsWith(StartedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The starting helper reported no process: '{outcomeLine}'.");
        }

        return ReadProcessId(outcomeLine);
    }

    private static int ReadProcessId(string startedLine)
    {
        string idText = startedLine[StartedPrefix.Length..];
        if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId)
            || processId <= 0)
        {
            throw new InvalidOperationException($"The helper reported an unreadable process id: '{startedLine}'.");
        }

        return processId;
    }

    private static string NewJobName()
    {
        return @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
    }

    private static string SleepArguments()
    {
        return "--sleep " + ChildLifetimeMilliseconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Runs the supervisor helper, which joins <paramref name="jobName"/> and then calls the starter.
    /// The helper reports one line: <c>started &lt;pid&gt;</c> or <c>refused &lt;message&gt;</c>, and
    /// then stays alive inside the job for <paramref name="holdMilliseconds"/>.
    /// </summary>
    private static SupervisorRun RunSupervisor(
        string jobName,
        string executablePath,
        string arguments,
        int holdMilliseconds)
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
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add(arguments);
        startInfo.ArgumentList.Add(holdMilliseconds.ToString(CultureInfo.InvariantCulture));

        Process supervisor = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The job supervisor helper did not start.");
        string? outcomeLine = supervisor.StandardOutput.ReadLine();
        if (outcomeLine is null)
        {
            int exitCode = TryReadExitCode(supervisor);
            string error = ReadAvailableError(supervisor);
            supervisor.Dispose();
            throw new InvalidOperationException(
                $"The job supervisor helper reported no outcome. Exit code {exitCode}. Stderr: {error}");
        }

        return new SupervisorRun(supervisor, ReadOutcome(outcomeLine));
    }

    /// <summary>Reads the exit code when the helper has already ended, and reports -1 otherwise.</summary>
    private static int TryReadExitCode(Process process)
    {
        return process.HasExited ? process.ExitCode : -1;
    }

    /// <summary>
    /// Reads whatever the helper has written to stderr, bounded so a helper that is still holding a
    /// long sleep cannot stall the failure report.
    /// </summary>
    private static string ReadAvailableError(Process process)
    {
        Task<string> reading = process.StandardError.ReadToEndAsync();
        return reading.Wait(TimeSpan.FromSeconds(2)) ? reading.Result : "<still running>";
    }

    private static SupervisorOutcome ReadOutcome(string outcomeLine)
    {
        if (outcomeLine.StartsWith(StartedPrefix, StringComparison.Ordinal))
        {
            return new SupervisorOutcome(started: true, ReadProcessId(outcomeLine), message: string.Empty);
        }

        if (outcomeLine.StartsWith(RefusedPrefix, StringComparison.Ordinal))
        {
            return new SupervisorOutcome(started: false, processId: 0, message: outcomeLine[RefusedPrefix.Length..]);
        }

        throw new InvalidOperationException($"The job supervisor reported an unknown outcome: '{outcomeLine}'.");
    }

    private static void StopHelper(int processId)
    {
        Process helper;
        try
        {
            helper = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // The helper's own sleep already ended, so there is nothing left to stop.
            return;
        }

        using (helper)
        {
            if (helper.HasExited)
            {
                return;
            }

            helper.Kill(entireProcessTree: true);
            if (!helper.WaitForExit(HelperExitTimeoutMilliseconds))
            {
                throw new InvalidOperationException(
                    $"Helper process {processId} did not exit after being killed, so it would outlive this run.");
            }
        }
    }

    /// <summary>The supervisor helper's single-line answer.</summary>
    private sealed class SupervisorOutcome
    {
        internal SupervisorOutcome(bool started, int processId, string message)
        {
            Started = started;
            ProcessId = processId;
            Message = message;
        }

        internal bool Started { get; }

        internal int ProcessId { get; }

        internal string Message { get; }
    }

    /// <summary>
    /// A running supervisor helper and the answer it reported. Disposing kills it, so a case that
    /// never closes the job still leaves no helper behind.
    /// </summary>
    private sealed class SupervisorRun : IDisposable
    {
        private readonly Process _supervisor;

        internal SupervisorRun(Process supervisor, SupervisorOutcome outcome)
        {
            _supervisor = supervisor;
            Outcome = outcome;
        }

        internal Process Supervisor => _supervisor;

        internal SupervisorOutcome Outcome { get; }

        public void Dispose()
        {
            try
            {
                if (!_supervisor.HasExited)
                {
                    _supervisor.Kill(entireProcessTree: true);
                    if (!_supervisor.WaitForExit(HelperExitTimeoutMilliseconds))
                    {
                        throw new InvalidOperationException(
                            "The job supervisor helper did not exit after being killed, so it would outlive this run.");
                    }
                }
            }
            finally
            {
                _supervisor.Dispose();
            }
        }
    }

    /// <summary>
    /// A per-run job object the test owns. Closing it is the caller's job closing in the scenario the
    /// starter has to survive.
    /// </summary>
    private sealed class TestJobObject : IDisposable
    {
        private nint _handle;

        private TestJobObject(string name, nint handle)
        {
            Name = name;
            _handle = handle;
        }

        internal string Name { get; }

        internal nint Handle => _handle;

        internal static TestJobObject Create(string name, uint limitFlags)
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

            return new TestJobObject(name, handle);
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
                throw NativeMethods.InteropFailure(
                    "CloseHandle for the test job",
                    Marshal.GetLastWin32Error());
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
