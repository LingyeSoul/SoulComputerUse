using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Headless;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The headless starter against real machine state: a real single-instance lock object, a real owner
/// pipe and a real ownership record, with a fake session client standing in for the backend this
/// build does not have.
/// </summary>
/// <remarks>
/// Every case uses a per-run lock name of the form <c>Global\SoulComputerUse-Test-{guid}</c>, a
/// per-run pipe name of the form <c>SoulComputerUse.owner.test-{guid}</c> and a store rooted in a
/// temporary directory, so the contract-pinned production lock, the production owner pipe and any real
/// session are never touched. No case starts a process or a remote desktop connection.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class HeadlessStarterIntegrationTests
{
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";
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

    private string _root = string.Empty;
    private string _lockName = string.Empty;
    private string _pipeName = string.Empty;
    private string _sessionId = string.Empty;
    private SessionOwnerStore _store = null!;
    private HeadlessOwnerSession? _started;

    [TestInitialize]
    public void CreateIsolatedState()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-headless-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
        _pipeName = "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N");
        _sessionId = Guid.NewGuid().ToString("D");
        _store = new SessionOwnerStore(_root, TestSid);
    }

    [TestCleanup]
    public async Task AssertNothingIsLeftBehind()
    {
        if (_started is not null)
        {
            await _started.DisposeAsync();
            _started = null;
        }

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The case's own lock object must be gone afterwards, otherwise the case leaked an owner.");
        Assert.IsFalse(
            File.Exists(_store.RecordPath),
            "The case's own ownership record must be gone afterwards.");
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AStartedHeadlessOwnerAnswersStatusAndScreenshotThroughThePipe()
    {
        RecordingSessionClient backend = new(_sessionId);
        HeadlessOwnerStarter starter = CreateStarter(() => backend);

        _started = (HeadlessOwnerSession)await starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        Assert.IsTrue(SingleInstanceLock.IsHeldByAnyone(_lockName), "A started headless owner must hold the lock.");
        Assert.IsTrue(File.Exists(_store.RecordPath), "A started headless owner must write its ownership record.");

        SessionOwnerRecord? record = _store.Read();
        Assert.IsNotNull(record, "A started headless owner must write its ownership record.");
        Assert.AreEqual(_sessionId, record.SessionId, "The record must name the session this owner serves.");

        await using (NamedPipeSessionClient pipeClient = new(_pipeName))
        {
            SessionStatus piped = await pipeClient.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);

            Assert.AreEqual(
                _sessionId,
                piped.SessionId,
                "A plain owner pipe client must reach the fake backend, exactly as it would reach an existing owner.");
        }

        SessionStatus status = await _started.GetStatusAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        Assert.AreEqual("ready", status.State);
        Assert.AreEqual(_sessionId, status.SessionId, "The status must be the fake backend's, routed through the pipe.");
        Assert.AreEqual(1920, status.Screen.Width);
        Assert.AreEqual(1080, status.Screen.Height);

        byte[] png = await _started.ScreenshotAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        CollectionAssert.AreEqual(OnePixelPng, png, "The screenshot bytes must survive the owner pipe unchanged.");
        Assert.AreEqual(2, backend.StatusCallCount, "The two status calls must both have been served by the backend.");
        Assert.AreEqual(1, backend.ScreenshotCallCount);
    }

    [TestMethod]
    public async Task AStartedHeadlessOwnerForwardsEveryActionToTheBackend()
    {
        RecordingSessionClient backend = new(_sessionId);
        HeadlessOwnerStarter starter = CreateStarter(() => backend);

        _started = (HeadlessOwnerSession)await starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        await _started.MouseMoveAsync(10, 20, CancellationToken.None).WaitAsync(CaseTimeout);
        Assert.AreEqual(10, backend.LastMouseMoveX);
        Assert.AreEqual(20, backend.LastMouseMoveY);

        await _started.MouseClickAsync(30, 40, MouseButton.Right, 2, CancellationToken.None).WaitAsync(CaseTimeout);
        Assert.AreEqual(30, backend.LastMouseClickX);
        Assert.AreEqual(40, backend.LastMouseClickY);
        Assert.AreEqual(MouseButton.Right, backend.LastMouseClickButton);
        Assert.AreEqual(2, backend.LastMouseClickCount);

        await _started.MouseScrollAsync(50, 60, -3, 4, CancellationToken.None).WaitAsync(CaseTimeout);
        Assert.AreEqual(50, backend.LastMouseScrollX);
        Assert.AreEqual(60, backend.LastMouseScrollY);
        Assert.AreEqual(-3, backend.LastMouseScrollDeltaX);
        Assert.AreEqual(4, backend.LastMouseScrollDeltaY);

        await _started.KeyTypeAsync("hello", CancellationToken.None).WaitAsync(CaseTimeout);
        Assert.AreEqual("hello", backend.LastTypedText);

        await _started.KeyPressAsync(
            [SessionKey.Ctrl, SessionKey.Alt, SessionKey.Delete],
            CancellationToken.None).WaitAsync(CaseTimeout);

        Assert.HasCount(3, backend.LastKeys);
        Assert.AreEqual(SessionKey.Ctrl, backend.LastKeys[0]);
        Assert.AreEqual(SessionKey.Alt, backend.LastKeys[1]);
        Assert.AreEqual(SessionKey.Delete, backend.LastKeys[2]);
    }

    [TestMethod]
    public async Task DisposingTheHeadlessSessionDeletesTheRecordReleasesTheLockAndStopsThePipe()
    {
        RecordingSessionClient backend = new(_sessionId);
        HeadlessOwnerStarter starter = CreateStarter(() => backend);

        HeadlessOwnerSession session = (HeadlessOwnerSession)await starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout);
        _started = session;

        await session.DisposeAsync().AsTask().WaitAsync(CaseTimeout);

        Assert.AreEqual(1, backend.DisposeCount, "The backend must be closed when the session is released.");
        Assert.IsFalse(File.Exists(_store.RecordPath), "Disposing the session must delete the ownership record.");
        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(_lockName), "Disposing the session must release the lock.");
        AssertPipeRefusesConnection(_pipeName);

        using SingleInstanceLock again = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(again.IsHeld, "The released lock must be acquirable again.");
    }

    /// <summary>
    /// The first owner really holds the lock and its record names a live process that is still in the
    /// starting state, so a second start on a silent pipe must be refused with the retryable
    /// <c>E_NOT_READY</c> instead of starting a second owner or touching the running owner's record.
    /// </summary>
    [TestMethod]
    public async Task ASecondStartOnAHeldLockOwnedByALiveStartingProcessFailsWithNotReadyInsteadOfStartingASecondOwner()
    {
        RecordingSessionClient backend = new(_sessionId);
        HeadlessOwnerStarter starter = CreateStarter(() => backend);
        _started = (HeadlessOwnerSession)await starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        string secondPipeName = "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N");
        HeadlessOwnerStarter second = new(
            new HeadlessStarterOptions(
                _lockName,
                secondPipeName,
                Guid.NewGuid().ToString("D"),
                _store,
                static client => new SessionClientToolHandler(client),
                () => new RecordingSessionClient(_sessionId)),
            new OwnerProbe(_lockName, secondPipeName, _store));

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => second.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout));

        Assert.AreEqual(
            SessionErrorCode.NotReady,
            error.Code,
            "A held lock whose per-run record names the live first owner in its starting state is the retryable case, not a stale lock.");
        Assert.IsTrue(error.Retryable);
        Assert.AreEqual(
            OwnerSessionResolver.StartInProgressMessage,
            error.Message,
            "The starting-owner refusal must use the one shared fixed template.");
        SessionOwnerRecord? record = _store.Read();
        Assert.IsNotNull(record);
        Assert.AreEqual(
            _sessionId,
            record.SessionId,
            "The refused second start must not overwrite the running owner's record.");
    }

    private HeadlessOwnerStarter CreateStarter(Func<ISessionClient> clientFactory)
    {
        return new HeadlessOwnerStarter(
            new HeadlessStarterOptions(
                _lockName,
                _pipeName,
                _sessionId,
                _store,
                static client => new SessionClientToolHandler(client),
                clientFactory),
            new OwnerProbe(_lockName, _pipeName, _store));
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
            Assert.Fail("A disposed headless owner must not accept a new pipe connection.");
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

    private sealed class RecordingSessionClient : ISessionClient, IAsyncDisposable
    {
        private const int ScreenWidth = 1920;
        private const int ScreenHeight = 1080;

        private readonly SessionStatus _status;
        private readonly object _gate = new();
        private int _statusCallCount;
        private int _screenshotCallCount;
        private IReadOnlyList<SessionKey> _lastKeys = [];

        internal RecordingSessionClient(string sessionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            _status = new SessionStatus(
                "ready",
                sessionId,
                AuthTier.SystemCredential,
                GateStrength.Login,
                new SessionScreen(ScreenWidth, ScreenHeight),
                null);
        }

        internal int StatusCallCount => Volatile.Read(ref _statusCallCount);

        internal int ScreenshotCallCount => Volatile.Read(ref _screenshotCallCount);

        internal int DisposeCount { get; private set; }

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
            return OnePixelPng;
        }

        public async Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
        {
            LastMouseMoveX = x;
            LastMouseMoveY = y;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
        {
            LastMouseClickX = x;
            LastMouseClickY = y;
            LastMouseClickButton = button;
            LastMouseClickCount = count;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            LastMouseScrollX = x;
            LastMouseScrollY = y;
            LastMouseScrollDeltaX = deltaX;
            LastMouseScrollDeltaY = deltaY;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task KeyTypeAsync(string text, CancellationToken cancellationToken)
        {
            LastTypedText = text;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _lastKeys = [.. keys];
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
