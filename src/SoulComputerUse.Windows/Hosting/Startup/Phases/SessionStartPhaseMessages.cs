namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// The fixed explanations a phase decision may carry. Each is a template plus an enumerated phase or
/// action name, so no explanation can contain a path, an account, a SID or a nonce.
/// </summary>
public static class SessionStartPhaseMessages
{
    /// <summary>The explanation when the leftover sweep has not finished.</summary>
    public const string SweepPending =
        "The leftover child-session sweep has not proved that no logged-on child session is left, so nothing may connect yet.";

    /// <summary>The explanation when the child-session switch is neither confirmed nor settled.</summary>
    public const string ChildSessionsPending =
        "The child-session switch has not been settled, so a connection could fail or take over the operator's console session.";

    /// <summary>The explanation when the alternate-shell properties are not written.</summary>
    public const string AlternateShellPending =
        "The alternate-shell properties are not configured, so a connection would start the usual shell instead of the agent.";

    /// <summary>The explanation when the connection has not been opened.</summary>
    public const string ConnectionPending =
        "The child-session connection has not been opened, so the window cannot show the Windows logon screen that the operator has to complete.";

    /// <summary>
    /// The explanation for an agent that reports itself attached before any connection was opened. The
    /// order is what makes the rest of the pipeline meaningful, so it is stated rather than repaired.
    /// </summary>
    public const string AgentBeforeConnection =
        "The connection must precede the agent attachment, because the agent runs as the child session's alternate shell only after the operator logs on; an agent reported before the connection is open cannot be trusted, so the connection comes first.";

    /// <summary>The explanation when the operator has logged on but the agent is not attached.</summary>
    public const string AgentPending =
        "The connection is open but the agent is not attached, so the session cannot serve a tool call yet.";

    /// <summary>The explanation when the agent is attached but its Windows session is not recorded.</summary>
    public const string SubagentSessionPending =
        "The agent is attached but its Windows session id is not recorded, so the agent could not verify that it is claimed.";

    /// <summary>The explanation when the owner pipe is not started.</summary>
    public const string OwnerPipePending =
        "The agent is attached but the owner pipe is not started, so a caller cannot reach the agent.";

    /// <summary>The explanation when every step is done except the record's ready state.</summary>
    public const string RecordReadyPending =
        "The owner pipe is serving calls but the ownership record has not been moved to ready, so a caller would still be refused.";

    /// <summary>The explanation for a session whose every step is done.</summary>
    public const string Ready =
        "The connection is open, the agent is attached, its Windows session is recorded, the owner pipe is serving calls and the record is ready.";

    /// <summary>The explanation for a before-connect segment that refused or failed.</summary>
    public const string BeforeConnectFailed =
        "The before-connect phase did not finish, so no connection was opened and the session did not start.";

    /// <summary>The explanation for a connection that could not be opened.</summary>
    public const string ConnectionFailed =
        "The connection could not be opened, so the window never showed the Windows logon screen and the agent was never attached.";

    /// <summary>The explanation for an after-logon segment that refused or failed.</summary>
    public const string AfterLogonFailed =
        "The after-logon phase did not finish after the operator logged on, so the session is not ready and no caller may use it.";

    /// <summary>The explanation for a finished after-logon segment whose record is not ready.</summary>
    public const string RecordNotReady =
        "The after-logon phase finished without moving the ownership record to ready, so the session is not ready and no caller may use it.";
}
