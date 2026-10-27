namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// What a start request had to do: use the owner that was already serving the pipe, or launch a host and
/// observe it take the single-instance lock.
/// </summary>
/// <remarks>
/// Both outcomes are success. The distinction exists so a caller can report whether the product window was
/// opened by this request or was already there, never so a caller can treat one as an error.
/// </remarks>
public enum HostStartOutcome
{
    /// <summary>An owner already answered on the pipe, so this request changed nothing.</summary>
    AlreadyRunning = 0,

    /// <summary>This request launched a host and observed the single-instance lock appear.</summary>
    Started = 1,
}
