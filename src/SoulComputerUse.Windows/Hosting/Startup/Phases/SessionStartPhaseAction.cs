namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// The one action a phase decision asks for next. The order is the contract's startup order, and
/// <see cref="None"/> means no action is left because the session is ready.
/// </summary>
/// <remarks>
/// <see cref="OpenConnection"/> and <see cref="AttachAgent"/> are separate on purpose. The agent is the
/// child session's alternate shell, so it exists only after the operator has logged on: attaching it
/// before the connection is open would look for a pipe that no process has created yet.
/// </remarks>
public enum SessionStartPhaseAction
{
    /// <summary>No action is left; the session is ready.</summary>
    None = 0,

    /// <summary>Log off every leftover child session before anything connects.</summary>
    Sweep = 1,

    /// <summary>Ask for administrator consent and enable the child-session switch.</summary>
    EnableChildSessions = 2,

    /// <summary>Write the alternate-shell properties that make the session run the agent.</summary>
    ConfigureAlternateShell = 3,

    /// <summary>Open the connection through the single connection entry point.</summary>
    OpenConnection = 4,

    /// <summary>Attach the agent pipe the child session started after the operator logged on.</summary>
    AttachAgent = 5,

    /// <summary>Record the agent's Windows session id in the ownership record.</summary>
    WriteSubagentSession = 6,

    /// <summary>Start the owner pipe that carries tool calls.</summary>
    StartOwnerPipe = 7,

    /// <summary>Move the ownership record to <c>ready</c>.</summary>
    MarkReady = 8,
}
