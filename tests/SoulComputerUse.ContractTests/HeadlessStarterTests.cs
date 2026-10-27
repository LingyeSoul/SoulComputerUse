using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Headless;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The headless starter's option contract and its refusals: a start that cannot be honoured must take
/// no lock and write no ownership record, and a session it does produce must release both.
/// </summary>
/// <remarks>
/// Every case uses a per-run lock name, a per-run pipe name and a store in a temporary directory, so no
/// case can touch the contract-pinned production lock, the production owner pipe or a real session.
/// The case that needs a held lock takes it through the named internal entry point, which is the same
/// path the integration suite uses.
/// </remarks>
[TestClass]
public sealed class HeadlessStarterTests
{
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";
    private const string LockHeldGuidance = "Task Manager";
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(30);
    private string _root = string.Empty;
    private string _lockName = string.Empty;
    private string _pipeName = string.Empty;
    private string _sessionId = string.Empty;
    private SessionOwnerStore _store = null!;
    private HeadlessOwnerSession? _started;

    [TestInitialize]
    public void CreateIsolatedState()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-headless-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
        _pipeName = "SoulComputerUse.owner.test-" + Guid.NewGuid().ToString("N");
        _sessionId = Guid.NewGuid().ToString("D");
        _store = new SessionOwnerStore(_root, TestSid);
    }

    [TestCleanup]
    public async Task DeleteIsolatedState()
    {
        if (_started is not null)
        {
            await _started.DisposeAsync();
            _started = null;
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void ASessionIdThatIsNotADashSeparatedUuidIsRefused()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => new HeadlessStarterOptions(
                _lockName,
                _pipeName,
                "example-session",
                _store,
                static client => new SessionClientToolHandler(client),
                static () => new UnavailableSessionClient()));

        Assert.AreEqual("sessionId", error.ParamName);
        StringAssert.Contains(error.Message, "D-format UUID");
    }

    [TestMethod]
    public void AnEmptyLockNameIsRefused()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => new HeadlessStarterOptions(
                " ",
                _pipeName,
                _sessionId,
                _store,
                static client => new SessionClientToolHandler(client),
                static () => new UnavailableSessionClient()));

        Assert.AreEqual("lockName", error.ParamName);
    }

    [TestMethod]
    public void AnEmptyPipeNameIsRefused()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => new HeadlessStarterOptions(
                _lockName,
                string.Empty,
                _sessionId,
                _store,
                static client => new SessionClientToolHandler(client),
                static () => new UnavailableSessionClient()));

        Assert.AreEqual("pipeName", error.ParamName);
    }

    [TestMethod]
    public void AMissingHandlerFactoryIsRefused()
    {
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(
            () => new HeadlessStarterOptions(
                _lockName,
                _pipeName,
                _sessionId,
                _store,
                null!,
                static () => new UnavailableSessionClient()));

        Assert.AreEqual("handlerFactory", error.ParamName);
    }

    [TestMethod]
    public void AMissingClientFactoryIsRefused()
    {
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(
            () => new HeadlessStarterOptions(
                _lockName,
                _pipeName,
                _sessionId,
                _store,
                static client => new SessionClientToolHandler(client),
                null!));

        Assert.AreEqual("clientFactory", error.ParamName);
    }

    [TestMethod]
    public async Task AnUnavailableBackendFailsWithNotReadyWithoutTakingTheLockOrWritingARecord()
    {
        HeadlessOwnerStarter starter = CreateStarter(static () => new UnavailableSessionClient());

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout));

        Assert.AreEqual(SessionErrorCode.NotReady, error.Code);
        Assert.IsTrue(error.Retryable, "The contract marks E_NOT_READY as retryable.");
        StringAssert.Contains(error.Message, "Host", "The message must name the only way out in this build.");
        AssertNoMachineData(error.Message);
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "A refused backend must leave the lock free, because no owner was started.");
        Assert.IsFalse(
            File.Exists(_store.RecordPath),
            "A refused backend must write no ownership record, because no owner was started.");
    }

    [TestMethod]
    public async Task AStartingOwnerSeenByTheProbeFailsWithNotReadyAndBuildsNoBackend()
    {
        RecordingSessionClient backend = new();
        HeadlessOwnerStarter starter = new(
            CreateOptions(() =>
            {
                backend.StartCount++;
                return backend;
            }),
            new StubOwnerProbe(lockExists: true, ownerPipeReachable: false, liveOwnerStarting: true));

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout));

        Assert.AreEqual(SessionErrorCode.NotReady, error.Code);
        Assert.IsTrue(error.Retryable, "A session owner that is still starting must be retried, not treated as a held lock.");
        StringAssert.Contains(error.Message, "starting");
        AssertNoMachineData(error.Message);
        Assert.AreEqual(0, backend.StartCount, "A starting owner must not lead to a second owner in this process.");
        Assert.IsFalse(
            File.Exists(_store.RecordPath),
            "A starting owner must not produce a record, because this process never became the owner.");
    }

    [TestMethod]
    public async Task AHeldLockSeenByTheProbeFailsWithLockHeldAndBuildsNoBackend()
    {
        using SingleInstanceLock held = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(held.IsHeld, "The case needs a live lock holder for its premise to hold.");

        RecordingSessionClient backend = new();
        HeadlessOwnerStarter starter = new(
            CreateOptions(() =>
            {
                backend.StartCount++;
                return backend;
            }),
            new OwnerProbe(_lockName, _pipeName, _store));

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout));

        Assert.AreEqual(SessionErrorCode.LockHeld, error.Code);
        Assert.IsFalse(error.Retryable, "The contract marks a held lock as not retryable.");
        StringAssert.Contains(error.Message, LockHeldGuidance);
        AssertNoMachineData(error.Message);
        Assert.AreEqual(0, backend.StartCount, "A lock the probe already sees as held must not build a backend.");
        Assert.IsFalse(
            File.Exists(_store.RecordPath),
            "A held lock must not produce a record, because this process never became the owner.");
    }

    [TestMethod]
    public async Task ALockTakenAfterTheProbeFailsWithLockHeldAndReleasesTheBackend()
    {
        // The probe reports a free machine, so the start goes ahead and the race is decided by the real
        // owner start: the lock is genuinely held here, which is the shape of two processes starting at once.
        using SingleInstanceLock held = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(held.IsHeld, "The case needs a live lock holder for its premise to hold.");

        RecordingSessionClient backend = new();
        HeadlessOwnerStarter starter = new(
            CreateOptions(() => backend),
            new FreeMachineProbe());

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout));

        Assert.AreEqual(SessionErrorCode.LockHeld, error.Code);
        Assert.IsFalse(error.Retryable);
        Assert.AreEqual(1, backend.DisposeCount, "The backend built for a start that lost the race must be closed.");
        Assert.IsFalse(
            File.Exists(_store.RecordPath),
            "A start that lost the race must leave the winner's record untouched, and this case left no winner.");
    }

    [TestMethod]
    public async Task AnAnsweringPipeIsAttachedInsteadOfStartingASecondSession()
    {
        RecordingSessionClient backend = new();
        HeadlessOwnerStarter starter = new(
            new HeadlessStarterOptions(
                _lockName,
                _pipeName,
                _sessionId,
                _store,
                static client => new SessionClientToolHandler(client),
                () =>
                {
                    backend.StartCount++;
                    return backend;
                }),
            new StubOwnerProbe(lockExists: false, ownerPipeReachable: true, liveOwnerStarting: false));

        ISessionClient client = await starter.StartAsync(CancellationToken.None).WaitAsync(CaseTimeout);

        Assert.IsInstanceOfType<NamedPipeSessionClient>(
            client,
            "The winning owner is reached through the owner pipe, never through the backend this process would have owned.");
        Assert.AreEqual(0, backend.StartCount, "An answering pipe means this process must build no backend at all.");
        Assert.IsFalse(
            File.Exists(_store.RecordPath),
            "The losing side of a race must not write an ownership record.");

        await ((IAsyncDisposable)client).DisposeAsync().AsTask().WaitAsync(CaseTimeout);
    }

    [TestMethod]
    public async Task DisposingAStartedSessionIsIdempotentAndReleasesTheLock()
    {
        RecordingSessionClient backend = new();
        HeadlessOwnerStarter starter = CreateStarter(() => backend);

        HeadlessOwnerSession session = (HeadlessOwnerSession)await starter
            .StartAsync(CancellationToken.None)
            .WaitAsync(CaseTimeout);
        _started = session;

        Assert.IsTrue(File.Exists(_store.RecordPath), "A started owner must own an ownership record.");
        Assert.IsTrue(SingleInstanceLock.IsHeldByAnyone(_lockName), "A started owner must hold the lock.");

        await session.DisposeAsync().AsTask().WaitAsync(CaseTimeout);
        await session.DisposeAsync().AsTask().WaitAsync(CaseTimeout);

        Assert.AreEqual(1, backend.DisposeCount, "The backend must be closed once, however often the session is disposed.");
        Assert.IsFalse(File.Exists(_store.RecordPath), "Disposing the session must delete the ownership record.");
        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(_lockName), "Disposing the session must release the lock.");

        using SingleInstanceLock again = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(again.IsHeld, "The lock must be acquirable again after the session was released.");
    }

    [TestMethod]
    [DoNotParallelize]
    public void OnlyTheExactGateValueAllowsAHeadlessOwner()
    {
        string? original = Environment.GetEnvironmentVariable(HeadlessStarterActivation.AllowHeadlessVariable);
        try
        {
            Environment.SetEnvironmentVariable(HeadlessStarterActivation.AllowHeadlessVariable, "1");
            Assert.IsTrue(HeadlessStarterActivation.IsAllowed(), "The documented value must open the gate.");

            foreach (string other in new[] { "true", "0", "yes", " 1 ", string.Empty })
            {
                Environment.SetEnvironmentVariable(HeadlessStarterActivation.AllowHeadlessVariable, other);
                Assert.IsFalse(
                    HeadlessStarterActivation.IsAllowed(),
                    $"Only '{HeadlessStarterActivation.AllowHeadlessValue}' may open the gate, so '{other}' must not.");
            }

            Environment.SetEnvironmentVariable(HeadlessStarterActivation.AllowHeadlessVariable, null);
            Assert.IsFalse(HeadlessStarterActivation.IsAllowed(), "An absent variable must keep the default path.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HeadlessStarterActivation.AllowHeadlessVariable, original);
        }
    }

    private static void AssertNoMachineData(string message)
    {
        Assert.DoesNotContain("\\", message, "An error message must not carry a path.");
        Assert.DoesNotContain("/", message, "An error message must not carry a path.");
        Assert.DoesNotContain("S-1-", message, "An error message must not carry an account identifier.");
    }

    private HeadlessOwnerStarter CreateStarter(Func<ISessionClient> clientFactory)
    {
        return new HeadlessOwnerStarter(
            CreateOptions(clientFactory),
            new StubOwnerProbe(lockExists: false, ownerPipeReachable: false, liveOwnerStarting: false));
    }

    private HeadlessStarterOptions CreateOptions(Func<ISessionClient> clientFactory)
    {
        return new HeadlessStarterOptions(
            _lockName,
            _pipeName,
            _sessionId,
            _store,
            static client => new SessionClientToolHandler(client),
            clientFactory);
    }

    private sealed class FreeMachineProbe : IOwnerProbe
    {
        public bool LockExists()
        {
            return false;
        }

        public Task<bool> IsOwnerPipeReachableAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public bool IsLiveOwnerStarting()
        {
            return false;
        }
    }

    private sealed class StubOwnerProbe : IOwnerProbe
    {
        private readonly bool _lockExists;
        private readonly bool _ownerPipeReachable;
        private readonly bool _liveOwnerStarting;

        internal StubOwnerProbe(bool lockExists, bool ownerPipeReachable, bool liveOwnerStarting)
        {
            _lockExists = lockExists;
            _ownerPipeReachable = ownerPipeReachable;
            _liveOwnerStarting = liveOwnerStarting;
        }

        public bool LockExists()
        {
            return _lockExists;
        }

        public Task<bool> IsOwnerPipeReachableAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_ownerPipeReachable);
        }

        public bool IsLiveOwnerStarting()
        {
            return _liveOwnerStarting;
        }
    }

    private sealed class RecordingSessionClient : ISessionClient, IAsyncDisposable
    {
        private readonly SessionStatus _status = new(
            "ready",
            Guid.NewGuid().ToString("D"),
            AuthTier.SystemCredential,
            GateStrength.Login,
            new SessionScreen(1920, 1080),
            null);

        internal int StartCount { get; set; }

        internal int DisposeCount { get; private set; }

        public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_status);
        }

        public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
