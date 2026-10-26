using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The owner foundation against a real mutex, a real ownership record and real named pipes: start
/// order, record contents, the handler adapter, lock exclusion, state rewrites, and shutdown cleanup.
/// </summary>
/// <remarks>
/// Every case uses a run-unique lock name, a run-unique pipe name and a store rooted in a temporary
/// directory. A real <c>SoulComputerUse.owner</c> session can never be touched, and no case can
/// contend with another.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class SessionOwnerServiceTests
{
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";
    private const int ScreenshotByteLimit = SessionIpcRules.ScreenshotByteLimit;
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset HostStartTime = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AcquiredAt = new(2026, 10, 26, 8, 0, 1, TimeSpan.Zero);

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

    private readonly List<SessionOwnerService> _services = [];
    private string _root = string.Empty;
    private string _lockName = string.Empty;
    private string _pipeName = string.Empty;
    private string _sessionId = string.Empty;
    private SessionOwnerStore _store = null!;

    [TestInitialize]
    public void CreateTemporaryState()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-owner-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _lockName = NewLockName();
        _pipeName = NewPipeName();
        _sessionId = Guid.NewGuid().ToString("D");
        _store = new SessionOwnerStore(_root, TestSid);
    }

    [TestCleanup]
    public async Task DeleteTemporaryState()
    {
        foreach (SessionOwnerService service in _services)
        {
            await service.DisposeAsync();
        }

        _services.Clear();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void StartWritesTheOwnershipRecordInTheStartingState()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionOwnerService service = CreateService(new SessionClientToolHandler(client));

        service.Start();

        Assert.IsTrue(File.Exists(_store.RecordPath), "Starting the owner must create its ownership record.");
        SessionOwnerRecord? record = _store.Read();
        Assert.IsNotNull(record);
        Assert.AreEqual(_sessionId, record.SessionId);
        Assert.AreEqual(Environment.ProcessId, record.HostPid);
        Assert.AreEqual(HostStartTime, record.HostProcessStartTime);
        Assert.AreEqual(0, record.SubagentWtsSessionId);
        Assert.IsFalse(record.ChildSessionsOpenedByUs);
        Assert.AreEqual(SessionState.Starting, record.State);
        Assert.AreEqual(AcquiredAt, record.AcquiredAt);
        Assert.AreEqual(SessionState.Starting, service.CurrentState);
        Assert.AreEqual(_sessionId, service.SessionId);
        Assert.AreEqual(_pipeName, service.PipeName);
        Assert.AreEqual(_lockName, service.LockName);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_store.RecordPath));
        JsonElement root = document.RootElement;
        string[] fields =
        [
            "sessionId",
            "hostPid",
            "hostProcessStartTime",
            "subagentWtsSessionId",
            "childSessionsOpenedByUs",
            "state",
            "acquiredAt",
        ];
        int fieldCount = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            fieldCount++;
            Assert.Contains(property.Name, fields, "The record must carry only the seven contract fields.");
        }

        Assert.AreEqual(fields.Length, fieldCount);
        foreach (string field in fields)
        {
            Assert.IsTrue(root.TryGetProperty(field, out JsonElement _), "The record must carry '" + field + "'.");
        }

        Assert.AreEqual("starting", root.GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task OwnerPipeServesStatusScreenshotAndEveryAction()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionOwnerService service = CreateService(new SessionClientToolHandler(client));
        service.Start();

        await using (NamedPipeSessionClient pipeClient = new(service.PipeName))
        {
            SessionStatus status = await pipeClient.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("ready", status.State);
            Assert.AreEqual(_sessionId, status.SessionId);
            Assert.AreEqual(AuthTier.SystemCredential, status.AuthTier);
            Assert.AreEqual(GateStrength.Login, status.GateStrength);
            Assert.AreEqual(1920, status.Screen.Width);
            Assert.AreEqual(1080, status.Screen.Height);

            byte[] png = await pipeClient.ScreenshotAsync(CancellationToken.None).WaitAsync(CaseTimeout);
            CollectionAssert.AreEqual(OnePixelPng, png, "The PNG bytes must survive the pipe unchanged.");

            await pipeClient.MouseMoveAsync(10, 20, CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual(10, client.LastMouseMoveX);
            Assert.AreEqual(20, client.LastMouseMoveY);

            await pipeClient.MouseClickAsync(30, 40, MouseButton.Right, 2, CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual(30, client.LastMouseClickX);
            Assert.AreEqual(40, client.LastMouseClickY);
            Assert.AreEqual(MouseButton.Right, client.LastMouseClickButton);
            Assert.AreEqual(2, client.LastMouseClickCount);

            await pipeClient.MouseScrollAsync(50, 60, -3, 4, CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual(50, client.LastMouseScrollX);
            Assert.AreEqual(60, client.LastMouseScrollY);
            Assert.AreEqual(-3, client.LastMouseScrollDeltaX);
            Assert.AreEqual(4, client.LastMouseScrollDeltaY);

            await pipeClient.KeyTypeAsync("hello", CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.AreEqual("hello", client.LastTypedText);

            await pipeClient.KeyPressAsync(
                [SessionKey.Ctrl, SessionKey.Alt, SessionKey.Delete],
                CancellationToken.None).WaitAsync(CaseTimeout);
            Assert.HasCount(3, client.LastKeys);
            Assert.AreEqual(SessionKey.Ctrl, client.LastKeys[0]);
            Assert.AreEqual(SessionKey.Alt, client.LastKeys[1]);
            Assert.AreEqual(SessionKey.Delete, client.LastKeys[2]);
        }

        Assert.AreEqual(1, client.ScreenshotCallCount);
        Assert.AreEqual(1, client.MouseMoveCallCount);
        Assert.AreEqual(1, client.MouseClickCallCount);
        Assert.AreEqual(1, client.MouseScrollCallCount);
        Assert.AreEqual(1, client.KeyTypeCallCount);
        Assert.AreEqual(1, client.KeyPressCallCount);
    }

    [TestMethod]
    public async Task OutOfRangeCoordinateIsRejectedWithoutCallingTheAction()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionOwnerService service = CreateService(new SessionClientToolHandler(client));
        service.Start();

        await using (NamedPipeSessionClient pipeClient = new(service.PipeName))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => pipeClient.MouseMoveAsync(1920, 10, CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.CoordOutOfRange, error.Code);
            Assert.IsFalse(error.Retryable);
            StringAssert.Contains(error.Message, "1920");
        }

        Assert.AreEqual(0, client.MouseMoveCallCount, "An out-of-range coordinate must never reach the session.");
        Assert.AreEqual(1, client.StatusCallCount, "The coordinate check must use exactly one fresh status.");
    }

    [TestMethod]
    public async Task ClientFailureIsPassedThroughAsTheContractError()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId))
        {
            ActionFailure = new SessionOperationException(SessionErrorCode.AgentError, "window-gone", true),
        };
        SessionClientToolHandler handler = new(client);
        SessionOwnerService service = CreateService(handler);
        service.Start();

        await using (NamedPipeSessionClient pipeClient = new(service.PipeName))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => pipeClient.MouseMoveAsync(10, 20, CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.AgentError, error.Code);
            Assert.AreEqual("window-gone", error.Message);
            Assert.IsTrue(error.Retryable);
        }

        SessionToolResult direct = await handler.HandleAsync(
            SessionTool.MouseMove,
            Json("""{"x":10,"y":20}"""),
            CancellationToken.None).WaitAsync(CaseTimeout);

        Assert.IsFalse(direct.Succeeded);
        Assert.IsNotNull(direct.Error);
        Assert.AreEqual(SessionErrorCode.AgentError, direct.Error.Code);
        Assert.AreEqual("window-gone", direct.Error.Message);
        Assert.IsTrue(direct.Error.Retryable);
        Assert.AreEqual(SessionState.Ready, direct.Error.State);
        Assert.AreEqual(2, client.MouseMoveCallCount, "One pipe call plus one direct call must have reached the session.");
    }

    [TestMethod]
    public async Task ScreenshotLargerThanTheLimitIsRejected()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId))
        {
            ScreenshotBytes = new byte[ScreenshotByteLimit + 1],
        };
        SessionClientToolHandler handler = new(client);
        SessionOwnerService service = CreateService(handler);
        service.Start();

        await using (NamedPipeSessionClient pipeClient = new(service.PipeName))
        {
            SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
                () => pipeClient.ScreenshotAsync(CancellationToken.None).WaitAsync(CaseTimeout));

            Assert.AreEqual(SessionErrorCode.ScreenshotTooLarge, error.Code);
            Assert.IsFalse(error.Retryable);
            StringAssert.Contains(error.Message, ScreenshotByteLimit.ToString(CultureInfo.InvariantCulture));
        }

        SessionToolResult direct = await handler.HandleAsync(
            SessionTool.Screenshot,
            SessionToolPayloads.EmptyArguments(),
            CancellationToken.None).WaitAsync(CaseTimeout);

        Assert.IsFalse(direct.Succeeded);
        Assert.IsNotNull(direct.Error);
        Assert.AreEqual(SessionErrorCode.ScreenshotTooLarge, direct.Error.Code);
        Assert.AreEqual(SessionState.Ready, direct.Error.State);
        Assert.AreEqual(2, client.ScreenshotCallCount);
    }

    [TestMethod]
    public void SecondStartOnTheSameLockFailsWithLockHeld()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionOwnerService first = CreateService(new SessionClientToolHandler(client));
        first.Start();

        string secondSessionId = Guid.NewGuid().ToString("D");
        RecordingSessionClient secondClient = new(ReadyStatus(secondSessionId));
        SessionOwnerService second = CreateService(
            new SessionClientToolHandler(secondClient),
            _lockName,
            NewPipeName(),
            _store,
            secondSessionId);

        SessionOwnerException error = Assert.ThrowsExactly<SessionOwnerException>(() => second.Start());

        Assert.AreEqual(SessionErrorCode.LockHeld, error.Code);
        Assert.AreEqual(SessionState.Starting, first.CurrentState);

        SessionOwnerRecord? record = _store.Read();
        Assert.IsNotNull(record);
        Assert.AreEqual(_sessionId, record.SessionId, "The locked-out service must not overwrite the owner's record.");
        Assert.AreEqual(SessionState.Starting, record.State);
    }

    [TestMethod]
    public async Task DisposeRemovesTheRecordReleasesTheLockAndStopsThePipe()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionOwnerService service = CreateService(new SessionClientToolHandler(client));
        service.Start();
        string recordPath = _store.RecordPath;
        Assert.IsTrue(File.Exists(recordPath));

        await service.DisposeAsync();

        Assert.IsFalse(File.Exists(recordPath), "Disposing the owner must delete its ownership record.");
        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(_lockName), "Disposing the owner must release the lock object.");
        await service.DisposeAsync();

        using (SingleInstanceLock again = SingleInstanceLock.Acquire(_lockName))
        {
            Assert.IsTrue(again.IsHeld, "The released lock must be acquirable again.");
        }

        AssertPipeRefusesConnection(_pipeName);
    }

    [TestMethod]
    public void TransitionRewritesTheRecordAndRefusesIllegalStates()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionOwnerService service = CreateService(new SessionClientToolHandler(client));
        service.Start();

        service.Transition(SessionState.Connecting);
        Assert.AreEqual(SessionState.Connecting, service.CurrentState);
        Assert.AreEqual(SessionState.Connecting, _store.Read()!.State);

        service.Transition(SessionState.Ready);
        Assert.AreEqual(SessionState.Ready, service.CurrentState);
        Assert.AreEqual(SessionState.Ready, _store.Read()!.State);

        SessionOwnerException illegal = Assert.ThrowsExactly<SessionOwnerException>(
            () => service.Transition(SessionState.Connecting));

        Assert.AreEqual(SessionErrorCode.State, illegal.Code);
        Assert.AreEqual(SessionState.Ready, service.CurrentState);
        Assert.AreEqual(SessionState.Ready, _store.Read()!.State);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => service.Transition((SessionState)999));
        Assert.AreEqual(SessionState.Ready, service.CurrentState);
    }

    [TestMethod]
    public void TransitionBeforeStartIsRefused()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionOwnerService service = CreateService(new SessionClientToolHandler(client));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.Transition(SessionState.Connecting));
        Assert.AreEqual(SessionState.Starting, service.CurrentState);
    }

    [TestMethod]
    public void TransitionFailureLeavesTheInMemoryStateUnchanged()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionOwnerService service = CreateService(new SessionClientToolHandler(client));
        service.Start();
        service.Transition(SessionState.Connecting);
        service.Transition(SessionState.Ready);

        string recordDirectory = Path.GetDirectoryName(_store.RecordPath)
            ?? throw new InvalidOperationException("The ownership record path has no directory part.");
        Directory.Delete(recordDirectory, recursive: true);
        File.WriteAllText(recordDirectory, "not a directory");

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => service.Transition(SessionState.Disconnected));

        StringAssert.Contains(error.Message, "disconnected");
        Assert.AreEqual(SessionState.Ready, service.CurrentState, "A failed record rewrite must not change the in-memory state.");
    }

    [TestMethod]
    public void StartReleasesTheLockWhenTheRecordCannotBeWritten()
    {
        string blockedRoot = Path.Combine(_root, "blocked-root");
        File.WriteAllText(blockedRoot, "not a directory");
        SessionOwnerStore blockedStore = new(blockedRoot, TestSid);
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        string lockName = NewLockName();
        SessionOwnerService service = CreateService(
            new SessionClientToolHandler(client),
            lockName,
            NewPipeName(),
            blockedStore,
            _sessionId);

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => service.Start());

        StringAssert.Contains(error.Message, "ownership record");
        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(lockName), "A record write failure must release the lock.");
        Assert.IsFalse(File.Exists(blockedStore.RecordPath));

        using (SingleInstanceLock again = SingleInstanceLock.Acquire(lockName))
        {
            Assert.IsTrue(again.IsHeld, "The lock must be free after a record write failure.");
        }
    }

    [TestMethod]
    public void StartReleasesTheLockWhenThePipeCannotStart()
    {
        string invalidPipeName = "invalid\0pipe";
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        string lockName = NewLockName();
        SessionOwnerService service = CreateService(
            new SessionClientToolHandler(client),
            lockName,
            invalidPipeName,
            _store,
            _sessionId);

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => service.Start());

        StringAssert.Contains(error.Message, "pipe");
        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(lockName), "A pipe startup failure must release the lock.");
        Assert.IsFalse(File.Exists(_store.RecordPath), "A pipe startup failure must delete the starting record.");
    }

    [TestMethod]
    public async Task HandlerRejectsStrictlyInvalidActionArgumentsWithoutCallingTheAction()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionClientToolHandler handler = new(client);

        SessionToolResult unknownField = await handler.HandleAsync(
            SessionTool.MouseMove,
            Json("""{"x":1,"y":2,"z":3}"""),
            CancellationToken.None).WaitAsync(CaseTimeout);
        AssertInputInvalid(unknownField, SessionState.Ready);

        string tooLong = new('t', SessionToolArguments.MaximumTextLength + 1);
        SessionToolResult longText = await handler.HandleAsync(
            SessionTool.KeyType,
            JsonSerializer.SerializeToElement(new { text = tooLong }),
            CancellationToken.None).WaitAsync(CaseTimeout);
        AssertInputInvalid(longText, SessionState.Ready);

        string[] tooManyKeys = ["enter", "esc", "tab", "delete", "backspace", "home"];
        SessionToolResult keys = await handler.HandleAsync(
            SessionTool.KeyPress,
            JsonSerializer.SerializeToElement(new { keys = tooManyKeys }),
            CancellationToken.None).WaitAsync(CaseTimeout);
        AssertInputInvalid(keys, SessionState.Ready);

        SessionToolResult count = await handler.HandleAsync(
            SessionTool.MouseClick,
            Json("""{"x":1,"y":2,"button":"left","count":4}"""),
            CancellationToken.None).WaitAsync(CaseTimeout);
        AssertInputInvalid(count, SessionState.Ready);

        Assert.AreEqual(0, client.MouseMoveCallCount);
        Assert.AreEqual(0, client.MouseClickCallCount);
        Assert.AreEqual(0, client.KeyTypeCallCount);
        Assert.AreEqual(0, client.KeyPressCallCount);
    }

    [TestMethod]
    public async Task HandlerRejectsNegativeCoordinatesWithoutCallingTheAction()
    {
        RecordingSessionClient client = new(ReadyStatus(_sessionId));
        SessionClientToolHandler handler = new(client);

        SessionToolResult result = await handler.HandleAsync(
            SessionTool.MouseMove,
            Json("""{"x":-1,"y":0}"""),
            CancellationToken.None).WaitAsync(CaseTimeout);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(SessionErrorCode.CoordOutOfRange, result.Error.Code);
        Assert.AreEqual(SessionState.Ready, result.Error.State);
        Assert.AreEqual(0, client.MouseMoveCallCount);
    }

    private static void AssertInputInvalid(SessionToolResult result, SessionState expectedState)
    {
        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(SessionErrorCode.InputInvalid, result.Error.Code);
        Assert.AreEqual(expectedState, result.Error.State);
    }

    private static JsonElement Json(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static SessionStatus ReadyStatus(string sessionId)
    {
        return new SessionStatus(
            "ready",
            sessionId,
            AuthTier.SystemCredential,
            GateStrength.Login,
            new SessionScreen(1920, 1080),
            null);
    }

    private static string NewLockName()
    {
        return @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
    }

    private static string NewPipeName()
    {
        return "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N");
    }

    private SessionOwnerService CreateService(ISessionToolHandler handler)
    {
        return CreateService(handler, _lockName, _pipeName, _store, _sessionId);
    }

    private SessionOwnerService CreateService(
        ISessionToolHandler handler,
        string lockName,
        string pipeName,
        SessionOwnerStore store,
        string sessionId)
    {
        SessionOwnerOptions options = new(
            lockName,
            pipeName,
            store,
            handler,
            sessionId,
            Environment.ProcessId,
            HostStartTime,
            AcquiredAt);
        SessionOwnerService service = new(options);
        _services.Add(service);
        return service;
    }

    private static void AssertPipeRefusesConnection(string pipeName)
    {
        using NamedPipeClientStream probe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            probe.Connect(300);
            Assert.Fail("A disposed owner must not accept a new pipe connection.");
        }
        catch (TimeoutException)
        {
            // The expected shape: no listening instance remains.
        }
        catch (IOException)
        {
            // Windows also reports a vanished pipe as a file-not-found style I/O error.
        }
    }

    private sealed class RecordingSessionClient : ISessionClient
    {
        private readonly SessionStatus _status;
        private readonly object _gate = new();
        private int _statusCallCount;
        private int _screenshotCallCount;
        private int _mouseMoveCallCount;
        private int _mouseClickCallCount;
        private int _mouseScrollCallCount;
        private int _keyTypeCallCount;
        private int _keyPressCallCount;
        private byte[] _screenshotBytes = OnePixelPng;
        private IReadOnlyList<SessionKey> _lastKeys = [];

        internal RecordingSessionClient(SessionStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            _status = status;
        }

        internal int StatusCallCount => Volatile.Read(ref _statusCallCount);

        internal int ScreenshotCallCount => Volatile.Read(ref _screenshotCallCount);

        internal int MouseMoveCallCount => Volatile.Read(ref _mouseMoveCallCount);

        internal int MouseClickCallCount => Volatile.Read(ref _mouseClickCallCount);

        internal int MouseScrollCallCount => Volatile.Read(ref _mouseScrollCallCount);

        internal int KeyTypeCallCount => Volatile.Read(ref _keyTypeCallCount);

        internal int KeyPressCallCount => Volatile.Read(ref _keyPressCallCount);

        internal byte[] ScreenshotBytes
        {
            get
            {
                lock (_gate)
                {
                    return _screenshotBytes;
                }
            }

            set
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (_gate)
                {
                    _screenshotBytes = value;
                }
            }
        }

        internal Exception? ActionFailure { get; set; }

        internal int LastMouseMoveX { get; private set; }

        internal int LastMouseMoveY { get; private set; }

        internal int LastMouseClickX { get; private set; }

        internal int LastMouseClickY { get; private set; }

        internal MouseButton LastMouseClickButton { get; private set; }

        internal int LastMouseClickCount { get; private set; }

        internal int LastMouseScrollX { get; private set; }

        internal int LastMouseScrollY { get; private set; }

        internal int LastMouseScrollDeltaX { get; private set; }

        internal int LastMouseScrollDeltaY { get; private set; }

        internal string? LastTypedText { get; private set; }

        internal IReadOnlyList<SessionKey> LastKeys
        {
            get
            {
                lock (_gate)
                {
                    return _lastKeys;
                }
            }
        }

        public async Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _statusCallCount);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return _status;
        }

        public async Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _screenshotCallCount);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return ScreenshotBytes;
        }

        public async Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _mouseMoveCallCount);
            LastMouseMoveX = x;
            LastMouseMoveY = y;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            ThrowActionFailure();
        }

        public async Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _mouseClickCallCount);
            LastMouseClickX = x;
            LastMouseClickY = y;
            LastMouseClickButton = button;
            LastMouseClickCount = count;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            ThrowActionFailure();
        }

        public async Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _mouseScrollCallCount);
            LastMouseScrollX = x;
            LastMouseScrollY = y;
            LastMouseScrollDeltaX = deltaX;
            LastMouseScrollDeltaY = deltaY;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            ThrowActionFailure();
        }

        public async Task KeyTypeAsync(string text, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _keyTypeCallCount);
            LastTypedText = text;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            ThrowActionFailure();
        }

        public async Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _keyPressCallCount);
            lock (_gate)
            {
                _lastKeys = [.. keys];
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            ThrowActionFailure();
        }

        private void ThrowActionFailure()
        {
            Exception? failure = ActionFailure;
            if (failure is not null)
            {
                throw failure;
            }
        }
    }
}