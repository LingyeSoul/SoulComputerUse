using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// Attaches the agent to the console of the process that started it, when that process has one.
/// </summary>
/// <remarks>
/// <para>
/// The agent is built as a window-subsystem process on purpose: the child session starts it from its own
/// Run entry on the desktop the operator watches through the product window, and a console-subsystem
/// process makes Windows open a black console window there. A window-subsystem process is not attached to
/// its caller's console, so a manual run from a terminal would lose the diagnostic lines the agent writes
/// to standard error; attaching restores exactly that case.
/// </para>
/// <para>
/// Attaching never creates a console. The production parent (whatever process runs the child session's
/// entry) has none, so the attach fails and the agent keeps running without any window.
/// </para>
/// </remarks>
internal static class AgentConsole
{
    /// <summary><c>ATTACH_PARENT_PROCESS</c> from wincon.h.</summary>
    private const int AttachParentProcess = -1;

    /// <summary>
    /// Attaches to the caller's console when it has one and rebinds the standard writers so their output
    /// reaches that console. A caller without a console leaves the process windowless.
    /// </summary>
    internal static void AttachToParentConsoleWhenPresent()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            // The caller has no console, which is the normal production case: no window, nothing to write.
            return;
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}