namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// The fixed explanations for a host that could not be launched. Each is a template plus an enumerated
/// reason, a fixed file name, a number or an exit code, so no explanation can contain a path, an account, a
/// SID or a machine name.
/// </summary>
/// <remarks>
/// Every message must be readable on its own and must name the next action. The launcher never passes the
/// original exception text through: <c>CreateProcessW</c> and file-system errors carry machine data that must
/// not reach a console, a screenshot or an issue.
/// </remarks>
public static class HostLaunchMessages
{
    /// <summary>The explanation when the caller is not on the session attached to the interactive console.</summary>
    public const string NotOnConsoleSession =
        "This process is not running in the interactive console session, so the product window and its sign-in prompt "
        + "would open on a desktop nobody can see. Sign in at the console and run start from a terminal in that session.";

    /// <summary>
    /// The explanation when the caller already holds an elevated administrator token, which would skip the
    /// operator's own UAC confirmation.
    /// </summary>
    public const string CallerElevated =
        "The caller is already elevated, so the host would gain administrator authority without the operator's own "
        + "UAC confirmation. Run start from an ordinary, non-elevated terminal.";

    /// <summary>The explanation when the host executable is not next to this program.</summary>
    public const string HostExecutableMissing =
        "The host executable '" + HostExecutablePath.FileName + "' is missing from the application directory, so no "
        + "session can be started. Reinstall the product or restore that executable next to this program.";

    /// <summary>
    /// The explanation when the operating system refused to create the host process, including a caller's job
    /// object that does not allow the host to break away and outlive the caller.
    /// </summary>
    public const string ProcessCreationFailed =
        "The host process could not be created, so no session was started. Retry start from an ordinary, non-elevated "
        + "terminal; if the failure repeats, the calling environment does not allow the host to outlive this process.";

    /// <summary>
    /// The explanation when the launched host ended before it took the lock. The placeholder is the exit code,
    /// or <c>unknown</c> when the operating system did not report one.
    /// </summary>
    public const string HostExitedBeforeLock =
        "The host process exited with exit code {0} before it took the single-instance lock, so no session was "
        + "started. Check the product window or its error dialog, fix the reported problem, and retry start.";

    /// <summary>
    /// The explanation when the lock did not appear within the bounded wait. The placeholder is the timeout in
    /// seconds.
    /// </summary>
    public const string TimedOut =
        "The host did not take the single-instance lock within {0} seconds, so the start is not confirmed. Check the "
        + "product window or its sign-in prompt before retrying, and do not start a second host while the first is "
        + "still starting.";
}
