using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The ownership record of this account, as the startup pipeline needs it: it reads the file, classifies
/// the leftover with the contract's takeover rules, writes the new record and deletes it on failure.
/// </summary>
/// <remarks>
/// The takeover judgement lives here so no caller has to re-derive it: a record is
/// <see cref="OwnerRecordStatus.TakeOver"/> only when the contract's four criteria hold, which is the
/// owner being gone plus the record being terminal or more than twenty-four hours old. Anything else that
/// exists is <see cref="OwnerRecordStatus.NotTakeable"/> and stays on disk for a human.
/// <para>
/// The clock is read once per classification, so the twenty-four hour rule cannot mix two readings. A
/// record that cannot be parsed is classified, never thrown: the contract makes that the refusal case.
/// </para>
/// </remarks>
public sealed class OwnerRecordStoreAdapter : IOwnerRecordStore
{
    private readonly SessionOwnerStore _store;

    /// <summary>Creates the adapter over this account's record file.</summary>
    /// <exception cref="ArgumentNullException">The store is null.</exception>
    public OwnerRecordStoreAdapter(SessionOwnerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Creates the adapter over the record file of the account running this process.</summary>
    public static OwnerRecordStoreAdapter ForCurrentUser()
    {
        return new OwnerRecordStoreAdapter(SessionOwnerStore.ForCurrentUser());
    }

    /// <inheritdoc />
    public OwnerRecordRead Read()
    {
        SessionOwnerRecord? record;
        try
        {
            record = _store.Read();
        }
        catch (InvalidOperationException)
        {
            // A record that cannot be parsed or has unusable fields is the contract's refusal case, so it
            // is classified here instead of surfacing as a fault of the startup pipeline.
            return OwnerRecordRead.NotTakeable();
        }

        if (record is null)
        {
            return OwnerRecordRead.Absent();
        }

        RecoveryEvidence evidence = new(
            RecordExists: true,
            OwnerProcessAlive: OwnerProcessLiveness.IsAlive(record.HostPid),
            OwnerProcessStartTimeMatches: OwnerProcessLiveness.DoesStartTimeMatch(record.HostPid, record.HostProcessStartTime),
            RecordedState: record.State,
            AcquiredAt: record.AcquiredAt,
            Now: DateTimeOffset.UtcNow);

        return SessionRecoveryPolicy.Evaluate(evidence) == RecoveryDecision.RecoverLeftovers
            ? OwnerRecordRead.TakeOver(record)
            : OwnerRecordRead.NotTakeable();
    }

    /// <inheritdoc />
    public void Write(SessionOwnerRecord record)
    {
        _store.Write(record);
    }

    /// <inheritdoc />
    public void Delete()
    {
        _store.Delete();
    }
}
