using System.Globalization;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The fixed explanations a startup decision may carry. Every template names only an enumerated value,
/// a count or a policy value name, so no message can contain a path, an account, a SID or a nonce.
/// </summary>
public static class SessionStartMessages
{
    /// <summary>The explanation for a start that may go on.</summary>
    public const string Proceed = "Every gate evaluated so far allows the session to start, so the pipeline continues.";

    /// <summary>The explanation for a start that replaces a leftover ownership record.</summary>
    public const string RecoverAndProceed =
        "The previous owner is gone and the ownership record meets the takeover criteria, so the leftover record is deleted and replaced before the pipeline continues.";

    /// <summary>The explanation for another process holding the machine-wide lock.</summary>
    public const string LockHeld =
        "Another process already holds the machine-wide session lock, so this process must not start a second session. Close the running session, or find its process in Task Manager, and retry.";

    /// <summary>The explanation for an ownership record that must not be taken over or deleted.</summary>
    public const string RecordNotTakeable =
        "An ownership record exists but cannot be parsed or does not meet the takeover criteria, so it must be inspected by hand instead of being deleted automatically.";

    /// <summary>The explanation for the tier that has no usable system verification path.</summary>
    public const string AuthTierNone =
        "The machine has no usable system verification path, so the session is refused until the operator configures Windows Hello or an interactive logon and retries.";

    /// <summary>The explanation for a sweep that did not prove a clean machine.</summary>
    /// <param name="loggedOffCount">How many sessions the attempt asked the system to log off.</param>
    /// <param name="remainingCount">How many requested sessions the attempt could not confirm as gone.</param>
    public static string SweepFailed(int loggedOffCount, int remainingCount)
    {
        return "The leftover child-session sweep failed after "
            + loggedOffCount.ToString(CultureInfo.InvariantCulture)
            + " logoff request(s) with "
            + remainingCount.ToString(CultureInfo.InvariantCulture)
            + " session(s) still present, so connecting would bypass the Windows logon gate.";
    }

    /// <summary>The explanation for a machine that allows child sessions but has the switch off.</summary>
    public const string ElevationRequired =
        "Child sessions are disabled on this machine but not forbidden, so the pipeline asks the operator to consent to enabling them before anything connects.";

    /// <summary>The explanation for the operator declining the elevation prompt.</summary>
    public const string ChildSessionsDeclined =
        "The operator declined the elevation prompt that would enable child sessions, so the session is refused.";

    /// <summary>The explanation for a machine that offers no elevation opportunity.</summary>
    public const string ElevationUnavailable =
        "This machine offers no administrator session, so an administrator has to enable child sessions once before the session can start.";

    /// <summary>The explanation for the helper that ran but could not enable the switch.</summary>
    public const string ChildSessionsDisabled =
        "The elevated helper ran but the system refused to enable child sessions, so the session is refused.";

    /// <summary>
    /// The explanation for an elevation helper that does not match the hash the installation manifest
    /// records, so it was never started.
    /// </summary>
    public const string HelperIntegrityFailed =
        "The elevation helper does not match the installation manifest, so it was not started. Reinstall the product "
        + "to restore the files the installation delivered.";

    /// <summary>The explanation for an agent connection that did not succeed.</summary>
    /// <param name="code">The contract code the connector observed.</param>
    public static string AgentFailed(SessionErrorCode code)
    {
        return "The child-session agent connection failed with the contract code '"
            + SessionErrorCodeNames.ToWireName(code)
            + "', so the session did not start.";
    }

    /// <summary>The explanation for a lock state that could not be determined.</summary>
    public const string LockProbeFailed =
        "The state of the machine-wide session lock could not be determined, so the session must not start and a human has to inspect the machine.";

    /// <summary>The explanation for an ownership record that could not be read.</summary>
    public const string RecordReadFailed =
        "The ownership record could not be read, so it must be inspected by hand instead of being deleted automatically.";

    /// <summary>The explanation for a record this attempt could not write.</summary>
    public const string RecordWriteFailed =
        "The ownership record could not be written, so the session must not keep the machine-wide lock.";

    /// <summary>The explanation for a credential tier that could not be probed.</summary>
    public const string AuthTierProbeFailed =
        "The credential gate tier could not be probed, so this machine has to be inspected before a session starts.";

    /// <summary>The explanation for a child-session switch or policy value that could not be read.</summary>
    public const string ChildSessionsProbeFailed =
        "The machine's child-session state could not be read, so a human has to inspect it before a session starts.";

    /// <summary>
    /// The explanation for a taken-over record that said the crashed owner turned the machine-wide
    /// child-session switch on, while this start could not turn it back off.
    /// </summary>
    /// <remarks>
    /// A machine-wide capability that stays on with nobody recording it cannot be handed over silently,
    /// so the start is refused and the record stays on disk for the next start to try again.
    /// </remarks>
    public const string SwitchRestoreOnTakeOverFailed =
        "The taken-over record said the previous owner turned the machine-wide child-session switch on and never turned it off, and this start could not turn it off either, so the machine state cannot be taken over and a human has to inspect it.";

    /// <summary>The explanation for a policy value that is not a DWORD.</summary>
    /// <param name="value">The policy value whose stored data has the wrong type.</param>
    public static string PolicyValueMalformed(PolicyValue value)
    {
        string valueName = value switch
        {
            PolicyValue.RdpInbound => "fDenyTSConnections",
            PolicyValue.ChildConnections => "fDenyChildConnections",
            _ => throw new System.ArgumentOutOfRangeException(nameof(value), value, "Unknown policy value."),
        };

        return "The policy value '" + valueName + "' is not a DWORD, so the machine has not expressed consent and the session is refused.";
    }

    /// <summary>The explanation for an elevation that reported success but left the switch off.</summary>
    public const string ChildSessionsStayedOff =
        "The elevation helper reported success, but the machine still reports child sessions as disabled, so the session is refused.";

    /// <summary>The explanation for an owner pipe that could not start.</summary>
    public const string OwnerPipeFailed =
        "The owner pipe that carries tool calls could not start, so the session cannot serve a caller and is refused.";

    /// <summary>The explanation for an agent session id that could not be resolved.</summary>
    public const string AgentSessionUnreadable =
        "The Windows session of the connected agent process could not be read, so the ownership record cannot name the child session.";

    /// <summary>The explanation for a stage that failed outside the decision table.</summary>
    public const string UnexpectedStageFailure =
        "A startup stage failed outside the contract's decision table, so the session did not start and a human has to inspect the machine.";

    /// <summary>The explanation for an agent connector that threw instead of reporting an outcome.</summary>
    public const string AgentConnectThrew =
        "The agent connector failed before it could report a contract code, so the session did not start.";
}
