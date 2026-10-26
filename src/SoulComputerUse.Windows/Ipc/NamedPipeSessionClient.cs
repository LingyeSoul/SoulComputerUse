using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc.Security;

namespace SoulComputerUse.Windows.Ipc;

/// <summary>
/// The CLI and MCP side of the owner pipe: one <see cref="ISessionClient"/> that speaks the section 4
/// frames over a named pipe to a running session owner.
/// </summary>
/// <remarks>
/// The pipe name is <see cref="IpcPipeNames.Owner"/>. The contract's
/// <c>SoulComputerUse.agent.{sessionId}</c> pipe belongs to the host to child-session agent hop and is a
/// different transport; this type never uses it.
/// <para>
/// Requests are correlated by their integer id, so several calls may be in flight on one connection
/// without their answers crossing. Event frames, such as the agent's heartbeat, are read and ignored.
/// </para>
/// <para>
/// After connecting, the client reads the server process id from its own handle and compares that
/// process token's user SID with the current user SID; a mismatch, or an identity that cannot be read,
/// closes the connection and fails the call with <c>E_IPC_PEER</c>. A response line longer than
/// <see cref="OwnerPipeLimits.MaximumFrameBytes"/> fails with <c>E_IPC_PROTOCOL</c> and breaks the
/// connection instead of being buffered in full.
/// </para>
/// <para>
/// The account check proves only that the server runs under the same Windows account. It does not prove
/// that the server is this product's owner process: another process of the same account can serve the
/// pipe, which is the known boundary of the owner SID model.
/// </para>
/// </remarks>
public sealed class NamedPipeSessionClient : ISessionClient, IAsyncDisposable
{
    private const int StreamBufferSize = 4096;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly TimeSpan _actionTimeout;
    private readonly TimeSpan _screenshotTimeout;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<IpcResponse>> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private NamedPipeClientStream? _stream;
    private StreamWriter? _writer;
    private Task? _readerLoop;
    private SessionOperationException? _brokenReason;
    private int _nextId;
    private bool _disposed;

    /// <summary>Creates the client for the production owner pipe with the contract timeouts.</summary>
    public NamedPipeSessionClient()
        : this(IpcPipeNames.Owner)
    {
    }

    /// <summary>Creates the client for one pipe name with the contract timeouts.</summary>
    public NamedPipeSessionClient(string pipeName)
        : this(pipeName, SessionIpcRules.ActionTimeout, SessionIpcRules.ScreenshotTimeout)
    {
    }

    /// <summary>Creates the client for one pipe name with explicit request timeouts.</summary>
    public NamedPipeSessionClient(string pipeName, TimeSpan actionTimeout, TimeSpan screenshotTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (actionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(actionTimeout), actionTimeout, "The action timeout must be positive.");
        }

        if (screenshotTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(screenshotTimeout), screenshotTimeout, "The screenshot timeout must be positive.");
        }

        _pipeName = pipeName;
        _actionTimeout = actionTimeout;
        _screenshotTimeout = screenshotTimeout;
    }

    /// <inheritdoc />
    public async Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync(SessionTool.GetStatus, SessionToolPayloads.EmptyArguments(), cancellationToken);
        return SessionToolPayloads.ReadStatus(result);
    }

    /// <inheritdoc />
    public async Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync(SessionTool.Screenshot, SessionToolPayloads.EmptyArguments(), cancellationToken);
        return SessionToolPayloads.ReadScreenshot(result);
    }

    /// <inheritdoc />
    public async Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
    {
        JsonElement arguments = JsonSerializer.SerializeToElement(new MouseMoveArguments(x, y), IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.MouseMove, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public async Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
    {
        SessionToolArguments.RequireClickCount(count);
        MouseClickArguments payload = new(x, y, button, count);
        JsonElement arguments = JsonSerializer.SerializeToElement(payload, IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.MouseClick, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public async Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        JsonElement arguments = JsonSerializer.SerializeToElement(new MouseScrollArguments(x, y, deltaX, deltaY), IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.MouseScroll, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public async Task KeyTypeAsync(string text, CancellationToken cancellationToken)
    {
        SessionToolArguments.RequireText(text);
        JsonElement arguments = JsonSerializer.SerializeToElement(new KeyTypeArguments(text), IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.KeyType, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public async Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        SessionToolArguments.RequireKeys(keys);
        KeyPressArguments payload = new([.. keys]);
        JsonElement arguments = JsonSerializer.SerializeToElement(payload, IpcJson.ArgumentOptions);
        await CallActionAsync(SessionTool.KeyPress, arguments, cancellationToken);
    }

    /// <summary>Closes the connection and fails every call that is still waiting.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? readerLoop;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            readerLoop = _readerLoop;
        }

        _lifetime.Cancel();
        NamedPipeClientStream? stream = _stream;
        if (stream is not null)
        {
            await stream.DisposeAsync();
        }

        if (readerLoop is not null)
        {
            await readerLoop;
        }

        FailPending(SessionIpcRules.Disconnected(_pipeName));
        _connectGate.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task CallActionAsync(SessionTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync(tool, arguments, cancellationToken);
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new IpcProtocolException("The owner reported a non-object result for an action call.");
        }
    }

    private async Task<JsonElement> CallAsync(SessionTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await EnsureConnectedAsync(cancellationToken);

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
            string line = IpcFrameCodec.WriteRequest(request);
            await WriteLineAsync(line, cancellationToken);

            TimeSpan timeout = tool == SessionTool.Screenshot ? _screenshotTimeout : _actionTimeout;
            IpcResponse response;
            try
            {
                response = await completion.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw SessionIpcRules.Timeout(tool);
            }

            if (!response.Ok)
            {
                IpcError error = response.Error
                    ?? throw new IpcProtocolException("The owner sent a failure response without an error object.");
                throw SessionIpcRules.ToOperationException(error);
            }

            return response.Result ?? throw new IpcProtocolException("The owner sent a success response without a result.");
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

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                if (_stream is not null)
                {
                    return;
                }
            }

            NamedPipeClientStream stream = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);
            }
            catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await stream.DisposeAsync();
                throw SessionIpcRules.Disconnected(_pipeName);
            }

            try
            {
                OwnerPipePeer.EnsureServerIsSameUser(stream);
            }
            catch (SessionOperationException peerError)
            {
                // The connected server is not this account's process. The connection is closed and the
                // client stays failed instead of trying again against a possibly hostile endpoint.
                await stream.DisposeAsync();
                MarkBroken(peerError);
                throw;
            }

            StreamWriter writer = new(stream, Utf8WithoutBom, StreamBufferSize, leaveOpen: true)
            {
                AutoFlush = false,
                NewLine = "\n",
            };

            lock (_stateGate)
            {
                _stream = stream;
                _writer = writer;
                _readerLoop = ReadLoopAsync(stream);
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ReadLoopAsync(Stream stream)
    {
        OwnerFrameReader reader = new(stream);
        CancellationToken token = _lifetime.Token;
        SessionOperationException reason = SessionIpcRules.Disconnected(_pipeName);
        bool breakConnection = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                OwnerFrameReadResult read = await reader.ReadLineAsync(token);
                if (read.Status == OwnerFrameReadStatus.EndOfStream)
                {
                    break;
                }

                if (read.Status == OwnerFrameReadStatus.FrameTooLong)
                {
                    throw SessionIpcRules.Protocol(OwnerPipeLimits.ExceededMessage);
                }

                IpcFrameParseResult parsed = IpcFrameCodec.Parse(read.Line);
                switch (parsed.Kind)
                {
                    case IpcFrameKind.Response:
                        Complete(parsed.Response!);
                        break;
                    case IpcFrameKind.Event:
                        // Heartbeats and other owner events are not needed by the CLI or MCP adapter.
                        break;
                    default:
                        throw new IpcProtocolException(parsed.ErrorMessage ?? "The owner sent a frame the client cannot use.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The client is being disposed.
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            reason = SessionIpcRules.Disconnected(_pipeName);
            MarkBroken(reason);
        }
        catch (IpcProtocolException error)
        {
            reason = SessionIpcRules.Protocol(error.Message);
            MarkBroken(reason);
        }
        catch (SessionOperationException error)
        {
            // The line crossed the frame limit: the connection is broken instead of being read further.
            reason = error;
            MarkBroken(error);
            breakConnection = true;
        }
        finally
        {
            if (breakConnection)
            {
                BreakConnection(stream);
            }

            FailPending(reason);
        }
    }

    private static void BreakConnection(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Closing a pipe that the owner already closed is not a failure of this client.
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

    private void MarkBroken(SessionOperationException reason)
    {
        lock (_stateGate)
        {
            _brokenReason ??= reason;
        }
    }

    private void FailPending(SessionOperationException reason)
    {
        List<TaskCompletionSource<IpcResponse>> waiting;
        lock (_stateGate)
        {
            waiting = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (TaskCompletionSource<IpcResponse> completion in waiting)
        {
            completion.TrySetException(reason);
        }
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
            throw SessionIpcRules.Disconnected(_pipeName);
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            SessionOperationException reason = SessionIpcRules.Disconnected(_pipeName);
            MarkBroken(reason);
            FailPending(reason);
            throw reason;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>The JSON options the pipe payloads are written with.</summary>
internal static class IpcJson
{
    /// <summary>Camel-case names, so <c>X</c> becomes the contract's <c>x</c> and <c>DeltaX</c> its <c>deltaX</c>.</summary>
    internal static readonly JsonSerializerOptions ArgumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}