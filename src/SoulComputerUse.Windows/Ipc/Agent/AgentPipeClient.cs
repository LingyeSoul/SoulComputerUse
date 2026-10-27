using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The host side client of the <c>SoulComputerUse.agent.{sessionId}</c> pipe: it connects, verifies the
/// server account, validates the hello nonce, monitors heartbeats and correlates request responses by
/// their integer id.
/// </summary>
/// <remarks>
/// The nonce is only a session binding value; it carries no confidentiality, and the explicit pipe
/// DACL plus the peer SID check are the primary authentication. A silence longer than the configured
/// heartbeat limit probes the server process: a confirmed exit is <c>E_AGENT_DEAD</c>, otherwise the
/// call fails with <c>E_AGENT_TIMEOUT</c>. Events the client does not know are ignored but still
/// refresh the liveness timestamp. Request ids are never reused or retried.
/// </remarks>
public sealed class AgentPipeClient : ISessionClient, IAsyncDisposable
{
    private const int StreamBufferSize = 4096;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly string _expectedNonce;
    private readonly AgentClientTimings _timings;
    private readonly IAgentProcessProbe _processProbe;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<IpcResponse>> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private NamedPipeClientStream? _stream;
    private StreamWriter? _writer;
    private Task? _readerLoop;
    private Task? _heartbeatLoop;
    private SessionOperationException? _brokenReason;
    private int _nextId;
    private long _lastReceivedTimestamp;
    private int _serverProcessId;
    private DateTime _serverStartTimeUtc;
    private bool _disposed;

    /// <summary>Creates one client for one agent pipe and one expected nonce.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> or <paramref name="nonce"/> is empty or whitespace.</exception>
    public AgentPipeClient(
        string pipeName,
        string nonce,
        AgentClientTimings timings,
        IAgentProcessProbe processProbe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(processProbe);

        _pipeName = pipeName;
        _expectedNonce = nonce;
        _timings = timings;
        _processProbe = processProbe;
    }

    /// <summary>
    /// The process id of the agent on the other end of the connected pipe, or zero before the
    /// handshake. The host uses it to record the child WTS session id.
    /// </summary>
    public int ServerProcessId
    {
        get
        {
            lock (_stateGate)
            {
                return _serverProcessId;
            }
        }
    }

    /// <inheritdoc />
    public async Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync(SessionTool.GetStatus, SessionToolPayloads.EmptyArguments(), cancellationToken).ConfigureAwait(false);
        return SessionToolPayloads.ReadStatus(result);
    }

    /// <inheritdoc />
    public async Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync(SessionTool.Screenshot, SessionToolPayloads.EmptyArguments(), cancellationToken).ConfigureAwait(false);
        return SessionToolPayloads.ReadScreenshot(result);
    }

    /// <inheritdoc />
    public async Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
    {
        JsonElement arguments = JsonSerializer.SerializeToElement(new MouseMoveArguments(x, y), IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.MouseMove, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
    {
        SessionToolArguments.RequireClickCount(count);
        MouseClickArguments payload = new(x, y, button, count);
        JsonElement arguments = JsonSerializer.SerializeToElement(payload, IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.MouseClick, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        JsonElement arguments = JsonSerializer.SerializeToElement(new MouseScrollArguments(x, y, deltaX, deltaY), IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.MouseScroll, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task KeyTypeAsync(string text, CancellationToken cancellationToken)
    {
        SessionToolArguments.RequireText(text);
        JsonElement arguments = JsonSerializer.SerializeToElement(new KeyTypeArguments(text), IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.KeyType, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        SessionToolArguments.RequireKeys(keys);
        KeyPressArguments payload = new([.. keys]);
        JsonElement arguments = JsonSerializer.SerializeToElement(payload, IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.KeyPress, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the pipe and fails every call that is still waiting.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? readerLoop;
        Task? heartbeatLoop;
        NamedPipeClientStream? stream;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            readerLoop = _readerLoop;
            heartbeatLoop = _heartbeatLoop;
            stream = _stream;
        }

        _lifetime.Cancel();
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        if (readerLoop is not null)
        {
            await readerLoop.ConfigureAwait(false);
        }

        if (heartbeatLoop is not null)
        {
            await heartbeatLoop.ConfigureAwait(false);
        }

        Break(AgentErrors.Disconnected(_pipeName));
        _connectGate.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task CallActionAsync(SessionTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new IpcProtocolException("The child-session agent reported a non-object result for an action call.");
        }
    }

    private async Task<JsonElement> CallAsync(
        SessionTool tool,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        int id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<IpcResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateGate)
        {
            if (_brokenReason is not null)
            {
                throw _brokenReason;
            }

            _pending.Add(id, completion);
        }

        try
        {
            IpcRequest request = new(id, SessionToolNames.ToWireName(tool), arguments);
            await WriteLineAsync(IpcFrameCodec.WriteRequest(request), cancellationToken).ConfigureAwait(false);

            TimeSpan timeout = tool == SessionTool.Screenshot ? _timings.ScreenshotTimeout : _timings.ActionTimeout;
            IpcResponse response;
            try
            {
                response = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw AgentErrors.Timeout(
                    "The child-session agent did not answer '"
                    + SessionToolNames.ToWireName(tool)
                    + "' within its contract timeout, so the call was abandoned.");
            }

            if (!response.Ok)
            {
                IpcError error = response.Error
                    ?? throw new IpcProtocolException("The child-session agent sent a failure response without an error object.");
                throw AgentErrors.FromWireError(error);
            }

            return response.Result
                ?? throw new IpcProtocolException("The child-session agent sent a success response without a result.");
        }
        finally
        {
            lock (_stateGate)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (_brokenReason is not null)
            {
                throw _brokenReason;
            }

            if (_stream is not null)
            {
                return;
            }
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_brokenReason is not null)
                {
                    throw _brokenReason;
                }

                if (_stream is not null)
                {
                    return;
                }
            }

            NamedPipeClientStream stream = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync((int)_timings.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw Break(AgentErrors.Disconnected(_pipeName));
            }

            lock (_stateGate)
            {
                _stream = stream;
            }

            StreamReader reader = new(stream, Utf8WithoutBom, detectEncodingFromByteOrderMarks: false, StreamBufferSize, leaveOpen: true);
            StreamWriter writer = new(stream, Utf8WithoutBom, StreamBufferSize, leaveOpen: true)
            {
                AutoFlush = false,
                NewLine = "\n",
            };

            try
            {
                await HandshakeAsync(stream, reader, writer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await CloseAfterFailedHandshakeAsync(stream, reader, writer).ConfigureAwait(false);
                throw;
            }
            catch (SessionOperationException error)
            {
                await CloseAfterFailedHandshakeAsync(stream, reader, writer).ConfigureAwait(false);
                throw Break(error);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                await CloseAfterFailedHandshakeAsync(stream, reader, writer).ConfigureAwait(false);
                throw Break(AgentErrors.Disconnected(_pipeName));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await CloseAfterFailedHandshakeAsync(stream, reader, writer).ConfigureAwait(false);
                throw Break(AgentErrors.Protocol(
                    "The child-session agent handshake failed because the peer data could not be read: "
                    + error.GetType().Name
                    + "."));
            }

            lock (_stateGate)
            {
                _writer = writer;
                _lastReceivedTimestamp = Stopwatch.GetTimestamp();
                _readerLoop = ReadLoopAsync(reader);
                _heartbeatLoop = MonitorHeartbeatAsync();
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task CloseAfterFailedHandshakeAsync(
        NamedPipeClientStream stream,
        StreamReader reader,
        StreamWriter writer)
    {
        reader.Dispose();
        writer.Dispose();
        lock (_stateGate)
        {
            if (ReferenceEquals(_stream, stream))
            {
                _stream = null;
            }
        }

        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandshakeAsync(
        NamedPipeClientStream stream,
        StreamReader reader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        int serverProcessId = AgentPeerIdentity.GetServerProcessId(stream);
        AgentPeerIdentity.EnsureSameUser(
            AgentPeerIdentity.CurrentUserSid(),
            AgentPeerIdentity.GetProcessUserSid(serverProcessId));

        _serverProcessId = serverProcessId;
        if (!WindowsAgentProcessProbe.TryGetStartTimeUtc(serverProcessId, out DateTime serverStartTime))
        {
            throw AgentErrors.Peer(
                "The child-session agent process start time could not be read, so its identity and liveness cannot be verified.");
        }

        _serverStartTimeUtc = serverStartTime;

        string? helloLine = await ReadLineWithHandshakeTimeoutAsync(reader, cancellationToken).ConfigureAwait(false);
        if (helloLine is null)
        {
            throw AgentErrors.Disconnected(_pipeName);
        }

        IpcFrameParseResult hello = IpcFrameCodec.Parse(helloLine);
        if (hello.Kind == IpcFrameKind.Invalid)
        {
            throw AgentErrors.Protocol(hello.ErrorMessage!);
        }

        if (hello.Kind != IpcFrameKind.Event)
        {
            throw AgentErrors.Protocol("The child-session agent's first frame was not the hello event.");
        }

        AgentHandshake.ValidateHello(hello.Event!, _expectedNonce);

        int ackId = Interlocked.Increment(ref _nextId);
        await WriteLineCoreAsync(writer, IpcFrameCodec.WriteRequest(AgentHandshake.CreateHelloAck(ackId)), cancellationToken).ConfigureAwait(false);

        string? ackLine = await ReadLineWithHandshakeTimeoutAsync(reader, cancellationToken).ConfigureAwait(false);
        if (ackLine is null)
        {
            throw AgentErrors.Disconnected(_pipeName);
        }

        IpcFrameParseResult ack = IpcFrameCodec.Parse(ackLine);
        if (ack.Kind == IpcFrameKind.Invalid)
        {
            throw AgentErrors.Protocol(ack.ErrorMessage!);
        }

        if (ack.Kind != IpcFrameKind.Response)
        {
            throw AgentErrors.Protocol("The child-session agent did not answer hello_ack with a response frame.");
        }

        IpcResponse response = ack.Response!;
        if (response.Id != ackId)
        {
            throw AgentErrors.Protocol("The child-session agent answered hello_ack with a different request id.");
        }

        if (!response.Ok)
        {
            IpcError error = response.Error
                ?? throw AgentErrors.Protocol("The child-session agent refused hello_ack without an error object.");
            throw AgentErrors.FromWireError(error);
        }
    }

    private async Task<string?> ReadLineWithHandshakeTimeoutAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            return await reader.ReadLineAsync(token).AsTask().WaitAsync(_timings.HandshakeTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw AgentErrors.Timeout(
                "The child-session agent did not complete the hello handshake within its contract timeout.");
        }
    }

    private async Task ReadLoopAsync(StreamReader reader)
    {
        CancellationToken token = _lifetime.Token;
        SessionOperationException reason = AgentErrors.Disconnected(_pipeName);
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                Interlocked.Exchange(ref _lastReceivedTimestamp, Stopwatch.GetTimestamp());
                IpcFrameParseResult parsed = IpcFrameCodec.Parse(line);
                switch (parsed.Kind)
                {
                    case IpcFrameKind.Response:
                        Complete(parsed.Response!);
                        break;
                    case IpcFrameKind.Event:
                        // Every event, known or unknown, only proves the peer is still there.
                        break;
                    default:
                        throw new IpcProtocolException(
                            parsed.ErrorMessage ?? "The child-session agent sent a frame the client cannot use.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The client is closing.
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            reason = AgentErrors.Disconnected(_pipeName);
        }
        catch (IpcProtocolException error)
        {
            reason = AgentErrors.Protocol(error.Message);
        }
        finally
        {
            reader.Dispose();
            Break(reason);
        }
    }

    private async Task MonitorHeartbeatAsync()
    {
        CancellationToken token = _lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_timings.HeartbeatCheckInterval, token).ConfigureAwait(false);
                long lastReceived = Interlocked.Read(ref _lastReceivedTimestamp);
                TimeSpan silence = Stopwatch.GetElapsedTime(lastReceived);
                if (silence < _timings.HeartbeatSilenceTimeout)
                {
                    continue;
                }

                if (Interlocked.Read(ref _lastReceivedTimestamp) != lastReceived)
                {
                    // A frame arrived while the silence was being measured.
                    continue;
                }

                Break(ClassifySilence());
                _lifetime.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The client is closing or the silence path already canceled the lifetime.
        }
    }

    private SessionOperationException ClassifySilence()
    {
        if (_processProbe.TryIsAlive(_serverProcessId, _serverStartTimeUtc, out bool isAlive)
            && !isAlive)
        {
            return AgentErrors.Dead(
                "The child-session agent process exited, so the connection was lost.");
        }

        return AgentErrors.Timeout(
            "The child-session agent sent no event or response within the heartbeat silence timeout.");
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        StreamWriter? writer;
        lock (_stateGate)
        {
            writer = _writer;
        }

        if (writer is null)
        {
            throw Break(AgentErrors.Disconnected(_pipeName));
        }

        await WriteLineCoreAsync(writer, line, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLineCoreAsync(StreamWriter writer, string line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            throw Break(AgentErrors.Disconnected(_pipeName));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Complete(IpcResponse response)
    {
        TaskCompletionSource<IpcResponse>? completion;
        lock (_stateGate)
        {
            if (!_pending.Remove(response.Id, out completion))
            {
                // A late answer to a call that already timed out; the id is no longer expected.
                return;
            }
        }

        completion.TrySetResult(response);
    }

    private SessionOperationException Break(SessionOperationException reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        List<TaskCompletionSource<IpcResponse>> waiting;
        SessionOperationException effective;
        lock (_stateGate)
        {
            _brokenReason ??= reason;
            effective = _brokenReason;
            waiting = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (TaskCompletionSource<IpcResponse> completion in waiting)
        {
            completion.TrySetException(effective);
        }

        return effective;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
