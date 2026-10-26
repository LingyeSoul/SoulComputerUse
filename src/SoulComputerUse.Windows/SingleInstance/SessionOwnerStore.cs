using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text.Json;

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
/// </remarks>
public sealed class SessionOwnerStore
{
    /// <summary>The directory name used under the local application data folder.</summary>
    public const string DirectoryName = "SoulComputerUse";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _recordPath;

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

    /// <summary>Writes the record. A failure here must be turned into releasing the lock by the caller.</summary>
    public void Write(SessionOwnerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string directory = Path.GetDirectoryName(_recordPath)
            ?? throw new InvalidOperationException("The ownership record path has no directory part.");
        Directory.CreateDirectory(directory);

        string payload = JsonSerializer.Serialize(record, SerializerOptions);
        string temporaryPath = GetTemporaryPath();
        File.WriteAllText(temporaryPath, payload);
        File.Move(temporaryPath, _recordPath, overwrite: true);
    }

    /// <summary>
    /// Reads the record. Returns <c>null</c> when no record exists, which the contract treats as a
    /// normal start. A record that exists but cannot be parsed or lacks usable values is an explicit
    /// failure, never a silent clean-up.
    /// </summary>
    public SessionOwnerRecord? Read()
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
    public void Delete()
    {
        string temporaryPath = GetTemporaryPath();
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        if (File.Exists(_recordPath))
        {
            File.Delete(_recordPath);
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