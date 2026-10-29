using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Integrity;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Decides whether a session must be started and, when it must, launches the host and waits for it to take
/// the single-instance lock.
/// </summary>
/// <remarks>
/// <para>
/// The decision order is fixed. A reachable owner pipe is idempotent success. A held lock whose ownership
/// record names a live owner that is still starting is the retryable <c>E_NOT_READY</c>. Any other held
/// lock is <c>E_LOCK_HELD</c> and is never waited on, retried, taken over or unbound. Only a free lock
/// reaches the launch guards, the executable resolution, process creation and the bounded wait for the
/// lock. The ownership record is read only in the silent-lock case, so a healthy session costs no record
/// read.
/// </para>
/// <para>
/// <b>Same process.</b> One semaphore is held for the whole of every start, so two callers in this process
/// queue instead of opening two windows. The queued caller then runs the decision again from its own fresh
/// probes: an answering pipe is <see cref="HostStartOutcome.AlreadyRunning"/>, a lock whose record still
/// names a live starting owner is the retryable <c>E_NOT_READY</c>, and any other held lock is
/// <c>E_LOCK_HELD</c>.
/// </para>
/// <para>
/// <b>Across processes.</b> The launcher deliberately has no cross-process lock. Two callers can both pass
/// the probe before either host takes the lock and each launch a host; the host's own single-instance lock
/// is the only arbiter. The host that loses exits with <c>E_LOCK_HELD</c>, and both callers still observe
/// the winning lock or pipe and report <see cref="HostStartOutcome.Started"/>. This is intentional: a
/// caller-side file lock would add a second, weaker arbiter, and a caller that reported <c>E_LOCK_HELD</c>
/// would invite the operator to kill a process that is merely starting. Do not turn this into a
/// cross-process file lock.
/// </para>
/// <para>
/// The wait is bounded: the launcher polls through <see cref="IWaitClock"/> every 200 ms for at most 30
/// seconds, and every poll re-reads the lock. A host that never takes the lock therefore fails explicitly
/// instead of being waited for without end. The launcher writes no log entries and never terminates the
/// host it started; every path closes the process handle, which only releases the observation.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore is never disposed on purpose: it lives exactly as long as the process does, and disposing a semaphore a queued start still awaits would turn a queued start into an ObjectDisposedException.")]
public sealed class HostProcessLauncher : IHostStarter
{
    private const string NewSessionArguments = "--new-session";
    private const int PollIntervalMilliseconds = 200;
    private const int StartupTimeoutSeconds = 30;
    private const int StartupPollCount = StartupTimeoutSeconds * 1000 / PollIntervalMilliseconds;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
    private static readonly CompositeFormat HostExitedBeforeLockFormat =
        CompositeFormat.Parse(HostLaunchMessages.HostExitedBeforeLock);
    private static readonly CompositeFormat TimedOutFormat =
        CompositeFormat.Parse(HostLaunchMessages.TimedOut);

    private readonly IOwnerProbe _probe;
    private readonly IConsoleSessionGuard _consoleGuard;
    private readonly IElevationGuard _elevationGuard;
    private readonly IHostProcessStarter _starter;
    private readonly IWaitClock _clock;
    private readonly string _applicationDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the launcher from the probes, guards, starter and wait clock it must use.</summary>
    /// <param name="probe">Reads the lock, the owner pipe and the starting-owner record.</param>
    /// <param name="consoleGuard">Reports whether this process runs on the interactive console session.</param>
    /// <param name="elevationGuard">Reports whether this process already holds an elevated token.</param>
    /// <param name="starter">Creates the host process and returns an observation handle.</param>
    /// <param name="clock">Waits the poll interval; production uses <see cref="BlockingWaitClock"/>.</param>
    /// <param name="applicationDirectory">The directory this program runs from, which holds the host executable.</param>
    /// <exception cref="ArgumentNullException">A port is null.</exception>
    /// <exception cref="ArgumentException">The application directory is empty or whitespace.</exception>
    public HostProcessLauncher(
        IOwnerProbe probe,
        IConsoleSessionGuard consoleGuard,
        IElevationGuard elevationGuard,
        IHostProcessStarter starter,
        IWaitClock clock,
        string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(consoleGuard);
        ArgumentNullException.ThrowIfNull(elevationGuard);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        _probe = probe;
        _consoleGuard = consoleGuard;
        _elevationGuard = elevationGuard;
        _starter = starter;
        _clock = clock;
        _applicationDirectory = applicationDirectory;
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<HostStartOutcome> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostStartOutcome> StartCoreAsync(CancellationToken cancellationToken)
    {
        bool lockExists = _probe.LockExists();
        bool ownerPipeReachable = await _probe.IsOwnerPipeReachableAsync(cancellationToken).ConfigureAwait(false);

        if (ownerPipeReachable)
        {
            return HostStartOutcome.AlreadyRunning;
        }

        if (lockExists)
        {
            if (_probe.IsLiveOwnerStarting())
            {
                throw new SessionOperationException(
                    SessionErrorCode.NotReady,
                    OwnerSessionResolver.StartInProgressMessage,
                    SessionErrorCatalog.IsRetryable(SessionErrorCode.NotReady));
            }

            throw new SessionOperationException(
                SessionErrorCode.LockHeld,
                OwnerSessionResolver.LockHeldMessage,
                SessionErrorCatalog.IsRetryable(SessionErrorCode.LockHeld));
        }

        if (!_consoleGuard.IsCurrentProcessOnConsoleSession())
        {
            throw HostLaunchFailure(HostLaunchMessages.NotOnConsoleSession);
        }

        if (_elevationGuard.IsCallerElevated())
        {
            throw HostLaunchFailure(HostLaunchMessages.CallerElevated);
        }

        string executablePath = ResolveHostExecutable();

        IHostProcessHandle handle;
        try
        {
            handle = _starter.Start(executablePath, NewSessionArguments);
        }
        catch (InvalidOperationException)
        {
            throw HostLaunchFailure(HostLaunchMessages.ProcessCreationFailed);
        }
        catch (Win32Exception)
        {
            throw HostLaunchFailure(HostLaunchMessages.ProcessCreationFailed);
        }

        using (handle)
        {
            return await WaitForLockAsync(handle, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls until the lock appears, the host exits, or the bounded wait runs out. The lock is read before
    /// the exit state so a host that took the lock and then ended is still a successful start.
    /// </summary>
    private async Task<HostStartOutcome> WaitForLockAsync(
        IHostProcessHandle handle,
        CancellationToken cancellationToken)
    {
        for (int poll = 0; poll < StartupPollCount; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _clock.Wait(PollInterval);

            if (_probe.LockExists())
            {
                return HostStartOutcome.Started;
            }

            if (!handle.HasExited)
            {
                continue;
            }

            // Across processes both callers may have launched a host. The loser exits after the winner took
            // the lock, so a silent pipe at this point means this start failed rather than another won.
            bool ownerPipeReachable = await _probe.IsOwnerPipeReachableAsync(cancellationToken).ConfigureAwait(false);
            if (ownerPipeReachable)
            {
                return HostStartOutcome.AlreadyRunning;
            }

            throw HostLaunchFailure(string.Format(
                CultureInfo.InvariantCulture,
                HostExitedBeforeLockFormat,
                FormatExitCode(handle.ExitCode)));
        }

        throw HostLaunchFailure(string.Format(
            CultureInfo.InvariantCulture,
            TimedOutFormat,
            StartupTimeoutSeconds));
    }

    private string ResolveHostExecutable()
    {
        string executablePath;
        try
        {
            executablePath = HostExecutablePath.Resolve(_applicationDirectory);
        }
        catch (FileNotFoundException)
        {
            throw HostLaunchFailure(HostLaunchMessages.HostExecutableMissing);
        }

        // The manifest is read from the application directory this start launches from, which is the
        // program's own directory in production. A source or test build has no manifest and stays allowed.
        ExecutableIntegrity integrity = InstallManifest.Verify(
            executablePath,
            Path.Combine(_applicationDirectory, InstallManifest.FileName));
        if (integrity is not ExecutableIntegrity.Verified and not ExecutableIntegrity.NotInstalled)
        {
            throw HostLaunchFailure(HostLaunchMessages.HostExecutableIntegrityFailed);
        }

        return executablePath;
    }

    private static string FormatExitCode(int? exitCode)
    {
        return exitCode is null
            ? "unknown"
            : exitCode.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static SessionOperationException HostLaunchFailure(string message)
    {
        return new SessionOperationException(
            SessionErrorCode.HostLaunchFailed,
            message,
            SessionErrorCatalog.IsRetryable(SessionErrorCode.HostLaunchFailed));
    }
}
