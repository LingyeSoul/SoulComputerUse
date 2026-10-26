using System;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The next action a startup plan asks for. The order is the contract's startup order, and
/// <see cref="None"/> means there is no next action because the session is refused.
/// </summary>
public enum SessionStartStep
{
    /// <summary>No action: the plan refuses the session.</summary>
    None = 0,

    /// <summary>Log off every leftover child session before anything is connected.</summary>
    Sweep = 1,

    /// <summary>Ask the operator for administrator consent and enable the child-session switch.</summary>
    EnableChildSessions = 2,

    /// <summary>Connect the child-session agent pipe.</summary>
    ConnectAgent = 3,

    /// <summary>Hand the held lock, the record and the agent client to the running session.</summary>
    Run = 4,
}

/// <summary>The fixed relation between a plan kind and the step it may ask for.</summary>
public static class SessionStartSteps
{
    /// <summary>Returns true when the step is one a plan may name.</summary>
    public static bool IsDefined(SessionStartStep step)
    {
        return step is SessionStartStep.None
            or SessionStartStep.Sweep
            or SessionStartStep.EnableChildSessions
            or SessionStartStep.ConnectAgent
            or SessionStartStep.Run;
    }

    /// <summary>Renders the step for a message; an unknown value is an explicit error.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The step is not a defined value.</exception>
    public static string ToText(SessionStartStep step)
    {
        if (!IsDefined(step))
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown startup step.");
        }

        return step switch
        {
            SessionStartStep.None => "none",
            SessionStartStep.Sweep => "sweep",
            SessionStartStep.EnableChildSessions => "enable-child-sessions",
            SessionStartStep.ConnectAgent => "connect-agent",
            SessionStartStep.Run => "run",
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown startup step."),
        };
    }
}
