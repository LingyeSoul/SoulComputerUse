using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Starts the host executable with <c>CreateProcessW</c>, with no inherited handles and outside the
/// caller's job object.
/// </summary>
/// <remarks>
/// <para>
/// Both properties are deliberate, and each fixes a concrete failure.
/// </para>
/// <para>
/// <b>No inherited handles.</b> A child that inherits the caller's standard output keeps that pipe
/// open after the caller exits, so an agent reading the MCP server's output never sees end of file
/// and waits forever. The process is created with <c>bInheritHandles: FALSE</c>, and — because that
/// flag alone does not stop <c>CreateProcessW</c> from copying the caller's standard handles into the
/// child — with <c>STARTF_USESTDHANDLES</c> and all three standard handles set to
/// <c>INVALID_HANDLE_VALUE</c>. The cost is that the starter gets no channel other than the exit
/// code: a host that fails to start can only show its own dialog, and a start that died before it
/// took the lock is visible as a non-zero exit code and nothing more.
/// </para>
/// <para>
/// <b>Breakaway from the caller's job.</b> Windows puts a child in its parent's job object by default,
/// and a job with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> kills everything inside it when the agent
/// exits. A host killed that way leaves a logged-on child session behind, which the contract forbids.
/// When the caller is in a job the child is created with <c>CREATE_BREAKAWAY_FROM_JOB</c>. If that job
/// does not allow breakaway the start fails with a fixed message and is never retried without the
/// flag: a host the caller can kill is exactly the outcome this type exists to prevent.
/// </para>
/// <para>
/// The returned handle observes the process only. Reading <see cref="IHostProcessHandle.HasExited"/>
/// or <see cref="IHostProcessHandle.ExitCode"/> closes nothing and consumes no wait state, so the
/// launcher may poll them; the handle is not thread safe, and the launcher reads it from one thread.
/// </para>
/// </remarks>
public sealed class HostProcessStarter : IHostProcessStarter
{
    /// <summary>
    /// The fixed refusal for a job that does not allow breakaway. It names no process, account or
    /// path: the operator only needs to know that this environment cannot start a durable host.
    /// </summary>
    private const string BreakawayRefusedMessage =
        "The calling process runs in a job that does not allow breakaway, so the host cannot outlive this process.";

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="executablePath"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The caller's job forbids breakaway, or the process could not be created.
    /// </exception>
    public IHostProcessHandle Start(string executablePath, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        uint creationFlags = NativeMethods.CreateNewProcessGroup;
        if (IsCallerInAJob())
        {
            creationFlags |= NativeMethods.CreateBreakawayFromJob;
        }

        StartupInfo startupInfo = new()
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = NativeMethods.StartfUseShowWindow | NativeMethods.StartfUseStdHandles,
            ShowWindow = NativeMethods.SwShowNormal,
            StandardInput = NativeMethods.InvalidHandleValue,
            StandardOutput = NativeMethods.InvalidHandleValue,
            StandardError = NativeMethods.InvalidHandleValue,
        };

        // The child's complete path is the application name, and the command line repeats it quoted:
        // the loader must never search PATH for a name the caller handed over as a complete path.
        // CreateProcessW may rewrite the command line in place, so it gets a private native copy.
        nint commandLine = Marshal.StringToHGlobalUni(BuildCommandLine(executablePath, arguments));
        bool created;
        ProcessInformation processInformation;
        int createError;
        try
        {
            created = NativeMethods.CreateProcessW(
                executablePath,
                commandLine,
                0,
                0,
                false,
                creationFlags,
                0,
                null,
                ref startupInfo,
                out processInformation);
            createError = created ? 0 : Marshal.GetLastWin32Error();
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
        }

        if (!created)
        {
            throw CreateProcessFailure(creationFlags, createError);
        }

        ProcessHandle handle = new(processInformation.Process, processInformation.ProcessId);

        // The thread handle is only needed to let the primary thread begin running, which starting
        // the process already did. Keeping it open would leak a handle for the host's whole lifetime.
        if (!NativeMethods.CloseHandle(processInformation.Thread))
        {
            int errorCode = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new InvalidOperationException(
                $"CloseHandle for the host's primary thread failed with Win32 error {errorCode}, so the handle would leak.",
                new Win32Exception(errorCode));
        }

        return handle;
    }

    /// <summary>
    /// Reports whether this process belongs to any job object. A job is what lets the caller's exit
    /// take the child with it, so the answer decides whether breakaway is required.
    /// </summary>
    private static bool IsCallerInAJob()
    {
        if (!NativeMethods.IsProcessInJob(NativeMethods.GetCurrentProcess(), 0, out bool inJob))
        {
            throw NativeMethods.InteropFailure(
                "IsProcessInJob for the calling process",
                Marshal.GetLastWin32Error());
        }

        return inJob;
    }

    /// <summary>
    /// Builds the command line from the quoted executable path and the caller's arguments.
    /// </summary>
    private static string BuildCommandLine(string executablePath, string arguments)
    {
        string quotedPath = "\"" + executablePath + "\"";
        return arguments.Length == 0 ? quotedPath : quotedPath + " " + arguments;
    }

    /// <summary>
    /// Turns a failed <c>CreateProcessW</c> into one explicit error. <c>ERROR_ACCESS_DENIED</c> while
    /// breakaway was requested means the caller's job forbids leaving it; that case has its own fixed
    /// message because the operator can act on it, while every other code is a plain interop failure.
    /// </summary>
    private static InvalidOperationException CreateProcessFailure(uint creationFlags, int errorCode)
    {
        if (errorCode == NativeMethods.ErrorAccessDenied
            && (creationFlags & NativeMethods.CreateBreakawayFromJob) != 0)
        {
            return new InvalidOperationException(BreakawayRefusedMessage, new Win32Exception(errorCode));
        }

        return NativeMethods.InteropFailure("CreateProcessW for the host", errorCode);
    }

    /// <summary>
    /// A live observation of one started process. It owns the process handle and nothing else.
    /// </summary>
    /// <remarks>
    /// Not thread safe by design: the launcher polls it from one thread. <see cref="HasExited"/> and
    /// <see cref="ExitCode"/> are plain queries, so a second read repeats the same answer instead of
    /// consuming a wait. Reading either after <see cref="Dispose"/> throws
    /// <see cref="ObjectDisposedException"/>: a closed handle cannot answer, and the wait-result
    /// message a closed handle would otherwise produce hides the real cause.
    /// </remarks>
    private sealed class ProcessHandle : IHostProcessHandle
    {
        private nint _processHandle;

        internal ProcessHandle(nint processHandle, int processId)
        {
            _processHandle = processHandle;
            ProcessId = processId;
        }

        /// <inheritdoc />
        public int ProcessId { get; }

        /// <inheritdoc />
        public bool HasExited => IsSignaled();

        /// <inheritdoc />
        public int? ExitCode
        {
            get
            {
                if (!IsSignaled())
                {
                    return null;
                }

                if (!NativeMethods.GetExitCodeProcess(_processHandle, out int exitCode))
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"GetExitCodeProcess for the host process failed with Win32 error {errorCode}, so the exit code is unknown.",
                        new Win32Exception(errorCode));
                }

                return exitCode;
            }
        }

        /// <summary>Closes the process handle. Idempotent.</summary>
        public void Dispose()
        {
            nint handle = _processHandle;
            _processHandle = 0;
            if (handle == 0)
            {
                return;
            }

            if (!NativeMethods.CloseHandle(handle))
            {
                int errorCode = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"CloseHandle for the host process failed with Win32 error {errorCode}, so the handle may still be open.",
                    new Win32Exception(errorCode));
            }
        }

        /// <summary>
        /// A zero-length wait: signaled means the process ended. A zero-length wait never consumes the
        /// signal, so every later read produces the same answer. A closed handle is refused before the
        /// wait, because a wait on a closed handle reports a wait-result failure that hides the real
        /// cause.
        /// </summary>
        private bool IsSignaled()
        {
            if (_processHandle == 0)
            {
                throw new ObjectDisposedException(
                    nameof(ProcessHandle),
                    "The process handle is already closed, so the host's exit state can no longer be observed.");
            }

            uint waitResult = NativeMethods.WaitForSingleObject(_processHandle, 0);
            if (waitResult == NativeMethods.WaitObjectZero)
            {
                return true;
            }

            if (waitResult == NativeMethods.WaitTimeout)
            {
                return false;
            }

            throw new InvalidOperationException(
                $"WaitForSingleObject for the host process reported wait result {waitResult} instead of a timeout or a signaled object.");
        }
    }
}
