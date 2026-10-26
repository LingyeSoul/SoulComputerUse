using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.SessionLifecycle;

/// <summary>The process exit codes the lifecycle plans use.</summary>
public static class SessionLifecycleExitCodes
{
    /// <summary>The planned work completed and the process may keep running or exit cleanly.</summary>
    public const int Success = 0;

    /// <summary>The planned work was refused or a teardown step failed.</summary>
    public const int Failure = 1;
}

/// <summary>The error codes this lifecycle layer reports for a refused startup.</summary>
/// <remarks>
/// <see cref="LockHeld"/> and <see cref="LockStaleRecord"/> are the contract's section 2 codes.
/// <see cref="OwnerRecordWriteFailed"/> is specific to the write rule in section 3, which requires a
/// non-zero exit but does not reserve a separate code in the section 2 table.
/// </remarks>
public static class SessionLifecycleErrorCodes
{
    /// <summary>Another process already holds the machine-wide lock.</summary>
    public const string LockHeld = "E_LOCK_HELD";

    /// <summary>A record exists but cannot be parsed, has unusable fields, or fails the takeover rules.</summary>
    public const string LockStaleRecord = "E_LOCK_STALE_RECORD";

    /// <summary>The lock was acquired but the ownership record could not be written.</summary>
    public const string OwnerRecordWriteFailed = "E_OWNER_RECORD_WRITE_FAILED";
}

/// <summary>What the startup decision allows this process to do.</summary>
public enum SessionStartupKind
{
    /// <summary>The lock is held and a new ownership record was written, so the session may start.</summary>
    Proceed = 0,

    /// <summary>The previous owner is gone, so delete its record and start a new session.</summary>
    RecoverAndProceed = 1,

    /// <summary>The session must not start; the caller exits with the planned exit code.</summary>
    Refuse = 2,
}

/// <summary>What could be learned about this account's ownership record before the startup decision.</summary>
public enum OwnerRecordReadState
{
    /// <summary>No record exists for this account, which the contract treats as a normal start.</summary>
    Absent = 0,

    /// <summary>The record was read and all of its fields are present.</summary>
    Usable = 1,

    /// <summary>A record exists but cannot be parsed or has unusable fields, so it must not be deleted silently.</summary>
    Unusable = 2,
}

/// <summary>
/// Everything the startup decision depends on. The caller reads the record, evaluates the recovery
/// evidence, deletes leftovers when the recovery rules allow it, and only then calls
/// <see cref="SessionStartup.Plan"/> with the real result of writing the new record. That order is what
/// lets a write failure be refused explicitly instead of being discovered after the session started.
/// </summary>
/// <remarks>
/// When <see cref="RecordState"/> is <see cref="OwnerRecordReadState.Unusable"/>, the decision refuses
/// the startup and never reads <see cref="OwnerRecord"/>; a caller may therefore pass the untrusted
/// record or <c>null</c> for it.
/// <para>
/// <see cref="Now"/> is authoritative for the 24-hour staleness rule: the plan evaluates the recovery
/// evidence at that instant, replacing the evidence's own timestamp. A decision therefore can never
/// mix two different clock reads.
/// </para>
/// </remarks>
public sealed record SessionStartupInput(
    LockAcquireOutcome LockOutcome,
    OwnerRecordReadState RecordState,
    SessionOwnerRecord? OwnerRecord,
    RecoveryEvidence RecoveryEvidence,
    bool OwnerRecordWriteSucceeded,
    DateTimeOffset Now);

/// <summary>
/// One immutable startup decision. It names the reason and the contract error code, so the caller can
/// release the lock and exit without inventing a message or a non-zero code of its own.
/// </summary>
public sealed record SessionStartupPlan
{
    /// <summary>Builds a decision and refuses combinations that could silently start a session.</summary>
    public SessionStartupPlan(
        SessionStartupKind kind,
        string? errorCode,
        int exitCode,
        string reason,
        bool releaseLock)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown startup decision kind.");
        }

        if (exitCode < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exitCode), exitCode, "An exit code must not be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (kind == SessionStartupKind.Refuse)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
            {
                throw new ArgumentException("A refusal must carry a contract error code.", nameof(errorCode));
            }

            if (exitCode == SessionLifecycleExitCodes.Success)
            {
                throw new ArgumentException("A refusal must not use the success exit code.", nameof(exitCode));
            }
        }
        else
        {
            if (errorCode is not null)
            {
                throw new ArgumentException("A non-refusal decision must not carry an error code.", nameof(errorCode));
            }

            if (exitCode != SessionLifecycleExitCodes.Success)
            {
                throw new ArgumentException("A non-refusal decision must use the success exit code.", nameof(exitCode));
            }

            if (releaseLock)
            {
                throw new ArgumentException("A non-refusal decision keeps the lock and must not release it.", nameof(releaseLock));
            }
        }

        Kind = kind;
        ErrorCode = errorCode;
        ExitCode = exitCode;
        Reason = reason;
        ReleaseLock = releaseLock;
    }

    /// <summary>The decision itself.</summary>
    public SessionStartupKind Kind { get; }

    /// <summary>The section 2 code for a refusal, or <c>null</c> when the session may start.</summary>
    public string? ErrorCode { get; }

    /// <summary>The process exit code, zero only when the session may start.</summary>
    public int ExitCode { get; }

    /// <summary>A fixed, operator-readable explanation; it never contains machine-specific paths.</summary>
    public string Reason { get; }

    /// <summary>
    /// True when the caller already holds the lock and must close its handle before exiting. A refusal
    /// caused by another process holding the lock is false, because this process never acquired it.
    /// </summary>
    public bool ReleaseLock { get; }

    /// <summary>
    /// True when the decision is a takeover. A takeover requires deleting the leftover record before the
    /// replacement is written, so the caller performs that deletion before it supplies the write result
    /// to <see cref="SessionStartup.Plan"/>. Every refusal, including a write failure after a takeover
    /// attempt, is false.
    /// </summary>
    public bool DeletesStaleRecord => Kind == SessionStartupKind.RecoverAndProceed;
}

/// <summary>
/// The startup half of the session lifecycle from section 3: take the machine-wide lock, decide whether
/// a leftover record may be taken over, and refuse explicitly when a usable ownership record cannot be
/// established. Every side effect stays with the caller; this type only decides.
/// </summary>
public static class SessionStartup
{
    /// <summary>Decides what the process may do after the lock attempt and the record read.</summary>
    public static SessionStartupPlan Plan(SessionStartupInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureConsistent(input);

        if (input.LockOutcome == LockAcquireOutcome.HeldElsewhere)
        {
            return Refuse(
                SessionLifecycleErrorCodes.LockHeld,
                false,
                "Another process already holds the machine-wide session lock. Inspect the running SoulComputerUse processes in Task Manager before retrying; this process must not start a second session.");
        }

        if (input.RecordState == OwnerRecordReadState.Unusable)
        {
            return Refuse(
                SessionLifecycleErrorCodes.LockStaleRecord,
                true,
                "An ownership record exists but cannot be parsed or has unusable fields. It must be inspected by hand instead of being deleted automatically.");
        }

        if (input.RecordState == OwnerRecordReadState.Usable && !IsUsableRecord(input.OwnerRecord!))
        {
            return Refuse(
                SessionLifecycleErrorCodes.LockStaleRecord,
                true,
                "An ownership record exists but at least one of its fields is unusable. It must be inspected by hand instead of being deleted automatically.");
        }

        RecoveryDecision decision = SessionRecoveryPolicy.Evaluate(input.RecoveryEvidence with { Now = input.Now });
        return decision switch
        {
            RecoveryDecision.NormalStart => Complete(
                input,
                SessionStartupKind.Proceed,
                "No ownership record exists for this account, and the new record was written, so this is a normal start."),
            RecoveryDecision.RecoverLeftovers => Complete(
                input,
                SessionStartupKind.RecoverAndProceed,
                "The previous owner is gone and the record meets the takeover criteria; the takeover deletes the leftover record before writing the replacement and then continues."),
            RecoveryDecision.ManualInterventionRequired => Refuse(
                SessionLifecycleErrorCodes.LockStaleRecord,
                true,
                "The ownership record does not meet the takeover criteria, so a human must inspect the leftovers before another session starts."),
            _ => throw new ArgumentOutOfRangeException(nameof(input), decision, "Unknown recovery decision."),
        };
    }

    private static SessionStartupPlan Complete(SessionStartupInput input, SessionStartupKind kind, string reason)
    {
        if (!input.OwnerRecordWriteSucceeded)
        {
            return Refuse(
                SessionLifecycleErrorCodes.OwnerRecordWriteFailed,
                true,
                "The ownership record could not be written. The lock must be released and the process must exit instead of running without a recorded owner.");
        }

        return new SessionStartupPlan(kind, null, SessionLifecycleExitCodes.Success, reason, false);
    }

    private static SessionStartupPlan Refuse(string errorCode, bool releaseLock, string reason)
    {
        return new SessionStartupPlan(
            SessionStartupKind.Refuse,
            errorCode,
            SessionLifecycleExitCodes.Failure,
            reason,
            releaseLock);
    }

    private static void EnsureConsistent(SessionStartupInput input)
    {
        if (!Enum.IsDefined(input.LockOutcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.LockOutcome,
                "The lock acquisition outcome is not a known value.");
        }

        if (!Enum.IsDefined(input.RecordState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.RecordState,
                "The ownership record read state is not a known value.");
        }

        if (input.RecoveryEvidence is null)
        {
            throw new ArgumentException("The recovery evidence must not be null.", nameof(input));
        }

        if (input.Now == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.Now,
                "The startup decision needs a real timestamp, not the default value.");
        }

        bool recordExists = input.RecordState != OwnerRecordReadState.Absent;
        if (input.RecoveryEvidence.RecordExists != recordExists)
        {
            throw new ArgumentException(
                "The recovery evidence disagrees with the record read state about whether a record exists.",
                nameof(input));
        }

        switch (input.RecordState)
        {
            case OwnerRecordReadState.Absent when input.OwnerRecord is not null:
                throw new ArgumentException(
                    "No record exists, so no ownership record may be supplied.",
                    nameof(input));
            case OwnerRecordReadState.Usable when input.OwnerRecord is null:
                throw new ArgumentException(
                    "The record read state says the record is usable, but no record was supplied.",
                    nameof(input));
            case OwnerRecordReadState.Usable:
                EnsureEvidenceMatchesRecord(input.OwnerRecord!, input.RecoveryEvidence);
                break;
        }
    }

    private static void EnsureEvidenceMatchesRecord(SessionOwnerRecord record, RecoveryEvidence evidence)
    {
        if (!Enum.IsDefined(record.State) || record.AcquiredAt == default)
        {
            // The record is already unusable in a field this check would compare. Plan refuses it with
            // the stale-record code, so a data problem must not become an exception here.
            return;
        }

        if (evidence.AcquiredAt == default)
        {
            throw new ArgumentException(
                "The recovery evidence for an existing record needs the record's acquisition time.",
                nameof(evidence));
        }

        if (evidence.RecordedState != record.State)
        {
            throw new ArgumentException(
                "The recovery evidence state disagrees with the ownership record state.",
                nameof(evidence));
        }

        if (evidence.AcquiredAt != record.AcquiredAt)
        {
            throw new ArgumentException(
                "The recovery evidence acquisition time disagrees with the ownership record.",
                nameof(evidence));
        }
    }

    private static bool IsUsableRecord(SessionOwnerRecord record)
    {
        return !string.IsNullOrWhiteSpace(record.SessionId)
            && record.HostPid > 0
            && record.HostProcessStartTime != default
            && record.SubagentWtsSessionId >= 0
            && Enum.IsDefined(record.State)
            && record.AcquiredAt != default;
    }
}
