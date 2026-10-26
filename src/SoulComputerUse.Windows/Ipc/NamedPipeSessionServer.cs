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

namespace SoulComputerUse.Windows.Ipc;

/// <summary>
/// The owner side of the CLI/MCP to owner pipe. It accepts connections, reads one JSON frame per line,
/// runs each request through the injected <see cref="ISessionToolHandler"/> and writes one JSON
/// response per line.
/// </summary>
/// <remarks>
/// The pipe name is <see cref="IpcPipeNames.Owner"/> in production. The contract's
/// <c>SoulComputerUse.agent.{sessionId}</c> pipe belongs to the host to child-session agent hop and is a
/// different transport; this type never uses it.
/// <para>
/// Security status: the pipe is created with the process default security descriptor and this
/// milestone does <b>not</b> apply an explicit DACL and does <b>not</b> verify the peer's PID or SID.
/// Section 4 of the contract's DACL plus peer-SID check is a separate security gate that is not
/// implemented here; it must not be claimed as done.
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
    private readonly List<Task> _connections = [];
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
    /// Starts accepting connections and returns the accept loop. The loop runs until the caller's token
    /// is cancelled or the server is disposed, then it stops accepting and waits for live connections.
    /// </summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_started)
            {
                throw new InvalidOperationException("The session pipe server was already started.");
            }

            _started = true;
            _acceptLoop = AcceptLoopAsync(cancellationToken);
            return _acceptLoop;
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
            await acceptLoop;
        }

        Task[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
            _connections.Clear();
        }

        foreach (Task connection in connections)
        {
            await connection;
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = linked.Token;

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream stream = CreatePipe();
            try
            {
                await stream.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException)
            {
                await stream.DisposeAsync();
                break;
            }
            catch (ObjectDisposedException)
            {
                await stream.DisposeAsync();
                break;
            }

            Task connection = RunConnectionAsync(stream, token);
            lock (_gate)
            {
                _connections.Add(connection);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private async Task RunConnectionAsync(NamedPipeServerStream stream, CancellationToken token)
    {
        await using (stream)
        {
            try
            {
                await HandleConnectionAsync(stream, token);
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

    private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken token)
    {
        using StreamReader reader = new(stream, Utf8WithoutBom, detectEncodingFromByteOrderMarks: false, StreamBufferSize, leaveOpen: true);
        using StreamWriter writer = new(stream, Utf8WithoutBom, StreamBufferSize, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n",
        };

        using SemaphoreSlim writeGate = new(1, 1);
        List<Task> requests = [];

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
                    requests.Add(HandleRequestAsync(parsed.Request!, writer, writeGate, token));
                    break;
                case IpcFrameKind.Invalid:
                    await TryWriteAsync(
                        writer,
                        writeGate,
                        IpcResponse.CreateFailure(parsed.RecoveredId ?? 0, IpcError.Protocol(parsed.ErrorMessage!)),
                        token);
                    return;
                default:
                    await TryWriteAsync(
                        writer,
                        writeGate,
                        IpcResponse.CreateFailure(
                            parsed.RecoveredId ?? 0,
                            IpcError.Protocol("The owner pipe received a frame that is not a request.")),
                        token);
                    return;
            }
        }

        await Task.WhenAll(requests);
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
            response = await RunHandlerAsync(tool, request, token);
        }

        await TryWriteAsync(writer, writeGate, response, token);
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
            await WriteAsync(writer, writeGate, response, token);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client closed its side while the answer was being written; this connection is done.
        }
    }

    private static async Task WriteAsync(
        StreamWriter writer,
        SemaphoreSlim writeGate,
        IpcResponse response,
        CancellationToken token)
    {
        string line = IpcFrameCodec.WriteResponse(response);
        await writeGate.WaitAsync(token);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), token);
            await writer.FlushAsync(token);
        }
        finally
        {
            writeGate.Release();
        }
    }
}