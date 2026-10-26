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
using SoulComputerUse.Windows.Ipc.Security;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The security boundary of the owner pipe against real Windows named pipes: the explicit DACL, the
/// peer account check in both directions, and the per-frame size limit.
/// </summary>
/// <remarks>
/// Every case uses a unique pipe name of the form <c>SoulComputerUse.owner.test-{guid}</c>, so a real
/// owner session on this machine can never be touched and no case can collide with another. The
/// cross-account connection is deliberately not tested here; it needs a second Windows account, which
/// this suite does not create. The SID rule itself is tested with well-known SIDs.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class OwnerPipeSecurityTests
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
    public async Task SecuredPipeStillServesStatusAndOneAction()
    {
        string pipeName = NewPipeName();
        StubOwnerToolHandler handler = new();
        await using NamedPipeSessionServer server = new(pipeName, handler);
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        await using (NamedPipeSessionClient client = new(pipeName))
        {
            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(StubOwnerToolHandler.SessionId, status.SessionId);
            Assert.AreEqual(1920, status.Screen.Width);
            Assert.AreEqual(1080, status.Screen.Height);

            await client.MouseClickAsync(7, 9, MouseButton.Left, 1, CancellationToken.None).WaitAsync(CaseTimeout);

            JsonElement clickArguments = handler.ArgumentsFor(SessionTool.MouseClick);
            Assert.AreEqual(7, clickArguments.GetProperty("x").GetInt32());
            Assert.AreEqual(9, clickArguments.GetProperty("y").GetInt32());
            Assert.AreEqual("left", clickArguments.GetProperty("button").GetString());
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
        Assert.AreEqual(2, handler.CallCount, "The owner must have seen the status and the click call.");
    }

    [TestMethod]
    public async Task PipeDaclAllowsOnlyCurrentUserAndLocalSystem()
    {
        string pipeName = NewPipeName();
        await using NamedPipeSessionServer server = new(pipeName, new StubOwnerToolHandler());
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        PipeSecurity security = server.ReadAccessControl();
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));

        Assert.HasCount(2, rules);
        Assert.IsTrue(security.AreAccessRulesProtected, "The DACL must not inherit rules from a parent object.");

        SecurityIdentifier currentUser = OwnerPipePeer.CurrentUserSid();
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
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), allowed);

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task OversizedClientFrameIsRefusedAndTheServerKeepsAccepting()
    {
        string pipeName = NewPipeName();
        await using NamedPipeSessionServer server = new(pipeName, new StubOwnerToolHandler());
        using CancellationTokenSource serverLifetime = new();
        Task acceptLoop = server.RunAsync(serverLifetime.Token);

        // One byte over the limit is enough to prove the refusal happens while the line is read; the
        // rest of the line is never buffered by the server.
        string oversized = new string('x', OwnerPipeLimits.MaximumFrameBytes + 1);

        await using (RawPipeClient raw = await RawPipeClient.ConnectAsync(pipeName))
        {
            Task<bool> write = raw.TryWriteLineAsync(oversized);
            string? refusalLine = await raw.ReadLineAsync();

            Assert.IsNotNull(refusalLine, "The owner must answer an oversized frame with one refusal frame.");
            IpcFrameParseResult refusal = IpcFrameCodec.Parse(refusalLine);
            Assert.AreEqual(IpcFrameKind.Response, refusal.Kind);
            Assert.AreEqual(0, refusal.Response!.Id, "A frame refused before a request is read carries the fixed id zero.");
            Assert.IsFalse(refusal.Response.Ok);
            Assert.AreEqual("E_IPC_PROTOCOL", refusal.Response.Error!.Code);

            // Whether the tail of the line fits the kernel buffer is not part of the contract; only the
            // refusal and the close below are. The task is observed so no exception goes unowned.
            await write.WaitAsync(CaseTimeout);
            Assert.IsNull(await raw.ReadLineAsync(), "The oversized frame ends the connection.");
        }

        // A refusal must not take the accept loop down: a fresh connection is served normally.
        await using (NamedPipeSessionClient client = new(pipeName))
        {
            SessionStatus status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);
        }

        serverLifetime.Cancel();
        await acceptLoop.WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task OversizedServerFrameFailsTheCallWithProtocolError()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource serverLifetime = new();
        Task rawServer = RunRawOversizedOwnerAsync(pipeName, serverLifetime.Token);

        await using (NamedPipeSessionClient client = new(pipeName))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => client.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.IpcProtocol, error.Code);
            Assert.IsFalse(error.Retryable);
        }

        serverLifetime.Cancel();
        await AssertRawServerEndedAsync(rawServer);
    }

    [TestMethod]
    public void PeerRuleAcceptsTheSameSidAndRefusesAnotherSid()
    {
        SecurityIdentifier currentUser = OwnerPipePeer.CurrentUserSid();
        OwnerPipePeer.EnsureSameUser(currentUser, currentUser);

        SecurityIdentifier localService = new(WellKnownSidType.LocalServiceSid, null);
        Assert.AreNotEqual(currentUser, localService);

        SessionOperationException error = Assert.ThrowsExactly<SessionOperationException>(
            () => OwnerPipePeer.EnsureSameUser(currentUser, localService));

        Assert.AreEqual(SessionErrorCode.IpcPeer, error.Code);
        Assert.IsFalse(error.Retryable);
        Assert.IsFalse(error.Message.Contains(currentUser.Value, StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(localService.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// Both refusals a connection can receive are uncorrelated: they are sent before any request id is
    /// known, so they carry the fixed id zero and one contract error code each.
    /// </summary>
    [TestMethod]
    public void ConnectionRefusalsCarryTheFixedUncorrelatedId()
    {
        IpcFrameParseResult peer = IpcFrameCodec.Parse(IpcFrameCodec.WriteResponse(
            OwnerPipeRefusals.Peer("The named pipe peer runs under a different Windows account, so the connection was refused.")));
        Assert.AreEqual(IpcFrameKind.Response, peer.Kind);
        Assert.AreEqual(OwnerPipeRefusals.UncorrelatedId, peer.Response!.Id);
        Assert.AreEqual(0, peer.Response.Id);
        Assert.IsFalse(peer.Response.Ok);
        Assert.AreEqual("E_IPC_PEER", peer.Response.Error!.Code);
        Assert.IsFalse(peer.Response.Error.Retryable);

        IpcFrameParseResult tooLong = IpcFrameCodec.Parse(IpcFrameCodec.WriteResponse(OwnerPipeRefusals.FrameTooLong()));
        Assert.AreEqual(IpcFrameKind.Response, tooLong.Kind);
        Assert.AreEqual(0, tooLong.Response!.Id);
        Assert.IsFalse(tooLong.Response.Ok);
        Assert.AreEqual("E_IPC_PROTOCOL", tooLong.Response.Error!.Code);
        Assert.IsFalse(tooLong.Response.Error.Retryable);
    }

    private static async Task RunRawOversizedOwnerAsync(string pipeName, CancellationToken token)
    {
        await using NamedPipeServerStream stream = new(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await stream.WaitForConnectionAsync(token);

        using StreamReader reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        string? requestLine = await reader.ReadLineAsync(token);
        Assert.IsNotNull(requestLine, "The raw owner expected the client's request frame.");

        string oversized = new string('y', OwnerPipeLimits.MaximumFrameBytes + 1);
        using StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n",
        };

        try
        {
            await writer.WriteLineAsync(oversized.AsMemory(), token);
            await writer.FlushAsync(token);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client refused the line and closed its side while the rest was still being written.
        }
    }

    private static async Task AssertRawServerEndedAsync(Task rawServer)
    {
        try
        {
            await rawServer.WaitAsync(CaseTimeout);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The raw owner saw its pipe closed by the refused client, which is the expected ending.
        }
    }

    private sealed class StubOwnerToolHandler : ISessionToolHandler
    {
        public const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";

        private readonly ConcurrentDictionary<SessionTool, JsonElement> _arguments = new();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public JsonElement ArgumentsFor(SessionTool tool)
        {
            return _arguments[tool];
        }

        public Task<SessionToolResult> HandleAsync(SessionTool tool, JsonElement arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _arguments[tool] = arguments.Clone();

            SessionToolResult result = tool switch
            {
                SessionTool.GetStatus => SessionToolResult.Success(SessionToolPayloads.WriteStatus(new SessionStatus(
                    "ready",
                    SessionId,
                    AuthTier.SystemCredential,
                    GateStrength.Login,
                    new SessionScreen(1920, 1080),
                    null))),
                SessionTool.Screenshot => SessionToolResult.Success(SessionToolPayloads.WriteScreenshot(OnePixelPng)),
                _ => SessionToolResult.Success(SessionToolPayloads.OkResult()),
            };

            return Task.FromResult(result);
        }
    }

    /// <summary>A bare pipe client that speaks raw lines, used to send a frame the typed client never sends.</summary>
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
            StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            return new RawPipeClient(stream, reader, writer);
        }

        /// <summary>Writes one line and reports whether the owner accepted all of it.</summary>
        public async Task<bool> TryWriteLineAsync(string line)
        {
            try
            {
                await _writer.WriteLineAsync(line.AsMemory());
                return true;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                return false;
            }
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
