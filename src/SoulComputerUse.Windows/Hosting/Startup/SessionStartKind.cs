namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>What the startup decision allows this process to do.</summary>
public enum SessionStartKind
{
    /// <summary>Every evaluated gate allows the session, and no leftover record blocks the start.</summary>
    Proceed = 0,

    /// <summary>
    /// A leftover ownership record was found and meets the takeover criteria, so it is deleted and
    /// replaced before the session starts.
    /// </summary>
    RecoverAndProceed = 1,

    /// <summary>The session must not start; the plan names the contract code that refuses it.</summary>
    Refuse = 2,
}
