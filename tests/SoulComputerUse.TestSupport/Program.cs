using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using SoulComputerUse.Windows.Hosting.Launch;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.TestSupport;

/// <summary>
/// Helper process for tests that need a second real process: one that holds a named lock until it
/// exits, one that stays alive for a requested time, or one that joins a caller-created job and then
/// starts another process through the product's own starter.
/// </summary>
/// <remarks>
/// The lock and job names are always supplied by the test and are never production names: a leftover
/// helper must be unable to disturb a real session on the machine. The job verb exists because a
/// process inside a <c>KILL_ON_JOB_CLOSE</c> job dies when that job's last handle closes, so the test
/// that closes such a job must not be the process inside it.
/// </remarks>
internal static class Program
{
    private const int LockUnavailableExitCode = 2;
    private const int BadArgumentsExitCode = 64;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--hold-lock")
        {
            return HoldLock(args);
        }

        if (args.Length > 0 && args[0] == "--sleep")
        {
            return Sleep(args);
        }

        if (args.Length > 0 && args[0] == "--start-in-job")
        {
            return StartInJob(args);
        }

        if (args.Length > 0 && args[0] == "--start")
        {
            return StartDetached(args);
        }

        WriteUsage();
        return BadArgumentsExitCode;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The helper must keep the lock handle open until the process ends, which is the whole point of this test helper.")]
    private static int HoldLock(string[] args)
    {
        if (args.Length != 3)
        {
            WriteUsage();
            return BadArgumentsExitCode;
        }

        if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int holdMilliseconds)
            || holdMilliseconds < 0)
        {
            Console.Error.WriteLine("--hold-lock needs a non-negative whole number of milliseconds.");
            return BadArgumentsExitCode;
        }

        SingleInstanceLock held = SingleInstanceLock.Acquire(args[1]);
        if (!held.IsHeld)
        {
            Console.Error.WriteLine("the named lock was already held");
            return LockUnavailableExitCode;
        }

        Console.Out.WriteLine("held");
        Console.Out.Flush();
        Thread.Sleep(holdMilliseconds);

        // Exiting without disposing leaves the handle to be closed by the kernel at process exit.
        Environment.Exit(0);
        return 0;
    }

    private static int Sleep(string[] args)
    {
        if (args.Length != 2
            || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sleepMilliseconds)
            || sleepMilliseconds < 0)
        {
            Console.Error.WriteLine("--sleep needs a non-negative whole number of milliseconds.");
            return BadArgumentsExitCode;
        }

        Thread.Sleep(sleepMilliseconds);
        return 0;
    }

    /// <summary>
    /// Joins the named job the test created, then calls the real <see cref="HostProcessStarter"/>.
    /// This makes the helper the job-bound caller, so the test stays outside a kill-on-close job while
    /// it closes one. The single answer line is <c>started &lt;pid&gt;</c> or <c>refused &lt;message&gt;</c>.
    /// With a non-zero hold the helper then stays inside the job, so closing that job really kills a
    /// member and a surviving child proves it left.
    /// </summary>
    /// <remarks>
    /// Only the breakaway refusal is reported as <c>refused</c>. Any other start failure is a genuine
    /// defect, so it escapes to the exit code and stderr instead of being dressed up as a refusal the
    /// test would then accept.
    /// </remarks>
    private static int StartInJob(string[] args)
    {
        if (args.Length != 5
            || !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int holdMilliseconds)
            || holdMilliseconds < 0)
        {
            WriteUsage();
            return BadArgumentsExitCode;
        }

        if (!JoinJob(args[1]))
        {
            Console.Error.WriteLine("the named test job could not be joined");
            return LockUnavailableExitCode;
        }

        HostProcessStarter starter = new();
        try
        {
            using IHostProcessHandle started = starter.Start(args[2], args[3]);
            Console.Out.WriteLine("started " + started.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (InvalidOperationException error)
        {
            Console.Out.WriteLine("refused " + error.Message);
        }

        Console.Out.Flush();
        Thread.Sleep(holdMilliseconds);
        return 0;
    }

    /// <summary>
    /// Starts the requested executable through the real <see cref="HostProcessStarter"/> and exits
    /// immediately, without waiting for it. This is the caller whose standard output the started
    /// process must not hold open: the test reads this helper's output pipe to end of file, and a
    /// started process that inherited the pipe would keep that read blocked for its whole lifetime.
    /// </summary>
    private static int StartDetached(string[] args)
    {
        if (args.Length != 3)
        {
            WriteUsage();
            return BadArgumentsExitCode;
        }

        HostProcessStarter starter = new();
        using IHostProcessHandle started = starter.Start(args[1], args[2]);
        Console.Out.WriteLine("started " + started.ProcessId.ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        return 0;
    }

    /// <summary>
    /// Puts this process into the named job and closes its own job handle again. The job stays alive
    /// because the test holds a handle to it; membership belongs to the process, not to this handle.
    /// </summary>
    private static bool JoinJob(string jobName)
    {
        nint job = JobNative.OpenJobObjectW(JobNative.JobObjectAssignProcess, false, jobName);
        if (job == 0)
        {
            int openError = Marshal.GetLastWin32Error();
            Console.Error.WriteLine(
                "OpenJobObjectW failed with Win32 error " + openError.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        try
        {
            if (!JobNative.AssignProcessToJobObject(job, JobNative.GetCurrentProcess()))
            {
                int assignError = Marshal.GetLastWin32Error();
                Console.Error.WriteLine(
                    "AssignProcessToJobObject failed with Win32 error " + assignError.ToString(CultureInfo.InvariantCulture));
                return false;
            }
        }
        finally
        {
            JobNative.CloseHandle(job);
        }

        return true;
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            "usage: SoulComputerUse.TestSupport --hold-lock <lock-name> <milliseconds>"
            + " | --sleep <milliseconds>"
            + " | --start-in-job <job-name> <executable> <arguments> <hold-milliseconds>"
            + " | --start <executable> <arguments>");
    }

    /// <summary>The kernel32 calls the job verb needs.</summary>
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use 'LibraryImport' attributes instead of 'DllImport'",
        Justification = "Two plain kernel32 calls in a test helper, kept beside the verb that uses them.")]
    private static class JobNative
    {
        internal const uint JobObjectAssignProcess = 0x00000001;

        [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern nint OpenJobObjectW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint handle);
    }
}
