using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.Windows.Hosting.Headless;

/// <summary>
/// Starts a session owner inside the calling process when the machine has no owner, and hands back a
/// client that reaches it over the owner pipe.
/// </summary>
/// <remarks>
/// <para>
/// Start order: probe the lock and the pipe without creating either, refuse a held lock with
/// <c>E_LOCK_HELD</c>, attach when the pipe already answers, and only then become the owner. A held lock
/// whose ownership record names a live owner that is still starting is the one exception: it is the
/// retryable <c>E_NOT_READY</c>, because that owner is in its logon window rather than abandoned.
/// Becoming the owner means <see cref="SessionOwnerService.Start"/>, which takes the lock, writes the
/// ownership record and serves the owner pipe in that order. The handler behind the pipe is built from
/// the client the starter created, and the caller receives a <see cref="NamedPipeSessionClient"/>
/// instead of that inner client, so a caller cannot tell this path from attaching to an existing owner.
/// </para>
/// <para>
/// This implementation does not establish a remote desktop session. It hosts the owner pipe and one
/// injected session client, and its behaviour is whatever that client provides. A client that cannot
/// serve calls, such as <see cref="UnavailableSessionClient"/>, stops the start with <c>E_NOT_READY</c>
/// before the lock is taken, so a headless owner never runs while claiming a backend it does not have.
/// </para>
/// <para>
/// Every failure message is a fixed template without a path, account, session id or nonce.
/// </para>
/// </remarks>
public sealed class HeadlessOwnerStarter : IOwnerSessionStarter
{
    private const string LockHeldMessage =
        "Another process holds the single-instance lock, so this process did not start a headless session. "
        + "Use Task Manager's Details tab to look for a running SoulComputerUse process, or close the existing session and retry.";

    private const string BackendUnavailableMessage =
        "This build has no session backend to place behind a headless owner, so no session was started and no lock was taken. "
        + "Start the SoulComputerUse Host first, then retry.";

    private readonly HeadlessStarterOptions _options;
    private readonly IOwnerProbe _probe;

    /// <summary>Creates the starter from one option set and the probe it must consult first.</summary>
    /// <exception cref="ArgumentNullException">The options or the probe is null.</exception>
    public HeadlessOwnerStarter(HeadlessStarterOptions options, IOwnerProbe probe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        _options = options;
        _probe = probe;
    }

    /// <summary>
    /// Probes the machine, then either attaches to the owner that answered, starts an owner in this
    /// process, or fails without taking the lock.
    /// </summary>
    /// <param name="cancellationToken">
    /// Checked before the probe and once before the start begins. A start is deliberately not
    /// cancellable part way through, because abandoning it would leave a taken lock, a written record
    /// or a bound pipe behind.
    /// </param>
    /// <returns>
    /// A client bound to the owner pipe. Disposing it also releases the session when this process
    /// started it.
    /// </returns>
    /// <exception cref="SessionOperationException">
    /// A live owner is still starting (<c>E_NOT_READY</c>), the lock is held without an answer
    /// (<c>E_LOCK_HELD</c>), or the backend cannot serve calls (<c>E_NOT_READY</c>).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The pipe could not be started, or the lock and pipe state could not be observed.
    /// </exception>
    public async Task<ISessionClient> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool lockExists = _probe.LockExists();
        bool pipeReachable = await _probe.IsOwnerPipeReachableAsync(cancellationToken).ConfigureAwait(false);
        bool liveOwnerStarting = lockExists && !pipeReachable && _probe.IsLiveOwnerStarting();
        OwnerDiscoveryDecision decision = OwnerDiscovery.Decide(lockExists, pipeReachable, liveOwnerStarting);
        switch (decision)
        {
            case OwnerDiscoveryDecision.UseExistingOwner:
                // The pipe answered while this process was deciding, so another owner won the race.
                // The pipe is authoritative: attach to it instead of starting a second session. The
                // returned client owns nothing beyond its connection.
                return new NamedPipeSessionClient(_options.PipeName);

            case OwnerDiscoveryDecision.RefuseStartInProgress:
                throw StartInProgressFailure();

            case OwnerDiscoveryDecision.RefuseLockHeld:
                throw LockHeldFailure();

            case OwnerDiscoveryDecision.StartOwnSession:
                return await StartOwnSessionAsync(cancellationToken).ConfigureAwait(false);

            default:
                throw new InvalidOperationException(
                    $"The owner discovery table produced the undefined decision value {decision}.");
        }
    }

    /// <summary>
    /// Becomes the owner in this process: build the client, refuse an unusable one, then take the lock,
    /// write the record and serve the pipe.
    /// </summary>
    /// <remarks>
    /// The owner start is synchronous on purpose. It returns only after the pipe accepts a connection,
    /// and it has already undone everything it did when it fails, so there is no half-started owner to
    /// cancel and no thread switch that could deadlock a caller's synchronization context.
    /// </remarks>
    private async Task<ISessionClient> StartOwnSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ISessionClient sessionClient = _options.ClientFactory();
        if (sessionClient is UnavailableSessionClient)
        {
            // Refused before the lock: a headless owner without a backend would answer every call as
            // if the session were merely disconnected, which is the pretence this check exists to stop.
            throw NotReadyFailure();
        }

        SessionOwnerService owner;
        try
        {
            owner = CreateOwner(_options.HandlerFactory(sessionClient));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The backend was already built, so it is closed before the failure leaves the starter.
            Exception? handlerCleanupError = await TryDisposeClientAsync(sessionClient).ConfigureAwait(false);
            throw WithCleanupFailure(error, handlerCleanupError);
        }

        try
        {
            owner.Start();
        }
        catch (SessionOwnerException error) when (error.Code == SessionErrorCode.LockHeld)
        {
            // The lock was taken between the probe and this start. Section 3 makes the loser of that
            // race fail as a client with E_LOCK_HELD instead of starting a second session, and the
            // failed start already released everything it had taken.
            Exception? lockCleanupError = await TryDisposeClientAsync(sessionClient).ConfigureAwait(false);
            throw WithCleanupFailure(LockHeldFailure(), lockCleanupError);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Exception? cleanupError = await TryDisposeClientAsync(sessionClient).ConfigureAwait(false);
            throw WithCleanupFailure(error, cleanupError);
        }

        return new HeadlessOwnerSession(new NamedPipeSessionClient(_options.PipeName), owner, sessionClient);
    }

    private SessionOwnerService CreateOwner(ISessionToolHandler handler)
    {
        using Process process = Process.GetCurrentProcess();
        DateTimeOffset processStartTime = new(process.StartTime.ToUniversalTime());
        SessionOwnerOptions options = new(
            _options.LockName,
            _options.PipeName,
            _options.Store,
            handler,
            _options.SessionId,
            Environment.ProcessId,
            processStartTime,
            DateTimeOffset.UtcNow);
        return new SessionOwnerService(options);
    }

    /// <summary>
    /// Keeps the owner's own explicit failure and adds the cleanup failure that followed it, so neither
    /// cause is hidden behind the other.
    /// </summary>
    private static Exception WithCleanupFailure(Exception error, Exception? cleanupError)
    {
        if (cleanupError is null)
        {
            return error;
        }

        List<Exception> combined = [error, cleanupError];
        return new AggregateException(
            "The headless session could not start, and closing the session client it created also failed.",
            combined);
    }

    private static async Task<Exception?> TryDisposeClientAsync(ISessionClient client)
    {
        if (client is not IAsyncDisposable disposable)
        {
            return null;
        }

        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return error;
        }
    }

    private static SessionOperationException LockHeldFailure()
    {
        return new SessionOperationException(
            SessionErrorCode.LockHeld,
            LockHeldMessage,
            SessionErrorCatalog.IsRetryable(SessionErrorCode.LockHeld));
    }

    private static SessionOperationException StartInProgressFailure()
    {
        return new SessionOperationException(
            SessionErrorCode.NotReady,
            OwnerSessionResolver.StartInProgressMessage,
            SessionErrorCatalog.IsRetryable(SessionErrorCode.NotReady));
    }

    private static SessionOperationException NotReadyFailure()
    {
        return new SessionOperationException(
            SessionErrorCode.NotReady,
            BackendUnavailableMessage,
            SessionErrorCatalog.IsRetryable(SessionErrorCode.NotReady));
    }
}
