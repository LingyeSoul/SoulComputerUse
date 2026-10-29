using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting;

/// <summary>
/// Hosts one session owner: it takes the machine-wide single-instance lock, writes the ownership
/// record, serves the owner pipe, and releases all three in the contract's order on shutdown.
/// </summary>
/// <remarks>
/// Start order is fixed: acquire the lock, write the record in <c>starting</c>, then start the pipe.
/// A record write failure releases the lock immediately; a pipe startup failure deletes the record
/// and releases the lock. The service never keeps the lock while it has no ownership record.
/// <para>
/// The pipe is a <see cref="NamedPipeSessionServer"/>, so the owner pipe applies an explicit DACL that
/// allows only the current user and LocalSystem, and both ends verify that the peer process runs under
/// the current user's SID. That check proves the peer is the same account; it does not prove the peer is
/// this product, so a hostile process under the same account can still connect.
/// </para>
/// <para>
/// The service writes no log entries. Its messages name records and pipes by type or by their
/// contract names only; no SID, account, absolute path or other machine data is included.
/// </para>
/// </remarks>
public sealed class SessionOwnerService : IAsyncDisposable
{
    private static readonly TimeSpan PipeStartupTimeout = TimeSpan.FromSeconds(5);
    private const int PipeProbeTimeoutMilliseconds = 100;
    private const int PipeProbeRetryDelayMilliseconds = 20;

    private const string LockHeldMessage =
        "Another SoulComputerUse session already holds the single-instance lock, so this process did not become the owner. "
        + "Use Task Manager's Details tab to find the running session process, or close the existing session and retry.";

    private readonly SessionOwnerOptions _options;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly object _gate = new();

    private SingleInstanceLock? _lock;
    private NamedPipeSessionServer? _server;
    private CancellationTokenSource? _serverLifetime;
    private Task? _serverLoop;
    private SessionState _state = SessionState.Starting;
    private int _subagentWtsSessionId;
    private bool _started;
    private bool _disposing;
    private bool _disposed;

    /// <summary>Creates a service from one immutable option set.</summary>
    /// <exception cref="ArgumentException">A required text value is empty or a timestamp is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The process id is not positive.</exception>
    public SessionOwnerService(SessionOwnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.LockName))
        {
            throw new ArgumentException("The options must carry a non-empty lock name.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.PipeName))
        {
            throw new ArgumentException("The options must carry a non-empty owner pipe name.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.SessionId))
        {
            throw new ArgumentException("The options must carry a non-empty session id.", nameof(options));
        }

        if (options.HostPid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.HostPid, "The options must carry a positive host process id.");
        }

        if (options.HostProcessStartTime == default)
        {
            throw new ArgumentException("The options must carry a non-default host process start time.", nameof(options));
        }

        if (options.AcquiredAt == default)
        {
            throw new ArgumentException("The options must carry a non-default acquisition time.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.Store);
        ArgumentNullException.ThrowIfNull(options.Handler);
        _options = options;
        SessionId = options.SessionId;
        PipeName = options.PipeName;
        LockName = options.LockName;
    }

    /// <summary>Creates a service from the individual inputs, without defaults.</summary>
    public SessionOwnerService(
        string lockName,
        string pipeName,
        SessionOwnerStore store,
        ISessionToolHandler handler,
        string sessionId,
        int hostPid,
        DateTimeOffset hostProcessStartTime,
        DateTimeOffset acquiredAt)
        : this(new SessionOwnerOptions(
            lockName,
            pipeName,
            store,
            handler,
            sessionId,
            hostPid,
            hostProcessStartTime,
            acquiredAt))
    {
    }

    /// <summary>The session id carried by the ownership record and by <c>get_status</c>.</summary>
    public string SessionId { get; }

    /// <summary>The owner pipe this service listens on.</summary>
    public string PipeName { get; }

    /// <summary>The machine-wide lock name this service tries to hold.</summary>
    public string LockName { get; }

    /// <summary>The current in-memory state of the hosted session.</summary>
    public SessionState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The child WTS session id recorded in the ownership record, or zero when none was set.</summary>
    public int SubagentWtsSessionId
    {
        get
        {
            lock (_gate)
            {
                return _subagentWtsSessionId;
            }
        }
    }

    /// <summary>
    /// Creates the production service: contract lock name, contract owner pipe name, a UUIDv4
    /// session id, the current process identity and the current account's ownership record store.
    /// </summary>
    public static SessionOwnerService ForCurrentProcess(ISessionToolHandler handler)
    {
        return ForCurrentProcess(handler, Guid.NewGuid().ToString("D"));
    }

    /// <summary>
    /// Creates the production service with a caller-supplied session id, used when the host attaches
    /// to an agent that was launched with that same id.
    /// </summary>
    /// <exception cref="ArgumentException">The session id is empty.</exception>
    public static SessionOwnerService ForCurrentProcess(ISessionToolHandler handler, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("The session id must not be empty.", nameof(sessionId));
        }

        using Process process = Process.GetCurrentProcess();
        DateTimeOffset processStartTime = new(process.StartTime.ToUniversalTime());
        SessionOwnerOptions options = new(
            SingleInstanceLock.MutexName,
            IpcPipeNames.Owner,
            SessionOwnerStore.ForCurrentUser(),
            handler,
            sessionId,
            Environment.ProcessId,
            processStartTime,
            DateTimeOffset.UtcNow);
        return new SessionOwnerService(options);
    }

    /// <summary>
    /// Acquires the lock, writes the starting record and starts the pipe. The method returns only
    /// after the pipe has accepted a probe connection, so a caller cannot treat a dead pipe as a
    /// running owner.
    /// </summary>
    /// <exception cref="SessionOwnerException">The lock is held by another process.</exception>
    /// <exception cref="InvalidOperationException">The record could not be written or the pipe could not start.</exception>
    /// <exception cref="ObjectDisposedException">The service was already disposed.</exception>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disposing)
            {
                throw new ObjectDisposedException(nameof(SessionOwnerService), "The session owner is being disposed, so it cannot start.");
            }

            if (_started)
            {
                throw new InvalidOperationException("The session owner service was already started.");
            }

            SingleInstanceLock acquired = SingleInstanceLock.Acquire(_options.LockName);
            if (!acquired.IsHeld)
            {
                acquired.Dispose();
                throw new SessionOwnerException(SessionErrorCode.LockHeld, LockHeldMessage);
            }

            _lock = acquired;
            try
            {
                _options.Store.Write(CreateRecord(SessionState.Starting));
            }
            catch (Exception writeError) when (writeError is not OutOfMemoryException)
            {
                List<Exception> cleanupErrors = ReleaseLockAfterRecordWriteFailure(acquired);
                if (cleanupErrors.Count > 0)
                {
                    List<Exception> combined = [writeError];
                    combined.AddRange(cleanupErrors);
                    throw new AggregateException(
                        "The ownership record could not be written, and releasing the single-instance lock also failed.",
                        combined);
                }

                throw new InvalidOperationException(
                    "The ownership record could not be written, so the single-instance lock was released and the session owner did not start.",
                    writeError);
            }

            NamedPipeSessionServer? server = null;
            CancellationTokenSource? lifetime = null;
            Task? loop = null;
            try
            {
                server = new NamedPipeSessionServer(_options.PipeName, _options.Handler);
                lifetime = new CancellationTokenSource();
                loop = server.RunAsync(lifetime.Token);
                WaitUntilPipeAccepts(_options.PipeName, loop);
            }
            catch (Exception startError) when (startError is not OutOfMemoryException)
            {
                List<Exception> cleanupErrors = CleanupStartFailure(server, lifetime, loop, acquired, startError);
                if (cleanupErrors.Count > 0)
                {
                    List<Exception> combined = [startError];
                    combined.AddRange(cleanupErrors);
                    throw new AggregateException(
                        "The session owner pipe could not start, and cleaning up the record or the lock also failed.",
                        combined);
                }

                throw new InvalidOperationException(
                    "The session owner pipe could not start, so the ownership record was deleted and the single-instance lock was released.",
                    startError);
            }

            _server = server;
            _serverLifetime = lifetime;
            _serverLoop = loop;
            _state = SessionState.Starting;
            _started = true;
        }
    }

    /// <summary>
    /// Moves the session to a legal state and rewrites the ownership record atomically. An illegal
    /// transition is refused with <c>E_STATE</c>; a record write failure leaves the in-memory state
    /// unchanged and is reported explicitly.
    /// </summary>
    /// <exception cref="SessionOwnerException">The contract's state machine refuses the transition.</exception>
    /// <exception cref="InvalidOperationException">The service is not started or the record could not be written.</exception>
    public void Transition(SessionState state)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disposing)
            {
                throw new ObjectDisposedException(nameof(SessionOwnerService), "The session owner is being disposed, so its state cannot change.");
            }

            if (!_started)
            {
                throw new InvalidOperationException("The session owner service has not started, so its state cannot change.");
            }

            if (!Enum.IsDefined(state))
            {
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown session state.");
            }

            SessionState from = _state;
            if (!SessionStateMachine.CanTransition(from, state))
            {
                throw new SessionOwnerException(
                    SessionErrorCode.State,
                    "The session state '"
                        + SessionStateNames.ToWireName(from)
                        + "' cannot move to '"
                        + SessionStateNames.ToWireName(state)
                        + "'.");
            }

            try
            {
                _options.Store.Write(CreateRecord(state));
            }
            catch (Exception writeError) when (writeError is not OutOfMemoryException)
            {
                throw new InvalidOperationException(
                    "The ownership record could not be rewritten for state '"
                        + SessionStateNames.ToWireName(state)
                        + "', so the in-memory state was left unchanged.",
                    writeError);
            }

            _state = state;
        }
    }

    /// <summary>
    /// Records the Windows session id of the child-session agent and rewrites the ownership record.
    /// The record is written before the in-memory value is trusted; a write failure restores the
    /// previous value and is reported explicitly.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The WTS session id is not positive.</exception>
    /// <exception cref="InvalidOperationException">The service is not started or the record could not be written.</exception>
    public void SetSubagentWtsSessionId(int wtsSessionId)
    {
        if (wtsSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wtsSessionId),
                wtsSessionId,
                "A child session id must be positive.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disposing)
            {
                throw new ObjectDisposedException(nameof(SessionOwnerService), "The session owner is being disposed, so its record cannot change.");
            }

            if (!_started)
            {
                throw new InvalidOperationException("The session owner service has not started, so its record cannot change.");
            }

            if (_subagentWtsSessionId == wtsSessionId)
            {
                return;
            }

            int previous = _subagentWtsSessionId;
            _subagentWtsSessionId = wtsSessionId;
            try
            {
                _options.Store.Write(CreateRecord(_state));
            }
            catch (Exception writeError) when (writeError is not OutOfMemoryException)
            {
                _subagentWtsSessionId = previous;
                throw new InvalidOperationException(
                    "The ownership record could not be rewritten with the child session id, so the previous value was restored.",
                    writeError);
            }
        }
    }

    /// <summary>
    /// Stops the pipe, deletes the ownership record and releases the lock, in that order. The method
    /// is idempotent. Every cleanup step is attempted even when an earlier one fails; the collected
    /// failures are thrown together instead of being swallowed.
    /// </summary>
    /// <remarks>
    /// A partial cleanup leaves the service non-writable rather than half-alive: once disposal has
    /// begun, <see cref="Transition"/> and <see cref="SetSubagentWtsSessionId"/> keep throwing
    /// <see cref="ObjectDisposedException"/>, so a record that failed to be deleted is never written
    /// again while the lock is gone. The pipe, its lifetime and the lock handle are forgotten only
    /// after their own release succeeded, so a second call skips what already worked and retries
    /// what is still held.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            NamedPipeSessionServer? server;
            CancellationTokenSource? lifetime;
            Task? loop;
            SingleInstanceLock? lockHandle;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposing = true;
                server = _server;
                lifetime = _serverLifetime;
                loop = _serverLoop;
                lockHandle = _lock;
            }

            List<Exception> errors = [];
            if (lifetime is not null)
            {
                try
                {
                    lifetime.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The lifetime was already disposed by a previous cleanup attempt.
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    errors.Add(error);
                }
            }

            bool loopStopped = true;
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The cancel above is the expected way for the accept loop to end.
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    loopStopped = false;
                    errors.Add(error);
                }
            }

            bool serverDisposed = true;
            if (server is not null)
            {
                try
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    serverDisposed = false;
                    errors.Add(error);
                }
            }

            bool lifetimeDisposed = true;
            if (lifetime is not null)
            {
                try
                {
                    lifetime.Dispose();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    lifetimeDisposed = false;
                    errors.Add(error);
                }
            }

            bool lockDisposed = true;
            if (lockHandle is not null)
            {
                try
                {
                    _options.Store.Delete();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    errors.Add(error);
                }

                try
                {
                    lockHandle.Dispose();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    lockDisposed = false;
                    errors.Add(error);
                }
            }

            lock (_gate)
            {
                if (serverDisposed)
                {
                    _server = null;
                }

                if (lifetimeDisposed)
                {
                    _serverLifetime = null;
                }

                if (loopStopped)
                {
                    _serverLoop = null;
                }

                if (lockDisposed)
                {
                    _lock = null;
                    _started = false;
                }

                if (errors.Count == 0)
                {
                    _disposing = false;
                    _disposed = true;
                }

                // A failed step leaves _disposing set, so the service stays non-writable even when
                // the lock is already gone; the fields left set above are what a retry still owns.
            }

            if (errors.Count > 0)
            {
                throw new AggregateException(
                    "The session owner could not release every resource, so the ownership record or the single-instance lock may remain.",
                    errors);
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private SessionOwnerRecord CreateRecord(SessionState state)
    {
        return new SessionOwnerRecord(
            _options.SessionId,
            _options.HostPid,
            _options.HostProcessStartTime,
            _subagentWtsSessionId,
            ChildSessionsOpenedByUs: false,
            state,
            _options.AcquiredAt);
    }

    private static void WaitUntilPipeAccepts(string pipeName, Task acceptLoop)
    {
        long deadline = Environment.TickCount64 + (long)PipeStartupTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            ThrowIfAcceptLoopStopped(acceptLoop);
            using NamedPipeClientStream probe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                probe.ConnectAsync(PipeProbeTimeoutMilliseconds).GetAwaiter().GetResult();
                return;
            }
            catch (TimeoutException)
            {
                // No listening instance yet; try again until the startup deadline.
            }
            catch (IOException)
            {
                // The pipe instance is not ready yet; try again until the startup deadline.
            }

            Thread.Sleep(PipeProbeRetryDelayMilliseconds);
        }

        ThrowIfAcceptLoopStopped(acceptLoop);
        throw new TimeoutException("The session owner pipe did not accept a connection within the startup window.");
    }

    private static void ThrowIfAcceptLoopStopped(Task acceptLoop)
    {
        if (acceptLoop.IsFaulted)
        {
            Exception? cause = acceptLoop.Exception?.GetBaseException();
            throw new InvalidOperationException("The session owner pipe failed during startup.", cause);
        }

        if (acceptLoop.IsCompleted)
        {
            throw new InvalidOperationException("The session owner pipe stopped during startup.");
        }
    }

    /// <summary>
    /// Releases the lock after the record could not be written. The record file is deliberately not
    /// deleted here: a record that could not be replaced may be leftover evidence, and the contract
    /// only requires the lock to be released when the write fails.
    /// </summary>
    private List<Exception> ReleaseLockAfterRecordWriteFailure(SingleInstanceLock lockHandle)
    {
        List<Exception> errors = [];
        try
        {
            lockHandle.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            errors.Add(error);
        }

        _lock = null;
        return errors;
    }

    /// <summary>
    /// Stops anything that did start, deletes the record and releases the lock after a failed pipe
    /// start. The loop's own fault is the startup failure already being reported, so it is not
    /// collected a second time; an independent cleanup failure is.
    /// </summary>
    private List<Exception> CleanupStartFailure(
        NamedPipeSessionServer? server,
        CancellationTokenSource? lifetime,
        Task? loop,
        SingleInstanceLock lockHandle,
        Exception startError)
    {
        List<Exception> errors = [];
        if (lifetime is not null)
        {
            try
            {
                lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Nothing was left to cancel.
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!IsSameFailure(error, startError))
                {
                    errors.Add(error);
                }
            }
        }

        if (loop is not null)
        {
            try
            {
                loop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // The cancel above is the expected way for the accept loop to end.
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!IsSameFailure(error, startError))
                {
                    errors.Add(error);
                }
            }
        }

        if (server is not null)
        {
            try
            {
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!IsSameFailure(error, startError))
                {
                    errors.Add(error);
                }
            }
        }

        if (lifetime is not null)
        {
            try
            {
                lifetime.Dispose();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                errors.Add(error);
            }
        }

        try
        {
            _options.Store.Delete();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            errors.Add(error);
        }

        try
        {
            lockHandle.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            errors.Add(error);
        }

        _lock = null;
        _server = null;
        _serverLifetime = null;
        _serverLoop = null;
        _started = false;
        return errors;
    }

    private static bool IsSameFailure(Exception candidate, Exception startError)
    {
        if (ReferenceEquals(candidate, startError))
        {
            return true;
        }

        return ReferenceEquals(candidate.GetBaseException(), startError.GetBaseException());
    }
}