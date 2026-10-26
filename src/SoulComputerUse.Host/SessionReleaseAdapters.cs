using System;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Host;

/// <summary>
/// Writes the <c>closing</c> state into this session's ownership record as the teardown executor's first
/// step.
/// </summary>
/// <remarks>
/// The new-session mode has no owner service: the split segments write the record through their own port.
/// This adapter therefore writes the same record shape those segments wrote, carrying the values the ready
/// start reported, so the teardown sequence starts from the record that is actually on disk.
/// </remarks>
internal sealed class ReportedSessionStateTransition : ISessionStateTransition
{
    private readonly IOwnerRecordStore _recordStore;
    private readonly SessionStartOptions _options;
    private readonly int _subagentWtsSessionId;
    private readonly bool _childSessionsOpenedByUs;

    internal ReportedSessionStateTransition(
        IOwnerRecordStore recordStore,
        SessionStartOptions options,
        int subagentWtsSessionId,
        bool childSessionsOpenedByUs)
    {
        ArgumentNullException.ThrowIfNull(recordStore);
        ArgumentNullException.ThrowIfNull(options);

        _recordStore = recordStore;
        _options = options;
        _subagentWtsSessionId = subagentWtsSessionId;
        _childSessionsOpenedByUs = childSessionsOpenedByUs;
    }

    /// <inheritdoc />
    public void EnterClosing()
    {
        _recordStore.Write(new SessionOwnerRecord(
            _options.SessionId,
            _options.HostPid,
            _options.HostProcessStartTime,
            _subagentWtsSessionId,
            _childSessionsOpenedByUs,
            SessionState.Closing,
            _options.AcquiredAt));
    }
}

/// <summary>
/// The teardown layer's lock-release port for a launched session: the owner pipe stops first, then the
/// record goes, then the handle closes and the release is confirmed.
/// </summary>
/// <remarks>
/// This mirrors the owner service's disposal order. A session that stopped accepting calls before its record
/// disappeared can never be reached by a client that read a stale record, and a lock that is still held
/// after the handle closed is reported as a failed step rather than as a clean shutdown.
/// </remarks>
internal sealed class OwnerAndLockRelease : ISingleInstanceLockRelease
{
    private readonly IOwnerRecordStore _recordStore;
    private readonly IAsyncDisposable? _ownerPipe;
    private readonly IDisposable? _lockHandle;

    internal OwnerAndLockRelease(
        IOwnerRecordStore recordStore,
        IAsyncDisposable? ownerPipe,
        IDisposable? lockHandle)
    {
        ArgumentNullException.ThrowIfNull(recordStore);
        _recordStore = recordStore;
        _ownerPipe = ownerPipe;
        _lockHandle = lockHandle;
    }

    /// <inheritdoc />
    public void Release()
    {
        if (_ownerPipe is not null)
        {
            _ownerPipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _recordStore.Delete();
        _lockHandle?.Dispose();
    }
}