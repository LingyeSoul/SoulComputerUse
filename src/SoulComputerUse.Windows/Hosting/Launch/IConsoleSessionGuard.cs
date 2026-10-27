using System;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Answers whether the caller runs on the session attached to the interactive console, the only session
/// whose desktop the operator is actually looking at.
/// </summary>
/// <remarks>
/// A launch from another session would put the host's window and its UAC prompt on a desktop nobody can
/// see, and the start would then wait for a logon the operator cannot perform. The port exists so the
/// launcher's decision table can pin either answer without reading the real session table.
/// </remarks>
public interface IConsoleSessionGuard
{
    /// <summary>Reports whether this process runs on the active console session.</summary>
    /// <remarks>
    /// Implementations fail closed: when the active console session cannot be read at all, the answer is
    /// <see langword="false"/>, because an unreadable console session is not one a launch may rely on.
    /// </remarks>
    bool IsCurrentProcessOnConsoleSession();
}
