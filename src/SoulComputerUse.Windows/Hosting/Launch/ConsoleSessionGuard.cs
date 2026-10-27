using System;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Compares this process's Terminal Services session with the session attached to the interactive
/// console.
/// </summary>
/// <remarks>
/// The comparison is the whole check. A process in any other session — a remote desktop session, a
/// disconnected session or the service session — is refused, because the host it would start shows its
/// window and its UAC prompt on a desktop the operator is not looking at.
/// <para>
/// The check fails closed. When no session is attached to the console,
/// <see cref="WtsSessionProbe.GetActiveConsoleSessionId"/> throws instead of returning a session id; that
/// is not a session this process could be on, so the guard reports <see langword="false"/> rather than
/// letting the probe's failure escape as an unhandled error.
/// </para>
/// </remarks>
public sealed class ConsoleSessionGuard : IConsoleSessionGuard
{
    /// <inheritdoc />
    public bool IsCurrentProcessOnConsoleSession()
    {
        int processSessionId = WtsSessionProbe.GetCurrentProcessSessionId();
        int consoleSessionId;
        try
        {
            consoleSessionId = WtsSessionProbe.GetActiveConsoleSessionId();
        }
        catch (InvalidOperationException)
        {
            // No session is attached to the console, so no process can be on it. The answer is therefore
            // a refusal, not an error: the launcher has nothing to change based on how the probe reported
            // the absence.
            return false;
        }

        return processSessionId == consoleSessionId;
    }
}
