using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.ChildSessions;

/// <summary>Why the elevation attempt did not result in child sessions being enabled.</summary>
public enum ElevationFailure
{
    /// <summary>The operator dismissed the consent prompt.</summary>
    Declined = 0,

    /// <summary>The machine offered no elevation opportunity at all, for example a standard user without administrator credentials.</summary>
    Unavailable = 1,

    /// <summary>The helper ran but the system refused the change.</summary>
    HelperFailed = 2,
}

/// <summary>The outcome of asking the system to enable child sessions.</summary>
public sealed record ElevationResult(bool Enabled, ElevationFailure? Failure, int ErrorCode)
{
    /// <summary>True when the attempt did not enable the capability.</summary>
    public bool Failed => !Enabled;
}

/// <summary>
/// Enables child sessions through the minimal elevation helper, as the interface contract requires:
/// the helper is a separate executable started with the <c>runas</c> verb, its path is derived from
/// this program's own directory, and only one verb is ever passed to it.
/// </summary>
/// <remarks>
/// This type never holds a credential and never inspects the consent prompt beyond its Win32 result.
/// A dismissed prompt and a machine without an elevation opportunity are reported separately,
/// because the operator fixes them differently.
/// </remarks>
public static class ChildSessionsElevator
{
    /// <summary>The helper executable name, next to this program.</summary>
    public const string HelperFileName = "SoulComputerUse.ChildSessionsHelper.exe";

    private const int ErrorCancelled = 1223;
    private const int ErrorSuccess = 0;

    /// <summary>Derives the helper path from the running program's own directory.</summary>
    public static string ResolveHelperPath(string applicationDirectory)
    {
        if (string.IsNullOrEmpty(applicationDirectory))
        {
            throw new ArgumentException("The application directory must not be empty.", nameof(applicationDirectory));
        }

        string helperPath = Path.GetFullPath(Path.Combine(applicationDirectory, HelperFileName));
        if (!File.Exists(helperPath))
        {
            // The message names the helper only: the real installation path must never reach a
            // console, a screenshot or an issue.
            throw new FileNotFoundException(
                $"The elevation helper '{HelperFileName}' is missing from the application directory.",
                HelperFileName);
        }

        return helperPath;
    }

    /// <summary>Starts the helper elevated with the enable verb and reports what happened.</summary>
    public static ElevationResult EnableChildSessions(string applicationDirectory, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds),
                timeoutMilliseconds,
                "The timeout must be positive, otherwise a prompted elevation could wait forever.");
        }

        string helperPath = ResolveHelperPath(applicationDirectory);
        using ManualResetEventSlim started = new(false);
        int helperExitCode = -1;
        int launchError = ErrorSuccess;
        string? launchMessage = null;

        Thread helperThread = new(() =>
        {
            try
            {
                helperExitCode = RunHelper(helperPath, out launchError, out launchMessage);
            }
            finally
            {
                started.Set();
            }
        })
        {
            IsBackground = true,
            Name = "SoulComputerUse child sessions elevation",
        };
        helperThread.SetApartmentState(ApartmentState.STA);
        helperThread.Start();

        if (!started.Wait(timeoutMilliseconds))
        {
            throw new TimeoutException(
                $"The elevation prompt did not finish within {timeoutMilliseconds} ms, so the child-session state is unknown.");
        }

        if (launchError == ErrorCancelled)
        {
            return new ElevationResult(false, ElevationFailure.Declined, launchError);
        }

        if (launchMessage is not null)
        {
            return new ElevationResult(false, ElevationFailure.Unavailable, launchError);
        }

        return helperExitCode == ChildSessionsHelperContract.SuccessExitCode
            ? new ElevationResult(true, null, ErrorSuccess)
            : new ElevationResult(false, ElevationFailure.HelperFailed, helperExitCode);
    }

    private static int RunHelper(string helperPath, out int launchError, out string? launchMessage)
    {
        ShellExecuteInfo executeInfo = new()
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Verb = "runas",
            File = helperPath,
            Parameters = $"{ChildSessionsHelperContract.VerbSwitch} {ChildSessionsHelperContract.ToArgument(ChildSessionsHelperVerb.Enable)}",
            Show = ShowNormal,
            Mask = ShellExecuteMaskNoCloseProcess,
            Process = 0,
        };

        if (!NativeMethods.ShellExecuteExW(ref executeInfo))
        {
            launchError = Marshal.GetLastWin32Error();
            launchMessage = new Win32Exception(launchError).Message;
            return -1;
        }

        launchError = ErrorSuccess;
        launchMessage = null;
        try
        {
            uint waitResult = NativeMethods.WaitForSingleObject(executeInfo.Process, InfiniteWaitMilliseconds);
            if (waitResult != WaitObjectZero)
            {
                int waitError = Marshal.GetLastWin32Error();
                launchMessage = $"waiting for the helper did not report completion (Win32 error {waitError})";
                return -1;
            }

            // The helper writes no output, so its exit code is the whole result.
            return NativeMethods.GetExitCodeProcess(executeInfo.Process, out int exitCode) ? exitCode : -1;
        }
        finally
        {
            NativeMethods.CloseHandle(executeInfo.Process);
        }
    }

    private const int ShowNormal = 1;
    private const uint InfiniteWaitMilliseconds = 0xFFFFFFFF;
    private const uint WaitObjectZero = 0;
    private const uint ShellExecuteMaskNoCloseProcess = 0x00000040;
}