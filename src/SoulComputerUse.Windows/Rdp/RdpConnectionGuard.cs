using System;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>Decides whether a connection may be attempted at all.</summary>
/// <remarks>
/// The only legitimate connection this product makes is a child session on this machine. A local
/// connection without the child-session setting takes over the operator's console session and logs
/// them out, so the check is fail-closed: without an explicit child-session intent, no connection is
/// allowed, local or not.
/// <para>
/// An earlier version tried to recognise "this machine" by name, which only refuses the spellings it
/// knows: a raw local IP or an alias resolving to this machine would have passed. Not enumerating is
/// the only way not to enumerate wrongly, and this product has no remote use case to preserve.
/// </para>
/// </remarks>
public static class RdpConnectionGuard
{
    /// <summary>Refuses any connection that is not an explicit child-session connection.</summary>
    public static void EnsureChildSessionIntent(bool connectToChildSession, string server)
    {
        if (connectToChildSession)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to connect to '{server}': without an explicit child-session intent a local connection would take over the console session and log the operator out, and this product has no remote connection use case.");
    }
}

/// <summary>
/// Best-effort recognition of a server name that denotes this machine, used only to make diagnostic
/// messages clearer. It must never be used to allow a connection: see <see cref="RdpConnectionGuard"/>.
/// </summary>
public static class RdpServerDescription
{
    private static readonly string[] LoopbackNames =
    [
        "localhost",
        "127.0.0.1",
        "::1",
        "[::1]",
    ];

    /// <summary>
    /// Tells whether a server name is recognisable as this machine. A false answer means "not
    /// recognised", not "remote": the guard does not rely on this to permit anything.
    /// </summary>
    public static bool IsRecognisablyLocal(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);

        string trimmed = server.Trim();
        foreach (string loopbackName in LoopbackNames)
        {
            if (string.Equals(trimmed, loopbackName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string machineName = Environment.MachineName;
        return string.Equals(trimmed, machineName, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(machineName + ".", StringComparison.OrdinalIgnoreCase);
    }
}