using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc.Security;

namespace SoulComputerUse.Windows.Ipc;

/// <summary>
/// The owner side of the CLI/MCP to owner pipe. It accepts connections, verifies that each peer runs
/// under the current Windows account, reads one JSON frame per line, runs each request through the
/// injected <see cref="ISessionToolHandler"/> and writes one JSON response per line.
/// </summary>
/// <remarks>
/// The pipe name is <see cref="IpcPipeNames.Owner"/> in production. The contract's
/// <c>SoulComputerUse.agent.{sessionId}</c> pipe belongs to the host to child-session agent hop and is a
/// different transport; this type never uses it.
/// <para>
/// Each instance is created with the explicit DACL from <see cref="OwnerPipeSecurity"/>: only the
/// current user and LocalSystem hold <c>FullControl</c>, and no rule is inherited. After a connection
/// is accepted the server reads the client process id and compares its token user SID with the current
/// user SID; a peer that differs, or whose identity cannot be read, receives one <c>E_IPC_PEER</c>
/// refusal and its connection is closed without serving any request. A line longer than
/// <see cref="OwnerPipeLimits.MaximumFrameBytes"/> receives one <c>E_IPC_PROTOCOL</c> refusal and the
/// connection is closed.
/// </para>
/// <para>
/// The account check proves only that the peer runs under the same Windows account. It does not prove
/// that the peer is this product's owner client, and another process of the same account can still
/// connect; that is the known boundary of the owner SID model and this type does not claim more.
/// </para>
/// <para>
/// One connection may carry many requests. Requests inside a connection are handled concurrently and
/// correlated by their integer id, so a slow call cannot block a later one. A client that goes away
/// only ends its own connection; the accept loop keeps serving new ones.
/// </para>
/// </remarks>
public sealed class NamedPipeSessionServer : IAsyncDisposable
{
    private const int MaxInstances = NamedPipeServerStream.MaxAllowedServerInstances;
    private const int StreamBufferSize = 4096;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly ISessionToolHandler _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _connections = [];
    private NamedPipeServerStream? _listeningPipe;
    private Task? _acceptLoop;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the server for one pipe name and one handler.</summary>
    public NamedPipeSessionServer(string pipeName, ISessionToolHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(handler);
        _pipeName = pipeName;
        _handler = handler;
    }

    /// <summary>The pipe this server listens on.</summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// The number of connections that are still running. Test-only: it exists so an integration test can
    /// prove that a finished connection is removed from the tracking set instead of accumulating for the
    /// life of the server.
    /// </summary>
    internal int LiveConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>
    /// Starts accepting connections and returns the accept loop. The loop runs until the caller's token
    /// is cancelled or the server is disposed, then it stops accepting and waits for live connections.
    /// </summary>
    /// <remarks>
    /// The first pipe instance is created before this method returns, so a caller may connect as soon
    /// as it returns and a caller may read the DACL of the waiting instance. A failure to create that
    /// instance is thrown here rather than reported through the returned task.
    /// </remarks>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_started)
            {
                throw new InvalidOperationException("The session pipe server was already started.");
            }

            NamedPipeServerStream first = CreatePipe();
            _started = true;
            _acceptLoop = AcceptLoopAsync(first, cancellationToken);
            return _acceptLoop;
        }
    }

    /// <summary>
    /// Reads the DACL of the pipe instance that is waiting for a connection. The returned object is
    /// read back from the live pipe handle, not the descriptor the instance was created from.
    /// </summary>
    /// <exception cref="InvalidOperationException">No pipe instance is currently waiting for a connection.</exception>
    public PipeSecurity ReadAccessControl()
    {
        lock (_gate)
        {
            NamedPipeServerStream? pipe = _listeningPipe;
            if (pipe is null)
            {
                throw new InvalidOperationException(
                    "The owner pipe server has no instance waiting for a connection, so its DACL cannot be read.");
            }

            return pipe.GetAccessControl();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? acceptLoop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            acceptLoop = _acceptLoop;
        }

        _shutdown.Cancel();

        if (acceptLoop is not null)
        {
            await acceptLoop.ConfigureAwait(false);
        }

        Task[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
            _connections.Clear();
        }

        List<Exception> failures = [];
        foreach (Task connection in connections)
        {
            try
            {
                await connection.ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Every connection is awaited even when an earlier one failed, so a single fault cannot
                // abandon the rest and leave the server torn down underneath them.
                failures.Add(error);
            }
        }

        _shutdown.Dispose();

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more owner pipe connections failed while the server was shutting down.",
                failures);
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream first, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = linked.Token;

        NamedPipeServerStream? pending = first;
        try
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream stream = pending ?? CreatePipe();
                pending = null;

                bool connected = false;
                try
                {
                    await stream.WaitForConnectionAsync(token).ConfigureAwait(false);
                    connected = true;
                }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                finally
                {
                    if (!connected)
                    {
                        ClearListeningPipe(stream);
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                }

                ClearListeningPipe(stream);
                Task connection = RunConnectionAsync(stream, token);
                lock (_gate)
                {
                    _connections.Add(connection);
                }

                _ = ForgetConnectionWhenCompleteAsync(connection);
            }
        }
        finally
        {
            if (pending is not null)
            {
                ClearListeningPipe(pending);
                await pending.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Removes one connection from the live set when it ends. The connection keeps being the awaited
    /// task, so <see cref="DisposeAsync"/> still waits for anything that is still running; this only
    /// stops a finished connection from being kept until the server shuts down.
    /// </summary>
    private async Task ForgetConnectionWhenCompleteAsync(Task connection)
    {
        try
        {
            await connection.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The transport failures a connection can end with are already handled inside
            // RunConnectionAsync. A fault that reaches here is an owner defect; it is reported so it
            // cannot become an unobserved background fault. The detail stays out because it may name a
            // path or an account. Dispose still reports a connection that is live when the server shuts
            // down.
            Console.Error.WriteLine(
                "E_MANUAL_INTERVENTION An owner pipe connection failed unexpectedly ("
                + error.GetType().Name
                + ").");
        }
        finally
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        // The buffer sizes stay at the platform defaults, as they were before the explicit DACL.
        NamedPipeServerStream stream = NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            OwnerPipeSecurity.Create(),
            HandleInheritability.None,
            (PipeAccessRights)0);

        lock (_gate)
        {
            _listeningPipe = stream;
        }

        return stream;
    }

    private void ClearListeningPipe(NamedPipeServerStream stream)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_listeningPipe, stream))
            {
                _listeningPipe = null;
            }
        }
    }

    private async Task RunConnectionAsync(NamedPipeServerStream stream, CancellationToken token)
    {
        await using (stream)
        {
            try
            {
                if (!await TryVerifyPeerAsync(stream, token).ConfigureAwait(false))
                {
                    return;
                }

                await HandleConnectionAsync(stream, token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client vanished; only this connection ends and the accept loop continues.
            }
            catch (ObjectDisposedException)
            {
                // The stream was closed while the server was shutting down.
            }
            catch (OperationCanceledException)
            {
                // The server is shutting down.
            }
        }
    }

    /// <summary>
    /// Verifies that the connected client runs under the current account. A failure is answered with
    /// one uncorrelated <c>E_IPC_PEER</c> frame and the connection is closed without reading a request.
    /// </summary>
    private static async Task<bool> TryVerifyPeerAsync(NamedPipeServerStream stream, CancellationToken token)
    {
        try
        {
            OwnerPipePeer.EnsureClientIsSameUser(stream);
            return true;
        }
        catch (SessionOperationException error)
        {
            await TryWriteAsync(stream, OwnerPipeRefusals.Peer(error.Message), token).ConfigureAwait(false);
            return false;
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken token)
    {
        OwnerFrameReader reader = new(stream);

        using SemaphoreSlim writeGate = new(1, 1);
        List<Task> requests = [];

        try
        {
            // Declared inside the try: disposing the writer flushes even an empty buffer, and that flush
            // fails with a broken-pipe IOException once the peer is gone. Only this connection may end,
            // never the accept loop, so the dispose must run where the catch below can observe it.
            using StreamWriter writer = new(stream, Utf8WithoutBom, StreamBufferSize, leaveOpen: true)
            {
                AutoFlush = false,
                NewLine = "\n",
            };

            while (!token.IsCancellationRequested)
            {
                OwnerFrameReadResult read = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (read.Status == OwnerFrameReadStatus.EndOfStream)
                {
                    break;
                }

                if (read.Status == OwnerFrameReadStatus.FrameTooLong)
                {
                    // The line was refused while it was being read, so no request id can be recovered. The
                    // refusal is written first, then every request that already started is allowed to finish
                    // so the write gate and the stream are not disposed underneath a running handler.
                    await TryWriteAsync(writer, writeGate, OwnerPipeRefusals.FrameTooLong(), token).ConfigureAwait(false);
                    await Task.WhenAll(requests).ConfigureAwait(false);
                    return;
                }

                IpcFrameParseResult parsed = IpcFrameCodec.Parse(read.Line);
                switch (parsed.Kind)
                {
                    case IpcFrameKind.Request:
                        requests.Add(HandleRequestAsync(parsed.Request!, writer, writeGate, token));
                        break;
                    case IpcFrameKind.Invalid:
                        await TryWriteAsync(
                            writer,
                            writeGate,
                            IpcResponse.CreateFailure(parsed.RecoveredId ?? 0, IpcError.Protocol(parsed.ErrorMessage!)),
                            token).ConfigureAwait(false);
                        await Task.WhenAll(requests).ConfigureAwait(false);
                        return;
                    default:
                        await TryWriteAsync(
                            writer,
                            writeGate,
                            IpcResponse.CreateFailure(
                                parsed.RecoveredId ?? 0,
                                IpcError.Protocol("The owner pipe received a frame that is not a request.")),
                            token).ConfigureAwait(false);
                        await Task.WhenAll(requests).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down. The requests that already started are awaited below, before the
            // stream and the write gate are disposed; leaving the loop early must not abandon them.
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The peer closed its side; only this connection ended, and the accept loop keeps serving the
            // next client. The requests that already started are still awaited below.
        }

        await Task.WhenAll(requests).ConfigureAwait(false);
    }

    private async Task HandleRequestAsync(
        IpcRequest request,
        StreamWriter writer,
        SemaphoreSlim writeGate,
        CancellationToken token)
    {
        IpcResponse response;
        if (!SessionIpcRules.TryResolveMethod(request.Method, out SessionTool tool))
        {
            response = IpcResponse.CreateFailure(
                request.Id,
                IpcError.Protocol("The IPC request names a method the protocol does not define."));
        }
        else
        {
            response = await RunHandlerAsync(tool, request, token).ConfigureAwait(false);
        }

        await TryWriteAsync(writer, writeGate, response, token).ConfigureAwait(false);
    }

    private async Task<IpcResponse> RunHandlerAsync(SessionTool tool, IpcRequest request, CancellationToken token)
    {
        try
        {
            SessionToolResult result = await _handler.HandleAsync(tool, request.Parameters, token).ConfigureAwait(false);
            if (result.Succeeded)
            {
                JsonElement payload = result.Result ?? SessionToolPayloads.EmptyArguments();
                return IpcResponse.CreateSuccess(request.Id, payload);
            }

            SessionToolResult failure = result;
            return IpcResponse.CreateFailure(request.Id, IpcError.FromSessionError(failure.Error!));
        }
        catch (SessionOperationException error)
        {
            return IpcResponse.CreateFailure(
                request.Id,
                new IpcError(SessionErrorCodeNames.ToWireName(error.Code), error.Message, error.Retryable, null));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A handler that throws outside the contract failure type is an owner defect. The client
            // must still get an explicit answer rather than a silent hang, so the call is reported as
            // a failed action; the detail is deliberately not echoed onto the wire.
            return IpcResponse.CreateFailure(
                request.Id,
                new IpcError(
                    SessionErrorCodeNames.ToWireName(SessionErrorCode.AgentError),
                    "The session owner failed to execute the tool call.",
                    SessionErrorCatalog.IsRetryable(SessionErrorCode.AgentError),
                    null));
        }
    }

    private static async Task TryWriteAsync(
        StreamWriter writer,
        SemaphoreSlim writeGate,
        IpcResponse response,
        CancellationToken token)
    {
        try
        {
            await WriteAsync(writer, writeGate, response, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client closed its side while the answer was being written; this connection is done.
        }
    }

    /// <summary>
    /// Writes one refusal to a peer that has not sent a request frame, so a writer per connection is
    /// created here instead of being shared with the request loop.
    /// </summary>
    private static async Task TryWriteAsync(NamedPipeServerStream stream, IpcResponse response, CancellationToken token)
    {
        try
        {
            using StreamWriter writer = new(stream, Utf8WithoutBom, StreamBufferSize, leaveOpen: true)
            {
                AutoFlush = false,
                NewLine = "\n",
            };

            await writer.WriteLineAsync(IpcFrameCodec.WriteResponse(response).AsMemory(), token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The refused peer already closed its side; the connection closes either way.
        }
    }

    private static async Task WriteAsync(
        StreamWriter writer,
        SemaphoreSlim writeGate,
        IpcResponse response,
        CancellationToken token)
    {
        string line = IpcFrameCodec.WriteResponse(response);
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
