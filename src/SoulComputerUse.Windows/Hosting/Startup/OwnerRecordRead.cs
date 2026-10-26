using System;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>The result of reading this account's ownership record.</summary>
public sealed record OwnerRecordRead
{
    private OwnerRecordRead(OwnerRecordStatus status, SessionOwnerRecord? record)
    {
        Status = status;
        Record = record;
    }

    /// <summary>What the record means for a new start.</summary>
    public OwnerRecordStatus Status { get; }

    /// <summary>The parsed record when one was read and may be taken over; null otherwise.</summary>
    public SessionOwnerRecord? Record { get; }

    /// <summary>No record exists for this account, which the contract treats as a normal start.</summary>
    public static OwnerRecordRead Absent()
    {
        return new OwnerRecordRead(OwnerRecordStatus.Absent, null);
    }

    /// <summary>The record meets the takeover criteria and may be replaced.</summary>
    /// <exception cref="ArgumentNullException">The record is null.</exception>
    public static OwnerRecordRead TakeOver(SessionOwnerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new OwnerRecordRead(OwnerRecordStatus.TakeOver, record);
    }

    /// <summary>
    /// The record cannot be parsed, has unusable fields, or fails the takeover criteria. It stays on
    /// disk because it is the only evidence of what the previous owner left behind.
    /// </summary>
    public static OwnerRecordRead NotTakeable()
    {
        return new OwnerRecordRead(OwnerRecordStatus.NotTakeable, null);
    }
}
