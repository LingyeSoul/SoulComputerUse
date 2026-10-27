using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The helpers this run started, so the suite can collect exactly those and nothing else. A guard that
/// swept by process name would kill helpers another test run is legitimately still using.
/// </summary>
internal static class HelperProcessRegistry
{
    private static readonly object Gate = new();
    private static readonly List<Process> Started = [];

    internal static void Register(Process helper)
    {
        lock (Gate)
        {
            Started.Add(helper);
        }
    }

    internal static IReadOnlyList<Process> TakeAll()
    {
        lock (Gate)
        {
            List<Process> taken = [.. Started];
            Started.Clear();
            return taken;
        }
    }
}

/// <summary>
/// Suite-level machine-state guard. The integration suite touches a real kernel lock object and real
/// security policy values, so it must start from a clean machine and leave no helper of its own behind:
/// one surviving helper keeps its lock handle open and makes later cases fail for the wrong reason.
/// </summary>
[TestClass]
public sealed class IntegrationSuiteLifecycle
{
    private const string HelperProcessName = "SoulComputerUse.TestSupport";
    private const int HelperExitTimeoutMilliseconds = 5000;
    private const int HelperExitTimeoutSeconds = HelperExitTimeoutMilliseconds / 1000;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 from windef.h.</summary>
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>
    /// Makes the test process DPI-aware before any case runs, and proves it worked. Without this the
    /// host is DPI-unaware, every coordinate it reads is virtualized, and a case whose name claims to
    /// run "at the machine DPI" would silently be proving its own assumption instead of the product.
    /// </summary>
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use 'LibraryImport' attributes instead of 'DllImport'",
        Justification = "One trivial user32 call, kept next to the check that consumes it.")]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    /// <summary>
    /// Fails before any case runs when the machine is already dirty. It reports what is dirty and never
    /// kills anything: another test run's helpers are legitimate and killing them would be destructive.
    /// </summary>
    [AssemblyInitialize]
    public static void VerifyCleanMachineBeforeTheSuite(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequirePerMonitorV2();

        List<string> problems = [];

        Process[] orphanHelpers = Process.GetProcessesByName(HelperProcessName);
        foreach (Process orphan in orphanHelpers)
        {
            using (orphan)
            {
                if (orphan.Id != Environment.ProcessId)
                {
                    problems.Add(DescribeHelper(orphan) + " is already running");
                }
            }
        }

        try
        {
            if (SingleInstanceLock.IsHeldByAnyone())
            {
                problems.Add("the production single-instance lock object already exists");
            }
        }
        catch (InvalidOperationException error)
        {
            problems.Add($"the production lock state could not be read: {error.Message}");
        }

        // A logged-on child session answers the next connection without asking for a logon, which the
        // contract forbids. The check lives here so the suite says so once, instead of failing later in
        // cases that read the session table and cannot explain what they saw.
        try
        {
            List<SessionCandidate> candidates = [];
            foreach (WtsSessionEntry entry in new WtsSessionTable().Read())
            {
                candidates.Add(new SessionCandidate(entry.Id, entry.StationName, entry.State));
            }

            IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(
                candidates,
                WtsSessionProbe.GetActiveConsoleSessionId());
            if (leftovers.Count > 0)
            {
                List<string> ids = [];
                foreach (SessionCandidate leftover in leftovers)
                {
                    ids.Add(leftover.Id.ToString(CultureInfo.InvariantCulture));
                }

                problems.Add(
                    "child session id(s) " + string.Join(", ", ids)
                    + " are still logged on; log them off first, or the next connection would skip the logon gate");
            }
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            problems.Add($"the session table could not be read: {error.Message}");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"The integration suite refuses to start on a dirty machine: {string.Join("; ", problems)}. "
                + "Nothing was killed, because a helper from another run may still be in use.");
        }
    }

    /// <summary>
    /// Collects only the helpers this run registered. A helper that will not exit is reported as a
    /// failure instead of being allowed to slip away and poison a later run.
    /// </summary>
    [AssemblyCleanup]
    public static void CollectHelpersThisRunStarted()
    {
        List<string> stuck = [];

        foreach (Process helper in HelperProcessRegistry.TakeAll())
        {
            using (helper)
            {
                int processId = TryReadId(helper);
                if (processId == 0)
                {
                    continue;
                }

                try
                {
                    if (!helper.WaitForExit(HelperExitTimeoutMilliseconds))
                    {
                        helper.Kill(entireProcessTree: true);
                        if (!helper.WaitForExit(HelperExitTimeoutMilliseconds))
                        {
                            stuck.Add(processId.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // The case disposed its own process object, so this helper is already gone.
                }
            }
        }

        if (stuck.Count > 0)
        {
            throw new InvalidOperationException(
                $"Helpers {string.Join(", ", stuck)} would not exit after being killed, so the machine is left holding their lock handles.");
        }
    }

    /// <summary>
    /// Sets Per-Monitor V2 awareness for the test process and refuses to run when it did not take
    /// effect. A DPI-unaware host sees virtualized coordinates, so any case that claims to measure real
    /// pixels would be proving its own assumption; failing here is the only honest outcome.
    /// </summary>
    private static void RequirePerMonitorV2()
    {
        bool applied = SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        int errorCode = Marshal.GetLastWin32Error();

        DpiAwareness actual = DpiProbe.GetThreadAwareness();
        if (actual != DpiAwareness.PerMonitorAware)
        {
            throw new InvalidOperationException(
                $"The integration suite requires per-monitor DPI awareness, but the process reports {actual}. "
                + $"SetProcessDpiAwarenessContext returned {applied} with Win32 error {errorCode}. "
                + "Every pixel assertion in this suite would otherwise compare virtualized coordinates.");
        }
    }

    private static string DescribeHelper(Process helper)
    {
        string started = "<unknown>";
        try
        {
            started = helper.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            // The process object no longer refers to a live process; the id alone is still useful.
        }

        return $"helper process id {helper.Id.ToString(CultureInfo.InvariantCulture)} (started {started})";
    }

    private static int TryReadId(Process helper)
    {
        try
        {
            return helper.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}