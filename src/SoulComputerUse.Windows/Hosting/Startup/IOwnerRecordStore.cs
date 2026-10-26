using System;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The ownership record operations the startup path needs: judge the leftover, write the new record as
/// the session progresses, and delete it when the start fails.
/// </summary>
/// <remarks>
/// <see cref="Read"/> classifies an unreadable or unusable record as
/// <see cref="OwnerRecordStatus.NotTakeable"/> instead of throwing, because the contract makes that the
/// refusal case rather than an exception. A thrown exception therefore means the read could not even be
/// classified, and the runner reports it as an unusable record instead of treating it as a clean start.
/// </remarks>
public interface IOwnerRecordStore
{
    /// <summary>Reads and classifies this account's ownership record.</summary>
    OwnerRecordRead Read();

    /// <summary>Writes the record atomically. A failure must surface; the caller then releases the lock.</summary>
    void Write(SessionOwnerRecord record);

    /// <summary>Deletes the record. Idempotent, so cleanup may call it unconditionally.</summary>
    void Delete();
}
