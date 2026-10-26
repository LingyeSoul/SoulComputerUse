using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The owner pipe against real Windows named pipes: one server, one client, a real round trip for every
/// tool kind, error and protocol refusals, id correlation and event tolerance.
/// </summary>
/// <remarks>
/// Every case uses a unique pipe name of the form <c>SoulComputerUse.owner.test-{guid}</c>, so a real
/// owner session on this machine can never be touched and no case can collide with another.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class IpcTransportTests
{
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] OnePixelPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];

    private static string NewPipeName()
    {
        return "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N");
    }

    [TestMethod]
    public async Task RoundTripReturnsStatusScreenshotAndActionResults()
    {
        string pipeName = NewPipeName();
        StubSessionToolHandler handler = new();
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (NamedPipeSessionClient client = new(pipeName))
        {
            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(StubSessionToolHandler.SessionId, status.SessionId);
            Assert.AreEqual(AuthTier.SystemCredential, status.AuthTier);
            Assert.AreEqual(GateStrength.Login, status.GateStrength);
            Assert.AreEqual(1920, status.Screen.Width);
            Assert.AreEqual(1080, status.Screen.Height);

            byte[] png = await client.ScreenshotAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.IsTrue(png.AsSpan().SequenceEqual(OnePixelPng), "The PNG bytes must survive the base64 round trip unchanged.");

            await client.MouseClickAsync(10, 20, MouseButton.Left, 2, CancellationToken.None).WaitAsync(CaseTimeout);

            JsonElement clickArguments = handler.ArgumentsFor(SessionTool.MouseClick);
            Assert.AreEqual(10, clickArguments.GetProperty("x").GetInt32());
            Assert.AreEqual(20, clickArguments.GetProperty("y").GetInt32());
            Assert.AreEqual("left", clickArguments.GetProperty("button").GetString());
            Assert.AreEqual(2, clickArguments.GetProperty("count").GetInt32());
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
        Assert.IsGreaterThanOrEqualTo(3, handler.CallCount, "The owner must have seen the status, screenshot and click calls.");
    }

    [TestMethod]
    public async Task ErrorResponseBecomesSessionOperationExceptionWithTheContractCode()
    {
        string pipeName = NewPipeName();
        StubSessionToolHandler handler = new()
        {
            ForcedFailure = new IpcError("E_COORD_OUT_OF_RANGE", "The x coordinate must satisfy 0 <= x < 1920.", false, "ready"),
        };
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (NamedPipeSessionClient client = new(pipeName))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.MouseMoveAsync(5000, 10, CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.CoordOutOfRange, error.Code);
            Assert.AreEqual("The x coordinate must satisfy 0 <= x < 1920.", error.Message);
            Assert.IsFalse(error.Retryable);
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task UnknownErrorCodeIsRefusedInsteadOfBeingGuessed()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource serverLifetime = new();
        Task rawServer = RunRawOwnerAsync(
            pipeName,
            request =>
            [
                "{\"v\":1,\"type\":\"response\",\"id\":"
                    + request.Id.ToString(CultureInfo.InvariantCulture)
                    + ",\"ok\":false,\"error\":{\"code\":\"E_NOT_A_REAL_CODE\",\"message\":\"The owner sent a code outside the contract.\",\"retryable\":false,\"state\":null}}",
            ],
            serverLifetime.Token);

        await using (NamedPipeSessionClient client = new(pipeName))
        {
            await Assert.ThrowsExactlyAsync<IpcProtocolException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));
        }

        serverLifetime.Cancel();
        await rawServer.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task UnknownMethodIsRefusedWithProtocolErrorAndKeepsTheConnectionAlive()
    {
        string pipeName = NewPipeName();
        StubSessionToolHandler handler = new();
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (RawPipeClient raw = await RawPipeClient.ConnectAsync(pipeName))
        {
            await raw.WriteLineAsync("""{"v":1,"type":"request","id":9,"method":"shell","params":{}}""");

            IpcFrameParseResult refused = IpcFrameCodec.Parse(await raw.ReadLineAsync() ?? string.Empty);
            Assert.AreEqual(IpcFrameKind.Response, refused.Kind);
            Assert.AreEqual(9, refused.Response!.Id);
            Assert.IsFalse(refused.Response.Ok);
            Assert.AreEqual("E_IPC_PROTOCOL", refused.Response.Error!.Code);

            // The connection must still serve a real request after the refusal.
            await raw.WriteLineAsync("""{"v":1,"type":"request","id":10,"method":"get_status","params":{}}""");
            IpcFrameParseResult served = IpcFrameCodec.Parse(await raw.ReadLineAsync() ?? string.Empty);
            Assert.AreEqual(IpcFrameKind.Response, served.Kind);
            Assert.IsTrue(served.Response!.Ok, "A refused method must not take the whole connection down.");
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task BadJsonIsRefusedWithProtocolErrorAndClosesTheConnection()
    {
        string pipeName = NewPipeName();
        StubSessionToolHandler handler = new();
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (RawPipeClient raw = await RawPipeClient.ConnectAsync(pipeName))
        {
            await raw.WriteLineAsync("this is not json");

            IpcFrameParseResult refused = IpcFrameCodec.Parse(await raw.ReadLineAsync() ?? string.Empty);
            Assert.AreEqual(IpcFrameKind.Response, refused.Kind);
            Assert.IsFalse(refused.Response!.Ok);
            Assert.AreEqual("E_IPC_PROTOCOL", refused.Response.Error!.Code);

            string? afterClose = await raw.ReadLineAsync();
            Assert.IsNull(afterClose, "A frame that cannot be parsed ends the connection.");
        }

        // The server must keep accepting brand new connections after a bad frame.
        await using (RawPipeClient again = await RawPipeClient.ConnectAsync(pipeName))
        {
            await again.WriteLineAsync("""{"v":1,"type":"request","id":1,"method":"screenshot","params":{}}""");
            IpcFrameParseResult served = IpcFrameCodec.Parse(await again.ReadLineAsync() ?? string.Empty);
            Assert.IsTrue(served.Response!.Ok, "A new connection must still be served after the refusal.");
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task WrongVersionIsRefusedWithProtocolError()
    {
        string pipeName = NewPipeName();
        StubSessionToolHandler handler = new();
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (RawPipeClient raw = await RawPipeClient.ConnectAsync(pipeName))
        {
            await raw.WriteLineAsync("""{"v":2,"type":"request","id":4,"method":"get_status","params":{}}""");

            IpcFrameParseResult refused = IpcFrameCodec.Parse(await raw.ReadLineAsync() ?? string.Empty);
            Assert.AreEqual(IpcFrameKind.Response, refused.Kind);
            Assert.IsFalse(refused.Response!.Ok);
            Assert.AreEqual("E_IPC_PROTOCOL", refused.Response.Error!.Code);
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task MissingIdIsRefusedWithProtocolError()
    {
        string pipeName = NewPipeName();
        StubSessionToolHandler handler = new();
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (RawPipeClient raw = await RawPipeClient.ConnectAsync(pipeName))
        {
            await raw.WriteLineAsync("""{"v":1,"type":"request","method":"get_status","params":{}}""");

            IpcFrameParseResult refused = IpcFrameCodec.Parse(await raw.ReadLineAsync() ?? string.Empty);
            Assert.AreEqual(IpcFrameKind.Response, refused.Kind);
            Assert.IsFalse(refused.Response!.Ok);
            Assert.AreEqual("E_IPC_PROTOCOL", refused.Response.Error!.Code);
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task ConcurrentRequestsKeepTheirOwnResponses()
    {
        string pipeName = NewPipeName();
        StubSessionToolHandler handler = new() { StatusDelay = TimeSpan.FromMilliseconds(300) };
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (NamedPipeSessionClient client = new(pipeName))
        {
            // The delayed status call is answered after the fast screenshot call, so a naive
            // "answer in order" reader would hand the PNG to the status caller.
            Task<SessionStatus> statusCall = client.GetStatusAsync(CancellationToken.None);
            Task<byte[]> screenshotCall = client.ScreenshotAsync(CancellationToken.None);

            byte[] png = await screenshotCall.WaitAsync(CaseTimeout);
            SessionStatus status = await statusCall.WaitAsync(CaseTimeout);

            Assert.IsTrue(png.AsSpan().SequenceEqual(OnePixelPng), "The screenshot caller must receive the PNG.");
            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(StubSessionToolHandler.SessionId, status.SessionId);
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task TimeoutIsReportedAsAgentTimeoutInsteadOfHangingOrSucceeding()
    {
        string pipeName = NewPipeName();
        StubSessionToolHandler handler = new() { StatusDelay = Timeout.InfiniteTimeSpan };
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (NamedPipeSessionClient client = new(
            pipeName,
            actionTimeout: TimeSpan.FromMilliseconds(300),
            screenshotTimeout: TimeSpan.FromMilliseconds(300)))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code);
            Assert.IsTrue(error.Retryable, "A timeout is a retryable condition in the contract.");
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task EventFramesAreIgnoredAndDoNotBreakTheCall()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource serverLifetime = new();
        Task rawServer = RunRawOwnerAsync(
            pipeName,
            request => ["""{"v":1,"type":"event","event":"heartbeat","data":{"tick":123}}""", SuccessStatusLine(request.Id)],
            serverLifetime.Token);

        await using (NamedPipeSessionClient client = new(pipeName))
        {
            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);
        }

        serverLifetime.Cancel();
        await rawServer.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public void FramesRoundTripThroughThePureCodec()
    {
        JsonElement arguments = SessionToolPayloads.EmptyArguments();
        IpcRequest request = new(7, "screenshot", arguments);
        IpcFrameParseResult parsedRequest = IpcFrameCodec.Parse(IpcFrameCodec.WriteRequest(request));
        Assert.AreEqual(IpcFrameKind.Request, parsedRequest.Kind);
        Assert.AreEqual(7, parsedRequest.Request!.Id);
        Assert.AreEqual("screenshot", parsedRequest.Request.Method);

        IpcResponse success = IpcResponse.CreateSuccess(7, SessionToolPayloads.OkResult());
        IpcFrameParseResult parsedSuccess = IpcFrameCodec.Parse(IpcFrameCodec.WriteResponse(success));
        Assert.IsTrue(parsedSuccess.Response!.Ok);
        Assert.AreEqual(7, parsedSuccess.Response.Id);

        IpcResponse failure = IpcResponse.CreateFailure(7, IpcError.Protocol("refused"));
        IpcFrameParseResult parsedFailure = IpcFrameCodec.Parse(IpcFrameCodec.WriteResponse(failure));
        Assert.IsFalse(parsedFailure.Response!.Ok);
        Assert.AreEqual("E_IPC_PROTOCOL", parsedFailure.Response.Error!.Code);

        IpcEvent heartbeat = new("heartbeat", JsonSerializer.SerializeToElement(new { tick = 1 }));
        IpcFrameParseResult parsedEvent = IpcFrameCodec.Parse(IpcFrameCodec.WriteEvent(heartbeat));
        Assert.AreEqual(IpcFrameKind.Event, parsedEvent.Kind);
        Assert.AreEqual("heartbeat", parsedEvent.Event!.Name);

        foreach (string badLine in new[]
        {
            "not json",
            """{"v":2,"type":"request","id":1,"method":"get_status","params":{}}""",
            """{"v":1,"type":"hello","id":1}""",
            """{"v":1,"type":"request","method":"get_status","params":{}}""",
        })
        {
            Assert.AreEqual(IpcFrameKind.Invalid, IpcFrameCodec.Parse(badLine).Kind, badLine);
        }
    }

    [TestMethod]
    public void ContractTimeoutsAreTheOnesTheRulesCarry()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), SessionIpcRules.TimeoutFor(SessionTool.MouseMove));
        Assert.AreEqual(TimeSpan.FromSeconds(10), SessionIpcRules.TimeoutFor(SessionTool.KeyPress));
        Assert.AreEqual(TimeSpan.FromSeconds(15), SessionIpcRules.TimeoutFor(SessionTool.Screenshot));
        Assert.AreEqual("SoulComputerUse.owner", OwnerPipeNameFromConfiguration());
    }

    private static string OwnerPipeNameFromConfiguration()
    {
        return IpcPipeNames.Owner;
    }

    private static async Task RunRawOwnerAsync(string pipeName, Func<IpcRequest, IReadOnlyList<string>> respond, CancellationToken token)
    {
        await using NamedPipeServerStream stream = new(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await stream.WaitForConnectionAsync(token);

        using StreamReader reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        try
        {
            string? requestLine = await reader.ReadLineAsync(token);
            IpcFrameParseResult request = IpcFrameCodec.Parse(requestLine ?? string.Empty);
            Assert.AreEqual(IpcFrameKind.Request, request.Kind, "The raw owner expected the client's request frame.");

            foreach (string line in respond(request.Request!))
            {
                await writer.WriteLineAsync(line.AsMemory(), token);
            }
        }
        finally
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The client already closed its side.
            }
        }
    }

    private static string SuccessStatusLine(int id)
    {
        SessionStatus status = new(
            "ready",
            StubSessionToolHandler.SessionId,
            AuthTier.SystemCredential,
            GateStrength.Login,
            new SessionScreen(1920, 1080),
            null);
        return IpcFrameCodec.WriteResponse(IpcResponse.CreateSuccess(id, SessionToolPayloads.WriteStatus(status)));
    }

    private sealed class StubSessionToolHandler : ISessionToolHandler
    {
        public const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";

        private readonly ConcurrentDictionary<SessionTool, JsonElement> _arguments = new();
        private int _callCount;

        public IpcError? ForcedFailure { get; init; }

        public TimeSpan StatusDelay { get; init; } = TimeSpan.Zero;

        public int CallCount => Volatile.Read(ref _callCount);

        public JsonElement ArgumentsFor(SessionTool tool)
        {
            return _arguments[tool];
        }

        public async Task<SessionToolResult> HandleAsync(SessionTool tool, JsonElement arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _arguments[tool] = arguments.Clone();

            if (ForcedFailure is not null)
            {
                return SessionToolResult.Failure(SessionError.Create(
                    SessionErrorCodeNames.FromWireName(ForcedFailure.Code),
                    ForcedFailure.Message,
                    SessionState.Ready));
            }

            if (tool == SessionTool.GetStatus)
            {
                if (StatusDelay == Timeout.InfiniteTimeSpan)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                else if (StatusDelay > TimeSpan.Zero)
                {
                    await Task.Delay(StatusDelay, cancellationToken);
                }

                SessionStatus status = new(
                    "ready",
                    SessionId,
                    AuthTier.SystemCredential,
                    GateStrength.Login,
                    new SessionScreen(1920, 1080),
                    null);
                return SessionToolResult.Success(SessionToolPayloads.WriteStatus(status));
            }

            if (tool == SessionTool.Screenshot)
            {
                return SessionToolResult.Success(SessionToolPayloads.WriteScreenshot(OnePixelPng));
            }

            return SessionToolResult.Success(SessionToolPayloads.OkResult());
        }
    }

    /// <summary>A bare pipe client that speaks raw lines, used to send frames the typed client never sends.</summary>
    private sealed class RawPipeClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private RawPipeClient(NamedPipeClientStream stream, StreamReader reader, StreamWriter writer)
        {
            _stream = stream;
            _reader = reader;
            _writer = writer;
        }

        public static async Task<RawPipeClient> ConnectAsync(string pipeName)
        {
            NamedPipeClientStream stream = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(5000);
            StreamReader reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            return new RawPipeClient(stream, reader, writer);
        }

        public Task WriteLineAsync(string line)
        {
            return _writer.WriteLineAsync(line);
        }

        public async Task<string?> ReadLineAsync()
        {
            return await _reader.ReadLineAsync().WaitAsync(CaseTimeout);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _writer.Dispose();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The owner already closed its side; there is nothing left to flush.
            }

            _reader.Dispose();
            await _stream.DisposeAsync();
        }
    }
}