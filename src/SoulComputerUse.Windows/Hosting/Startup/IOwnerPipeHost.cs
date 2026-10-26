using System;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// Starts the owner pipe that carries tool calls from the CLI and the MCP server, using the handler the
/// caller supplies. The implementation owns the pipe name and its DACL.
/// </summary>
/// <remarks>
/// The port returns only after the pipe accepts a connection, so a caller cannot treat a dead pipe as a
/// running owner. It never waits for tool calls itself: the startup path only needs to know the pipe is
/// listening before it reports the session ready.
/// </remarks>
public interface IOwnerPipeHost
{
    /// <summary>Starts the owner pipe and returns once it accepts a connection.</summary>
    /// <param name="handler">The handler that executes tool calls arriving on the pipe.</param>
    /// <exception cref="ArgumentNullException">The handler is null.</exception>
    /// <exception cref="Exception">The pipe could not start or never accepted a connection.</exception>
    void Start(ISessionToolHandler handler);
}
