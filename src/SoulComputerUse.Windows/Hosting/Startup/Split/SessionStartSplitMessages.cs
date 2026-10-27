namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// The fixed explanations the split segments, the connection opener and the connection waiter may carry.
/// </summary>
/// <remarks>
/// Every entry is a fixed template that names a step or an enumerated value only, so no explanation can
/// carry a path, an account, a SID or a nonce. The startup layer's own templates stay in
/// <see cref="SessionStartMessages"/>; these cover the steps that only exist once a start is split into
/// a before-connect segment, a connection and an after-logon segment.
/// </remarks>
public static class SessionStartSplitMessages
{
    /// <summary>The explanation when the hosted control exposes no alternate-shell properties.</summary>
    public const string AlternateShellUnavailable =
        "The hosted control did not expose the alternate-shell properties, so the child session cannot be pointed at the agent and nothing may connect.";

    /// <summary>The explanation when a written alternate-shell property did not stick.</summary>
    public const string AlternateShellWriteFailed =
        "The alternate-shell properties did not stick on the hosted control, so the child session would start the usual shell instead of the agent.";

    /// <summary>
    /// The explanation when the start-up entry that starts the agent on the child session's logon could
    /// not be mounted.
    /// </summary>
    public const string AgentBootstrapWriteFailed =
        "The start-up entry that launches the agent inside the child session could not be mounted, so the session could log on without an agent and nothing could attach to it.";

    /// <summary>The explanation when the agent process reports no positive Windows session id.</summary>
    public const string AgentSessionNotPositive =
        "The agent process reported no positive Windows session id, so the ownership record cannot name the child session the agent runs in.";

    /// <summary>The explanation for a client that reached the connected state without the logon screen.</summary>
    public const string ConnectionLogonBypassed =
        "The client reported the session as connected without ever reporting the logon screen, so the operator's own logon was bypassed, most likely because a leftover child session answered the connection.";

    /// <summary>The explanation for a client that dropped back to the disconnected state.</summary>
    public const string ConnectionDisconnected =
        "The client reported the session as disconnected while the window waited for the operator to log on, so no session exists any more.";

    /// <summary>The explanation for a connection that never reached the connected state in time.</summary>
    public const string ConnectionTimedOut =
        "The client did not report the session as connected within the allowed wait, so the operator may not have finished the Windows logon.";

    /// <summary>The explanation when a refused before-connect segment also failed to clean up.</summary>
    public const string BeforeConnectCleanupFailed =
        "The before-connect segment failed and its cleanup also failed, so the ownership record or the machine-wide lock may remain.";

    /// <summary>The explanation when a failed after-logon segment also failed to clean up.</summary>
    public const string AfterLogonCleanupFailed =
        "The after-logon segment failed and its cleanup also failed, so the ownership record or the agent connection may remain.";

    /// <summary>The explanation when a segment is asked to run a second time.</summary>
    public const string SegmentAlreadyRan =
        "A startup segment runs once per attempt; a second run would take a second lock, rewrite the ownership record or attach the agent twice.";
}
