using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.Ipc.Agent;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The host to child-session agent pipe against real Windows named pipes: the secured DACL, the
/// nonce handshake, heartbeat and request timeouts, id correlation and protocol refusals.
/// </summary>
/// <remarks>
/// Every case uses a unique pipe name of the form <c>SoulComputerUse.agent.test-{guid}</c>, so a real
/// agent on this machine can never be reached. The tests never start a second Windows account; the
/// peer SID check is exercised against the current account and its mismatch rule is covered as a pure
/// decision.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class AgentIpcTests
{
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(30);
    private const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";

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
        return "SoulComputerUse.agent.test-" + Guid.NewGuid().ToString("D");
    }

    private static AgentServerTimings ServerTimings()
    {
        return new AgentServerTimings(
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatInterval: TimeSpan.FromMilliseconds(100));
    }

    private static AgentServerTimings QuietServerTimings()
    {
        return new AgentServerTimings(
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatInterval: TimeSpan.FromHours(1));
    }

    private static AgentClientTimings ClientTimings()
    {
        return new AgentClientTimings(
            connectTimeout: TimeSpan.FromSeconds(5),
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatSilenceTimeout: TimeSpan.FromSeconds(2),
            heartbeatCheckInterval: TimeSpan.FromMilliseconds(50),
            actionTimeout: TimeSpan.FromSeconds(10),
            screenshotTimeout: TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public async Task RealPipeRoundTripReturnsStatusScreenshotAndActionResults()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        StubAgentToolHandler handler = new();
        await using AgentPipeServer server = new(pipeName, nonce, handler, ServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (AgentPipeClient client = new(
            pipeName,
            nonce,
            ClientTimings(),
            new WindowsAgentProcessProbe()))
        {
            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(SessionId, status.SessionId);
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

        await run.WaitAsync(CaseTimeout);
        Assert.IsGreaterThanOrEqualTo(3, handler.CallCount, "The agent must have seen the status, screenshot and click calls.");
    }

    [TestMethod]
    public async Task ConcurrentRequestsKeepTheirOwnResponses()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        StubAgentToolHandler handler = new() { StatusDelay = TimeSpan.FromMilliseconds(300) };
        await using AgentPipeServer server = new(pipeName, nonce, handler, ServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (AgentPipeClient client = new(
            pipeName,
            nonce,
            ClientTimings(),
            new WindowsAgentProcessProbe()))
        {
            Task<SessionStatus> statusCall = client.GetStatusAsync(CancellationToken.None);
            Task<byte[]> screenshotCall = client.ScreenshotAsync(CancellationToken.None);

            byte[] png = await screenshotCall.WaitAsync(CaseTimeout);
            SessionStatus status = await statusCall.WaitAsync(CaseTimeout);

            Assert.IsTrue(png.AsSpan().SequenceEqual(OnePixelPng), "The screenshot caller must receive the PNG.");
            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(SessionId, status.SessionId);
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task WrongNonceIsReportedAsIpcPeer()
    {
        string pipeName = NewPipeName();
        string serverNonce = AgentNonce.Create();
        string clientNonce = AgentNonce.Create();
        await using AgentPipeServer server = new(pipeName, serverNonce, new StubAgentToolHandler(), ServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (AgentPipeClient client = new(
            pipeName,
            clientNonce,
            ClientTimings(),
            new WindowsAgentProcessProbe()))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.IpcPeer, error.Code);
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task MissingHelloAckIsReportedAsAgentTimeout()
    {
        string pipeName = NewPipeName();
        AgentServerTimings timings = new(
            handshakeTimeout: TimeSpan.FromMilliseconds(300),
            heartbeatInterval: TimeSpan.FromSeconds(5));
        await using AgentPipeServer server = new(pipeName, AgentNonce.Create(), new StubAgentToolHandler(), timings);
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (RawAgentClient raw = await RawAgentClient.ConnectAsync(pipeName))
        {
            string? helloLine = await raw.ReadLineAsync();
            Assert.IsNotNull(helloLine);
            IpcFrameParseResult hello = IpcFrameCodec.Parse(helloLine);
            Assert.AreEqual(IpcFrameKind.Event, hello.Kind);
            Assert.AreEqual(AgentHandshake.HelloEvent, hello.Event!.Name);

            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => run.WaitAsync(CaseTimeout));
            Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code);
            Assert.IsTrue(error.Retryable, "A missed handshake is the contract's retryable timeout.");
        }
    }

    [TestMethod]
    public async Task HeartbeatSilenceIsReportedAsAgentTimeout()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        AgentServerTimings serverTimings = new(
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatInterval: TimeSpan.FromHours(1));
        AgentClientTimings clientTimings = new(
            connectTimeout: TimeSpan.FromSeconds(5),
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatSilenceTimeout: TimeSpan.FromMilliseconds(300),
            heartbeatCheckInterval: TimeSpan.FromMilliseconds(50),
            actionTimeout: TimeSpan.FromSeconds(10),
            screenshotTimeout: TimeSpan.FromSeconds(15));
        FixedAgentProcessProbe probe = new() { Alive = true };
        await using AgentPipeServer server = new(pipeName, nonce, new StubAgentToolHandler(), serverTimings);
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (AgentPipeClient client = new(pipeName, nonce, clientTimings, probe))
        {
            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);

            await Task.Delay(600);

            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));
            Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code);
            Assert.IsGreaterThanOrEqualTo(1, probe.ProbeCount, "The client must probe the agent process before deciding the failure.");
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task HeartbeatSilenceWithDeadAgentProcessIsReportedAsAgentDead()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        AgentServerTimings serverTimings = new(
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatInterval: TimeSpan.FromHours(1));
        AgentClientTimings clientTimings = new(
            connectTimeout: TimeSpan.FromSeconds(5),
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatSilenceTimeout: TimeSpan.FromMilliseconds(300),
            heartbeatCheckInterval: TimeSpan.FromMilliseconds(50),
            actionTimeout: TimeSpan.FromSeconds(10),
            screenshotTimeout: TimeSpan.FromSeconds(15));
        FixedAgentProcessProbe probe = new() { Alive = false };
        await using AgentPipeServer server = new(pipeName, nonce, new StubAgentToolHandler(), serverTimings);
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (AgentPipeClient client = new(pipeName, nonce, clientTimings, probe))
        {
            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);

            await Task.Delay(600);

            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));
            Assert.AreEqual(SessionErrorCode.AgentDead, error.Code);
            Assert.IsFalse(error.Retryable, "A dead agent is not a retryable condition in the contract.");
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task HeartbeatEventsCarryAnIncreasingTick()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        AgentServerTimings timings = new(
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatInterval: TimeSpan.FromMilliseconds(50));
        await using AgentPipeServer server = new(pipeName, nonce, new StubAgentToolHandler(), timings);
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        long firstTick;
        long secondTick;
        await using (RawAgentClient raw = await ConnectAndHandshakeAsync(pipeName))
        {
            firstTick = await ReadHeartbeatTickAsync(raw);
            secondTick = await ReadHeartbeatTickAsync(raw);
        }

        Assert.IsGreaterThanOrEqualTo(1, firstTick);
        Assert.IsGreaterThan(firstTick, secondTick, "Each heartbeat tick must be greater than the previous one.");
        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task ServerStopsListeningAfterTheSingleConnectionEnds()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        await using AgentPipeServer server = new(pipeName, nonce, new StubAgentToolHandler(), ServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (AgentPipeClient client = new(
            pipeName,
            nonce,
            ClientTimings(),
            new WindowsAgentProcessProbe()))
        {
            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);
        }

        await run.WaitAsync(CaseTimeout);

        NamedPipeClientStream second = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => second.ConnectAsync(300));
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ErrorResponseKeepsItsCodeMessageAndRetryableValue()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        StubAgentToolHandler handler = new()
        {
            ForcedFailure = SessionError.Create(
                SessionErrorCode.AgentError,
                "window-gone",
                SessionState.Ready),
        };
        await using AgentPipeServer server = new(pipeName, nonce, handler, ServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (AgentPipeClient client = new(
            pipeName,
            nonce,
            ClientTimings(),
            new WindowsAgentProcessProbe()))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.MouseMoveAsync(5, 6, CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.AgentError, error.Code);
            Assert.AreEqual("window-gone", error.Message);
            Assert.IsTrue(error.Retryable);
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task RequestTimeoutIsReportedOnceWithoutRetryingTheSameId()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        StubAgentToolHandler handler = new() { StatusDelay = Timeout.InfiniteTimeSpan };
        AgentClientTimings clientTimings = new(
            connectTimeout: TimeSpan.FromSeconds(5),
            handshakeTimeout: TimeSpan.FromSeconds(3),
            heartbeatSilenceTimeout: TimeSpan.FromSeconds(10),
            heartbeatCheckInterval: TimeSpan.FromMilliseconds(50),
            actionTimeout: TimeSpan.FromMilliseconds(300),
            screenshotTimeout: TimeSpan.FromSeconds(15));
        await using AgentPipeServer server = new(pipeName, nonce, handler, ServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (AgentPipeClient client = new(
            pipeName,
            nonce,
            clientTimings,
            new WindowsAgentProcessProbe()))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code);
            Assert.IsTrue(error.Retryable);
            Assert.AreEqual(1, handler.CallCount, "A timed-out id must not be sent again.");
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task BadJsonIsRefusedWithProtocolErrorAndClosesTheConnection()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        await using AgentPipeServer server = new(pipeName, nonce, new StubAgentToolHandler(), QuietServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (RawAgentClient raw = await ConnectAndHandshakeAsync(pipeName))
        {
            await raw.WriteLineAsync("this is not json");

            IpcResponse refused = await raw.ReadResponseAsync();
            Assert.IsFalse(refused.Ok);
            Assert.AreEqual("E_IPC_PROTOCOL", refused.Error!.Code);
            Assert.IsNull(await raw.ReadLineAsync(), "A frame that cannot be parsed ends the connection.");
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task WrongVersionIsRefusedWithProtocolError()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        await using AgentPipeServer server = new(pipeName, nonce, new StubAgentToolHandler(), QuietServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (RawAgentClient raw = await ConnectAndHandshakeAsync(pipeName))
        {
            await raw.WriteLineAsync("""{"v":2,"type":"request","id":4,"method":"get_status","params":{}}""");

            IpcResponse refused = await raw.ReadResponseAsync();
            Assert.AreEqual(4, refused.Id);
            Assert.IsFalse(refused.Ok);
            Assert.AreEqual("E_IPC_PROTOCOL", refused.Error!.Code);
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task UnknownMethodIsRefusedWithProtocolErrorAndKeepsTheConnectionAlive()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        await using AgentPipeServer server = new(pipeName, nonce, new StubAgentToolHandler(), QuietServerTimings());
        Task run = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await using (RawAgentClient raw = await ConnectAndHandshakeAsync(pipeName))
        {
            await raw.WriteLineAsync("""{"v":1,"type":"request","id":9,"method":"shell","params":{}}""");

            IpcResponse refused = await raw.ReadResponseAsync();
            Assert.AreEqual(9, refused.Id);
            Assert.IsFalse(refused.Ok);
            Assert.AreEqual("E_IPC_PROTOCOL", refused.Error!.Code);

            await raw.WriteLineAsync("""{"v":1,"type":"request","id":10,"method":"get_status","params":{}}""");
            IpcResponse served = await raw.ReadResponseAsync();
            Assert.AreEqual(10, served.Id);
            Assert.IsTrue(served.Ok, "A refused method must not take the whole agent connection down.");
        }

        await run.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task HelloWithWrongVersionIsReportedAsIpcProtocol()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        string wrongHello = "{\"v\":1,\"type\":\"event\",\"event\":\"hello\",\"data\":{\"v\":2,\"capabilities\":[\"screen\",\"mouse\",\"keyboard\"],\"nonce\":\""
            + nonce
            + "\"}}";
        using CancellationTokenSource lifetime = new();
        NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task rawRun = WriteRawHelloAndWaitAsync(pipe, wrongHello, lifetime.Token);
        try
        {
            await using AgentPipeClient client = new(
                pipeName,
                nonce,
                ClientTimings(),
                new WindowsAgentProcessProbe());

            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.IpcProtocol, error.Code);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await rawRun.WaitAsync(CaseTimeout);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // The raw server was still waiting for the client.
            }

            await pipe.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task HelloThatIsNotAnEventIsReportedAsIpcProtocol()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        const string WrongFrame = """{"v":1,"type":"response","id":1,"ok":true,"result":{}}""";
        using CancellationTokenSource lifetime = new();
        NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task rawRun = WriteRawHelloAndWaitAsync(pipe, WrongFrame, lifetime.Token);
        try
        {
            await using AgentPipeClient client = new(
                pipeName,
                nonce,
                ClientTimings(),
                new WindowsAgentProcessProbe());

            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.IpcProtocol, error.Code);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await rawRun.WaitAsync(CaseTimeout);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // The raw server was still waiting for the client.
            }

            await pipe.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task UnknownErrorCodeIsRefusedExplicitly()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        using CancellationTokenSource lifetime = new();
        NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task rawRun = RunRawAgentServerAsync(pipe, nonce, lifetime.Token);
        try
        {
            await using AgentPipeClient client = new(
                pipeName,
                nonce,
                ClientTimings(),
                new WindowsAgentProcessProbe());

            SessionOperationException unknownCodeError = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));
            Assert.AreEqual(SessionErrorCode.IpcProtocol, unknownCodeError.Code);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await rawRun.WaitAsync(CaseTimeout);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // The raw server was still waiting for the client.
            }

            await pipe.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task UnknownEventIsIgnoredAndTheNextResponseStillArrives()
    {
        string pipeName = NewPipeName();
        string nonce = AgentNonce.Create();
        using CancellationTokenSource lifetime = new();
        NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task rawRun = RunRawAgentServerWithUnknownEventAsync(pipe, nonce, lifetime.Token);
        try
        {
            await using AgentPipeClient client = new(
                pipeName,
                nonce,
                ClientTimings(),
                new WindowsAgentProcessProbe());

            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(SessionId, status.SessionId);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await rawRun.WaitAsync(CaseTimeout);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // The raw server was still waiting for the client.
            }

            await pipe.DisposeAsync();
        }
    }

    [TestMethod]
    public void InvalidSessionIdsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => AgentPipeNames.ForSession(null!));
        Assert.ThrowsExactly<ArgumentException>(() => AgentPipeNames.ForSession(string.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => AgentPipeNames.ForSession("   "));
        Assert.ThrowsExactly<ArgumentException>(() => AgentPipeNames.ForSession("not-a-uuid"));
        Assert.ThrowsExactly<ArgumentException>(() => AgentPipeNames.ForSession("b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f/../escape"));
        Assert.ThrowsExactly<ArgumentException>(() => AgentPipeNames.ForSession("b3f1c2d456784abc9def0a1b2c3d4e5f"));

        string sessionId = Guid.NewGuid().ToString("D");
        Assert.AreEqual("SoulComputerUse.agent." + sessionId, AgentPipeNames.ForSession(sessionId));
    }

    [TestMethod]
    public void DifferentPeerSidIsRefusedAsIpcPeer()
    {
        SecurityIdentifier currentUser = AgentPeerIdentity.CurrentUserSid();
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        Assert.AreNotEqual(currentUser, localSystem);

        SessionOperationException error = Assert.ThrowsExactly<SessionOperationException>(
            () => AgentPeerIdentity.EnsureSameUser(currentUser, localSystem));

        Assert.AreEqual(SessionErrorCode.IpcPeer, error.Code);
        Assert.IsFalse(error.Retryable);
    }

    [TestMethod]
    public async Task PipeDaclAllowsOnlyCurrentUserAndLocalSystem()
    {
        string pipeName = NewPipeName();
        await using AgentPipeServer server = new(
            pipeName,
            AgentNonce.Create(),
            new StubAgentToolHandler(),
            ServerTimings());
        _ = server.RunAsync(CancellationToken.None);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        PipeSecurity security = server.ReadAccessControl();
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));

        Assert.HasCount(2, rules);
        Assert.IsTrue(security.AreAccessRulesProtected, "The DACL must not inherit rules from a parent object.");

        SecurityIdentifier currentUser = AgentPeerIdentity.CurrentUserSid();
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        HashSet<SecurityIdentifier> allowed = [];
        foreach (AuthorizationRule rule in rules)
        {
            PipeAccessRule pipeRule = (PipeAccessRule)rule;
            Assert.AreEqual(AccessControlType.Allow, pipeRule.AccessControlType);
            Assert.AreEqual(PipeAccessRights.FullControl, pipeRule.PipeAccessRights);
            allowed.Add((SecurityIdentifier)pipeRule.IdentityReference);
        }

        Assert.Contains(currentUser, allowed);
        Assert.Contains(localSystem, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), allowed);

        await server.DisposeAsync();
    }

    [TestMethod]
    public async Task RunAsyncPropagatesCallerCancellation()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource lifetime = new();
        await using AgentPipeServer server = new(
            pipeName,
            AgentNonce.Create(),
            new StubAgentToolHandler(),
            ServerTimings());
        Task run = server.RunAsync(lifetime.Token);
        await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        lifetime.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => run.WaitAsync(CaseTimeout));
    }

    private static async Task<RawAgentClient> ConnectAndHandshakeAsync(string pipeName)
    {
        RawAgentClient raw = await RawAgentClient.ConnectAsync(pipeName);
        try
        {
            string? helloLine = await raw.ReadLineAsync();
            Assert.IsNotNull(helloLine);
            IpcFrameParseResult hello = IpcFrameCodec.Parse(helloLine);
            Assert.AreEqual(IpcFrameKind.Event, hello.Kind);
            Assert.AreEqual(AgentHandshake.HelloEvent, hello.Event!.Name);

            await raw.WriteLineAsync(IpcFrameCodec.WriteRequest(AgentHandshake.CreateHelloAck(1)));

            IpcResponse ack = await raw.ReadResponseAsync();
            Assert.IsTrue(ack.Ok);
            return raw;
        }
        catch (Exception)
        {
            await raw.DisposeAsync();
            throw;
        }
    }

    private static async Task<long> ReadHeartbeatTickAsync(RawAgentClient raw)
    {
        while (true)
        {
            string? line = await raw.ReadLineAsync();
            if (line is null)
            {
                throw new AssertFailedException("The agent closed the pipe before sending a heartbeat.");
            }

            IpcFrameParseResult parsed = IpcFrameCodec.Parse(line);
            if (parsed.Kind == IpcFrameKind.Event
                && string.Equals(parsed.Event!.Name, AgentHandshake.HeartbeatEvent, StringComparison.Ordinal))
            {
                return parsed.Event.Data.GetProperty("tick").GetInt64();
            }

            if (parsed.Kind == IpcFrameKind.Invalid)
            {
                throw new AssertFailedException("The agent sent a frame that is not valid JSON.");
            }
        }
    }

    private static async Task WriteRawHelloAndWaitAsync(
        NamedPipeServerStream pipe,
        string helloLine,
        CancellationToken token)
    {
        try
        {
            await pipe.WaitForConnectionAsync(token);
            using StreamReader reader = new(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
            StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            try
            {
                await writer.WriteLineAsync(helloLine.AsMemory(), token);
                try
                {
                    _ = await reader.ReadLineAsync(token);
                }
                catch (IOException)
                {
                    // The client closed after refusing the hello frame.
                }
            }
            finally
            {
                // The frame is already written because this writer auto-flushes, so the final flush
                // on disposal races the client's close; a broken pipe here is the same expected close
                // the read above tolerates. The write itself stays strict: a broken pipe there still
                // fails the case.
                try
                {
                    writer.Dispose();
                }
                catch (IOException)
                {
                    // The client closed its side first.
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The test is done with the raw server.
        }
    }

    private static async Task RunRawAgentServerAsync(
        NamedPipeServerStream pipe,
        string nonce,
        CancellationToken token)
    {
        try
        {
            await pipe.WaitForConnectionAsync(token);
            using StreamReader reader = new(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
            StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            try
            {
                await writer.WriteLineAsync(AgentHandshake.WriteHello(nonce).AsMemory(), token);

                string? ackLine = await reader.ReadLineAsync(token);
                Assert.IsNotNull(ackLine);
                IpcFrameParseResult ack = IpcFrameCodec.Parse(ackLine);
                Assert.AreEqual(IpcFrameKind.Request, ack.Kind);

                string unknownCode = "{\"v\":1,\"type\":\"response\",\"id\":"
                    + ack.Request!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"ok\":false,\"error\":{\"code\":\"E_NOT_A_REAL_CODE\",\"message\":\"outside the contract\",\"retryable\":false,\"state\":null}}";
                await writer.WriteLineAsync(unknownCode.AsMemory(), token);
            }
            finally
            {
                // The response is already written because this writer auto-flushes, so the final
                // flush on disposal races the client's close; a broken pipe here is the same
                // expected close the earlier reads tolerate. Every write above stays strict.
                try
                {
                    writer.Dispose();
                }
                catch (IOException)
                {
                    // The client closed its side first.
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The test is done with the raw server.
        }
    }

    private static async Task RunRawAgentServerWithUnknownEventAsync(
        NamedPipeServerStream pipe,
        string nonce,
        CancellationToken token)
    {
        try
        {
            await pipe.WaitForConnectionAsync(token);
            using StreamReader reader = new(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
            StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            try
            {
                await writer.WriteLineAsync(AgentHandshake.WriteHello(nonce).AsMemory(), token);

                string? ackLine = await reader.ReadLineAsync(token);
                Assert.IsNotNull(ackLine);
                IpcFrameParseResult ack = IpcFrameCodec.Parse(ackLine);
                Assert.AreEqual(IpcFrameKind.Request, ack.Kind);
                string ackResponse = IpcFrameCodec.WriteResponse(
                    IpcResponse.CreateSuccess(ack.Request!.Id, SessionToolPayloads.OkResult()));
                await writer.WriteLineAsync(ackResponse.AsMemory(), token);

                await writer.WriteLineAsync(
                    """{"v":1,"type":"event","event":"telemetry","data":{"sequence":1}}""".AsMemory(),
                    token);

                string? requestLine = await reader.ReadLineAsync(token);
                Assert.IsNotNull(requestLine);
                IpcFrameParseResult request = IpcFrameCodec.Parse(requestLine);
                Assert.AreEqual(IpcFrameKind.Request, request.Kind);

                SessionStatus status = new(
                    "ready",
                    SessionId,
                    AuthTier.SystemCredential,
                    GateStrength.Login,
                    new SessionScreen(1920, 1080),
                    null);
                string statusResponse = IpcFrameCodec.WriteResponse(
                    IpcResponse.CreateSuccess(request.Request!.Id, SessionToolPayloads.WriteStatus(status)));
                await writer.WriteLineAsync(statusResponse.AsMemory(), token);
            }
            finally
            {
                // The response is already written because this writer auto-flushes, so the final
                // flush on disposal races the client's close; a broken pipe here is the same
                // expected close the earlier reads tolerate. Every write above stays strict.
                try
                {
                    writer.Dispose();
                }
                catch (IOException)
                {
                    // The client closed its side first.
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The test is done with the raw server.
        }
    }

    private sealed class StubAgentToolHandler : ISessionToolHandler
    {
        private readonly ConcurrentDictionary<SessionTool, JsonElement> _arguments = new();
        private int _callCount;

        public SessionError? ForcedFailure { get; init; }

        public TimeSpan StatusDelay { get; init; } = TimeSpan.Zero;

        public int CallCount => Volatile.Read(ref _callCount);

        public JsonElement ArgumentsFor(SessionTool tool)
        {
            return _arguments[tool];
        }

        public async Task<SessionToolResult> HandleAsync(
            SessionTool tool,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _arguments[tool] = arguments.Clone();

            if (ForcedFailure is not null)
            {
                return SessionToolResult.Failure(ForcedFailure);
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

    private sealed class FixedAgentProcessProbe : IAgentProcessProbe
    {
        private int _probeCount;

        public bool Alive { get; init; }

        public int ProbeCount => Volatile.Read(ref _probeCount);

        public bool TryIsAlive(int processId, DateTime processStartTimeUtc, out bool isAlive)
        {
            Interlocked.Increment(ref _probeCount);
            isAlive = Alive;
            return true;
        }
    }

    private sealed class RawAgentClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private RawAgentClient(NamedPipeClientStream stream, StreamReader reader, StreamWriter writer)
        {
            _stream = stream;
            _reader = reader;
            _writer = writer;
        }

        public static async Task<RawAgentClient> ConnectAsync(string pipeName)
        {
            NamedPipeClientStream stream = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(5000).WaitAsync(CaseTimeout);
            StreamReader reader = new(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
            StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            return new RawAgentClient(stream, reader, writer);
        }

        public Task WriteLineAsync(string line)
        {
            return _writer.WriteLineAsync(line);
        }

        public async Task<string?> ReadLineAsync()
        {
            return await _reader.ReadLineAsync().WaitAsync(CaseTimeout);
        }

        public async Task<IpcResponse> ReadResponseAsync()
        {
            while (true)
            {
                string? line = await ReadLineAsync();
                if (line is null)
                {
                    throw new AssertFailedException("The agent closed the pipe before sending a response.");
                }

                IpcFrameParseResult parsed = IpcFrameCodec.Parse(line);
                if (parsed.Kind == IpcFrameKind.Response)
                {
                    return parsed.Response!;
                }

                if (parsed.Kind != IpcFrameKind.Event)
                {
                    throw new AssertFailedException("The agent sent a frame that is not an event or a response.");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _writer.Dispose();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The agent already closed its side; there is nothing left to flush.
            }

            _reader.Dispose();
            await _stream.DisposeAsync();
        }
    }
}