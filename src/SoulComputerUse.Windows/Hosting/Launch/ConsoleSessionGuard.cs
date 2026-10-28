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
/// The check fails closed for both reads. When no session is attached to the console,
/// <see cref="WtsSessionProbe.GetActiveConsoleSessionId"/> throws instead of returning a session id, and
/// the read of this process's own session can fail the same way. In either case the comparison is not one
/// this guard can rely on, so it reports <see langword="false"/> rather than letting the failure escape
/// as an unhandled error.
/// </para>
/// </remarks>
public sealed class ConsoleSessionGuard : IConsoleSessionGuard
{
    private readonly Func<int> _readProcessSessionId;
    private readonly Func<int> _readConsoleSessionId;

    /// <summary>Creates the guard over the real Windows session table.</summary>
    public ConsoleSessionGuard()
        : this(WtsSessionProbe.GetCurrentProcessSessionId, WtsSessionProbe.GetActiveConsoleSessionId)
    {
    }

    /// <summary>
    /// Creates the guard over two injected session readers, so the fail-closed branch can be exercised on a
    /// machine that does have a console session. Production code always uses the parameterless constructor.
    /// </summary>
    /// <exception cref="ArgumentNullException">A reader is null.</exception>
    internal ConsoleSessionGuard(Func<int> readProcessSessionId, Func<int> readConsoleSessionId)
    {
        ArgumentNullException.ThrowIfNull(readProcessSessionId);
        ArgumentNullException.ThrowIfNull(readConsoleSessionId);
        _readProcessSessionId = readProcessSessionId;
        _readConsoleSessionId = readConsoleSessionId;
    }

    /// <inheritdoc />
    public bool IsCurrentProcessOnConsoleSession()
    {
        int processSessionId;
        int consoleSessionId;
        try
        {
            processSessionId = _readProcessSessionId();
            consoleSessionId = _readConsoleSessionId();
        }
        catch (InvalidOperationException)
        {
            // Neither read completing means the comparison cannot be made. The answer is therefore a
            // refusal, not an error: the launcher has nothing to change based on how a read reported
            // its failure, and a launch that cannot prove this process is on the console session must
            // never show a host window on a desktop nobody can see.
            return false;
        }

        return processSessionId == consoleSessionId;
    }
}
