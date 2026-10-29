using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.SingleInstance;

/// <summary>
/// Reads and writes the per-user ownership record under the local application data folder,
/// named after the user SID.
/// </summary>
/// <remarks>
/// Machine-wide uniqueness comes from the mutex, not from this file. The record only feeds
/// diagnostics and the abandoned-lock recovery rules, which is why it is split per user SID: one
/// account can never read or block another account's record.
/// Error messages name the record by type only: a real SID, file name or path must never end up in
/// a message that could reach a console, a screenshot or an issue.
/// <para>
/// The state fence lives here because this file is the only way an ownership record reaches the disk:
/// the startup segments, the owner service, the teardown adapters and the runtime-state observer all
/// write through this type. The single-instance mutex guarantees that only one process owns the record,
/// so a write is the last trusted decision this process takes about it, and this is the only place where
/// a session that has already been released can be stopped from bringing its record back as
/// <c>ready</c>.
/// </para>
/// <para>
/// Two rules carry that fence. A record whose state is <c>closing</c> is terminal: a write while the
/// session is closing is refused unless it is another <c>closing</c> write or the <c>starting</c> write
/// of a takeover, which the recovery rules ask for and which only happens while the machine-wide lock is
/// held, and once this store declared <c>closing</c> it never writes any other state again. A store that
/// wrote the record it then deletes, or that declared <c>closing</c> before deleting it, is spent, so a
/// late write throws instead of recreating the file; only a delete that follows a <see cref="Read"/>
/// without an intervening write stays usable, because that is the takeover of a previous owner's leftover
/// and it is followed by the replacement record.
/// </para>
/// </remarks>
public sealed class SessionOwnerStore
{
    /// <summary>The directory name used under the local application data folder.</summary>
    public const string DirectoryName = "SoulComputerUse";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _recordPath;
    private bool _closingDeclared;
    private bool _ownsRecord;
    private bool _released;

    /// <summary>Creates a store rooted at a specific local application data folder and user SID.</summary>
    public SessionOwnerStore(string localAppDataRoot, string userSid)
    {
        if (string.IsNullOrEmpty(localAppDataRoot))
        {
            throw new ArgumentException("The local application data root must not be empty.", nameof(localAppDataRoot));
        }

        if (string.IsNullOrEmpty(userSid))
        {
            throw new ArgumentException("The user SID must not be empty.", nameof(userSid));
        }

        _recordPath = Path.Combine(localAppDataRoot, DirectoryName, $"owner-{userSid}.json");
    }

    /// <summary>Full path of this user's ownership record.</summary>
    public string RecordPath => _recordPath;

    /// <summary>Creates the store for the account that is running the process.</summary>
    public static SessionOwnerStore ForCurrentUser()
    {
        string localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppDataRoot))
        {
            throw new InvalidOperationException("The local application data folder could not be resolved for this account.");
        }

        return new SessionOwnerStore(localAppDataRoot, GetCurrentUserSid());
    }

    /// <summary>
    /// Writes the record. A failure here must be turned into releasing the lock by the caller.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This store already released its record, this store or the record on disk already declared
    /// <c>closing</c> and <paramref name="record"/> does not, or the record on disk cannot be parsed or has
    /// unusable values.
    /// </exception>
    public void Write(SessionOwnerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        bool declaresClosing = record.State == SessionState.Closing;

        lock (_sync)
        {
            if (_released)
            {
                throw new InvalidOperationException(
                    "The ownership record was already released by this store, so it cannot be written again; a released session must not recreate its record.");
            }

            if (declaresClosing)
            {
                // Closing is terminal. The declaration is recorded even when the write itself fails, so a
                // failed closing write can never be followed by a late ready write through this store.
                _closingDeclared = true;
            }
            else if (_closingDeclared)
            {
                throw new InvalidOperationException(
                    "This store already declared the session closing, so it cannot write '"
                    + SessionStateNames.ToWireName(record.State)
                    + "' after it.");
            }

            // A 'starting' write is the one state that may replace a terminal record: it is only ever
            // written after the machine-wide single-instance lock was acquired, so it is the takeover of a
            // dead owner's leftover, which the recovery rules ask for. 'ready', 'blocked' and 'disconnected'
            // over 'closing' stay refused, because a session that is closing must not come back as serving.
            bool isTakeover = record.State == SessionState.Starting;
            SessionOwnerRecord? existing = ReadRecordUnderLock();
            if (existing is not null
                && existing.State == SessionState.Closing
                && !declaresClosing
                && !isTakeover)
            {
                throw new InvalidOperationException(
                    "The ownership record on disk is 'closing', which is terminal, so it cannot be replaced by '"
                    + SessionStateNames.ToWireName(record.State)
                    + "'.");
            }

            string directory = Path.GetDirectoryName(_recordPath)
                ?? throw new InvalidOperationException("The ownership record path has no directory part.");
            Directory.CreateDirectory(directory);

            string payload = JsonSerializer.Serialize(record, SerializerOptions);
            string temporaryPath = GetTemporaryPath();
            File.WriteAllText(temporaryPath, payload);
            File.Move(temporaryPath, _recordPath, overwrite: true);
            _ownsRecord = true;
        }
    }

    /// <summary>
    /// Reads the record. Returns <c>null</c> when no record exists, which the contract treats as a
    /// normal start. A record that exists but cannot be parsed or lacks usable values is an explicit
    /// failure, never a silent clean-up.
    /// </summary>
    /// <remarks>
    /// A successful read marks the record as not written by this store, which is what lets the takeover
    /// path delete a previous owner's leftover without spending this store for the replacement write.
    /// </remarks>
    public SessionOwnerRecord? Read()
    {
        lock (_sync)
        {
            SessionOwnerRecord? record = ReadRecordUnderLock();
            _ownsRecord = false;
            return record;
        }
    }

    private SessionOwnerRecord? ReadRecordUnderLock()
    {
        if (!File.Exists(_recordPath))
        {
            return null;
        }

        string payload = File.ReadAllText(_recordPath);
        SessionOwnerRecord record;
        try
        {
            record = JsonSerializer.Deserialize<SessionOwnerRecord>(payload, SerializerOptions)
                ?? throw new JsonException("The ownership record parsed to nothing.");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(
                "The ownership record exists but cannot be parsed, so it must be inspected by hand instead of being deleted.",
                error);
        }

        Validate(record);
        return record;
    }

    /// <summary>Deletes the record and any leftover temporary file. Idempotent, so shutdown can always call it.</summary>
    /// <remarks>
    /// Deleting a record this store wrote, or deleting after this store declared <c>closing</c>, is the
    /// release: the record may disappear only after the lock is about to be released, so a write that
    /// arrives afterwards can only come from a handler that outlived the lock, and the store is spent.
    /// Deleting a record this store only read is the takeover of a previous owner's leftover; it is
    /// followed by the replacement record, so that store stays usable.
    /// </remarks>
    public void Delete()
    {
        lock (_sync)
        {
            bool owned = _ownsRecord;

            string temporaryPath = GetTemporaryPath();
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (File.Exists(_recordPath))
            {
                File.Delete(_recordPath);
            }

            _ownsRecord = false;
            if (owned || _closingDeclared)
            {
                _released = true;
            }
        }
    }

    private string GetTemporaryPath()
    {
        return _recordPath + ".tmp";
    }

    private static void Validate(SessionOwnerRecord record)
    {
        List<string> unusableFields = new();
        if (string.IsNullOrWhiteSpace(record.SessionId))
        {
            unusableFields.Add("sessionId");
        }

        if (record.HostPid <= 0)
        {
            unusableFields.Add("hostPid");
        }

        if (record.HostProcessStartTime == default)
        {
            unusableFields.Add("hostProcessStartTime");
        }

        // Session id 0 is a real Windows session, so only a negative value is unusable here.
        if (record.SubagentWtsSessionId < 0)
        {
            unusableFields.Add("subagentWtsSessionId");
        }

        if (record.AcquiredAt == default)
        {
            unusableFields.Add("acquiredAt");
        }

        if (unusableFields.Count > 0)
        {
            throw new InvalidOperationException(
                $"The ownership record exists but has unusable values for {string.Join(", ", unusableFields)}, so it must be inspected by hand instead of being deleted.");
        }
    }

    private static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string? userSid = identity.User?.Value;
        if (string.IsNullOrEmpty(userSid))
        {
            throw new InvalidOperationException("The current Windows account has no SID, so a per-user ownership record cannot be written.");
        }

        return userSid;
    }
}