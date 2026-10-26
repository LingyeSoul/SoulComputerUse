using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The host side of the <c>SoulComputerUse.agent.{sessionId}</c> pipe. It accepts one host connection,
/// verifies the peer account, runs the nonce handshake, sends heartbeats and answers requests through
/// the injected <see cref="ISessionToolHandler"/>.
/// </summary>
/// <remarks>
/// The pipe is created with an explicit DACL that allows only the current user and LocalSystem. The
/// server also reads the client process id and compares its token SID with the current user SID, so a
/// process that somehow reaches the pipe under another account is refused with <c>E_IPC_PEER</c>.
/// The nonce is only a session binding value; it carries no confidentiality, and the DACL plus the
/// peer SID check are the primary authentication.
/// <para>
/// The server accepts exactly one connection. It returns when that connection ends, and the caller may
/// dispose it to stop a server that is still waiting. Cancellation from the caller's token is never
/// swallowed; only the server's own shutdown cancellation is absorbed by <see cref="DisposeAsync"/>.
/// </para>
/// </remarks>
public sealed class AgentPipeServer : IAsyncDisposable
{
    private const int StreamBufferSize = 4096;
    private const int MaximumServerInstances = 1;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly string _nonce;
    private readonly ISessionToolHandler _handler;
    private readonly AgentServerTimings _timings;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeServerStream? _pipe;
    private Task? _runLoop;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates one single-connection agent pipe server.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> or <paramref name="nonce"/> is empty or whitespace.</exception>
    public AgentPipeServer(
        string pipeName,
        string nonce,
        ISessionToolHandler handler,
        AgentServerTimings timings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(timings);

        _pipeName = pipeName;
        _nonce = nonce;
        _handler = handler;
        _timings = timings;
    }

    /// <summary>The pipe this server listens on.</summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// Creates the secured pipe and starts serving one connection. The returned task completes when the
    /// connection ends, fails with the protocol or peer error that ended it, or is canceled through
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server was already started.</exception>
    /// <exception cref="ObjectDisposedException">The server was disposed.</exception>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The agent pipe server was already started.");
            }

            NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                MaximumServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                StreamBufferSize,
                StreamBufferSize,
                AgentPipeSecurity.Create(),
                HandleInheritability.None,
                (PipeAccessRights)0);

            _pipe = pipe;
            _started = true;
            _listening.TrySetResult();
            _runLoop = RunCoreAsync(pipe, cancellationToken);
            return _runLoop;
        }
    }

    /// <summary>Completes when the secured pipe is listening and the one connection may be attempted.</summary>
    /// <exception cref="InvalidOperationException">The server was not started.</exception>
    public Task WaitUntilListeningAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_started)
            {
                throw new InvalidOperationException("The agent pipe server must be started before it can be awaited.");
            }
        }

        return _listening.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Completes when one host has passed the nonce handshake and been acknowledged. The unclaimed
    /// gate waits on this task, not on the accept loop, so a claimed connection is never timed out.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server was not started.</exception>
    public Task WaitUntilClaimedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_started)
            {
                throw new InvalidOperationException("The agent pipe server must be started before its claim can be awaited.");
            }
        }

        return _claimed.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Reads the DACL that is applied to the live pipe instance.</summary>
    /// <exception cref="InvalidOperationException">The server is not listening, so there is no pipe to inspect.</exception>
    public PipeSecurity ReadAccessControl()
    {
        lock (_gate)
        {
            if (_pipe is null)
            {
                throw new InvalidOperationException(
                    "The agent pipe server is not listening, so its DACL cannot be read.");
            }

            return _pipe.GetAccessControl();
        }
    }

    /// <summary>Stops the server and waits for its connection to end.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? runLoop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runLoop = _runLoop;
        }

        _shutdown.Cancel();

        try
        {
            if (runLoop is not null)
            {
                try
                {
                    await runLoop;
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    // Disposing the server is this type's own cancellation, not the caller's.
                }
                catch (SessionOperationException)
                {
                    // RunAsync already carries this failure to the caller that observes it; disposal
                    // still has to release the pipe and the synchronization primitives.
                }
                catch (IOException)
                {
                    // The connection ended while the server was shutting down.
                }
                catch (ObjectDisposedException)
                {
                    // The connection ended while the server was shutting down.
                }
            }
        }
        finally
        {
            _writeGate.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task RunCoreAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);

        try
        {
            await pipe.WaitForConnectionAsync(linked.Token);
            await ServeConnectionAsync(pipe, linked.Token, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _pipe = null;
            }

            await pipe.DisposeAsync();
        }
    }

    private async Task ServeConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken serverToken,
        CancellationToken callerToken)
    {
        using StreamReader reader = new(pipe, Utf8WithoutBom, detectEncodingFromByteOrderMarks: false, StreamBufferSize, leaveOpen: true);
        using StreamWriter writer = new(pipe, Utf8WithoutBom, StreamBufferSize, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n",
        };

        try
        {
            int clientProcessId = AgentPeerIdentity.GetClientProcessId(pipe);
            AgentPeerIdentity.EnsureSameUser(
                AgentPeerIdentity.CurrentUserSid(),
                AgentPeerIdentity.GetProcessUserSid(clientProcessId));

            await WriteLineAsync(writer, AgentHandshake.WriteHello(_nonce), serverToken);

            if (!await CompleteHandshakeAsync(reader, writer, serverToken))
            {
                return;
            }

            using CancellationTokenSource connection = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            CancellationToken token = connection.Token;
            Task heartbeat = RunHeartbeatAsync(writer, connection, token);
            List<Task> requests = [];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(token);
                    if (line is null)
                    {
                        break;
                    }

                    IpcFrameParseResult parsed = IpcFrameCodec.Parse(line);
                    switch (parsed.Kind)
                    {
                        case IpcFrameKind.Request:
                            requests.Add(HandleRequestAsync(parsed.Request!, writer, token));
                            break;
                        case IpcFrameKind.Invalid:
                            await TryWriteResponseAsync(
                                writer,
                                IpcResponse.CreateFailure(
                                    parsed.RecoveredId ?? 0,
                                    IpcError.Protocol(parsed.ErrorMessage!)),
                                token);
                            return;
                        default:
                            await TryWriteResponseAsync(
                                writer,
                                IpcResponse.CreateFailure(
                                    parsed.RecoveredId ?? 0,
                                    IpcError.Protocol("The agent pipe received a frame that is not a request.")),
                                token);
                            return;
                    }
                }
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested && !callerToken.IsCancellationRequested)
            {
                // The connection ended, for example after a heartbeat write found the pipe gone.
            }
            finally
            {
                connection.Cancel();
                try
                {
                    await heartbeat;
                }
                catch (OperationCanceledException)
                {
                    // The heartbeat loop observes the same connection cancellation.
                }

                try
                {
                    await Task.WhenAll(requests);
                }
                catch (OperationCanceledException)
                {
                    // Request handlers observe the connection cancellation.
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The peer closed its side; only this connection ended.
        }
    }

    private async Task<bool> CompleteHandshakeAsync(
        StreamReader reader,
        StreamWriter writer,
        CancellationToken token)
    {
        string? line;
        try
        {
            line = await reader.ReadLineAsync(token).AsTask().WaitAsync(_timings.HandshakeTimeout, token);
        }
        catch (TimeoutException)
        {
            throw AgentErrors.Timeout(
                "The host did not complete the child-session agent handshake within the contract timeout, so the connection was closed.");
        }

        if (line is null)
        {
            return false;
        }

        IpcFrameParseResult parsed = IpcFrameCodec.Parse(line);
        if (parsed.Kind == IpcFrameKind.Invalid)
        {
            throw AgentErrors.Protocol(parsed.ErrorMessage!);
        }

        if (parsed.Kind != IpcFrameKind.Request)
        {
            throw AgentErrors.Protocol(
                "The child-session agent handshake expected a hello_ack request, but another frame type was received.");
        }

        IpcRequest request = parsed.Request!;
        if (!AgentHandshake.IsHelloAck(request))
        {
            throw AgentErrors.Protocol(
                "The child-session agent handshake expected a hello_ack request with an empty parameter object.");
        }

        IpcResponse response = IpcResponse.CreateSuccess(request.Id, SessionToolPayloads.OkResult());
        await WriteLineAsync(writer, IpcFrameCodec.WriteResponse(response), token);
        _claimed.TrySetResult();
        return true;
    }

    private async Task RunHeartbeatAsync(
        StreamWriter writer,
        CancellationTokenSource connection,
        CancellationToken token)
    {
        long tick = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_timings.HeartbeatInterval, token);
                tick++;
                await WriteLineAsync(writer, AgentHandshake.WriteHeartbeat(tick), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The connection is ending.
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            connection.Cancel();
        }
    }

    private async Task HandleRequestAsync(IpcRequest request, StreamWriter writer, CancellationToken token)
    {
        IpcResponse response;
        if (!SessionIpcRules.TryResolveMethod(request.Method, out SessionTool tool))
        {
            response = IpcResponse.CreateFailure(
                request.Id,
                IpcError.Protocol("The agent pipe request names a method the protocol does not define."));
        }
        else
        {
            response = await RunHandlerAsync(tool, request, token);
        }

        await TryWriteResponseAsync(writer, response, token);
    }

    private async Task<IpcResponse> RunHandlerAsync(SessionTool tool, IpcRequest request, CancellationToken token)
    {
        try
        {
            SessionToolResult result = await _handler.HandleAsync(tool, request.Parameters, token);
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AgentFailureResponse(request.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A handler that throws outside the contract failure type is an agent defect. The host
            // must still get an explicit answer, and the detail never crosses the wire.
            return AgentFailureResponse(request.Id);
        }
    }

    private static IpcResponse AgentFailureResponse(int id)
    {
        return IpcResponse.CreateFailure(
            id,
            new IpcError(
                SessionErrorCodeNames.ToWireName(SessionErrorCode.AgentError),
                "agent-failure",
                SessionErrorCatalog.IsRetryable(SessionErrorCode.AgentError),
                null));
    }

    private async Task TryWriteResponseAsync(StreamWriter writer, IpcResponse response, CancellationToken token)
    {
        try
        {
            await WriteLineAsync(writer, IpcFrameCodec.WriteResponse(response), token);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The peer closed its side while the answer was being written; the connection is done.
        }
    }

    private async Task WriteLineAsync(StreamWriter writer, string line, CancellationToken token)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), token);
            await writer.FlushAsync(token);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}