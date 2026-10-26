using System;

namespace SoulComputerUse.Windows.Rdp.Launch;

/// <summary>
/// Writes the alternate-shell properties that make a child session run the agent instead of the usual
/// shell, and refuses to accept a write the control did not keep.
/// </summary>
/// <remarks>
/// <para>
/// This type only configures properties on an already hosted control. It starts nothing and connects
/// nothing: the one entry point that connects is <c>ChildSessionConnection.Connect</c>, which also
/// carries the leftover-child-session sweep. A second launch route that connected on its own would
/// bypass that sweep, so this layer deliberately opens no connection of its own.
/// </para>
/// <para>
/// The values exist only in the in-memory connection configuration: nothing here writes the registry
/// or the disk, and the command line carries no credential material.
/// </para>
/// </remarks>
public static class RdpLaunchConfigurator
{
    /// <summary>The control property that names the program the session runs instead of the shell.</summary>
    public const string StartProgramProperty = "StartProgram";

    /// <summary>The control property that names the directory the alternate shell starts in.</summary>
    public const string WorkingDirectoryProperty = "WorkingDirectory";

    /// <summary>
    /// Writes both alternate-shell properties for one launch and reads each one back before going on.
    /// </summary>
    /// <param name="surface">The control properties to write.</param>
    /// <param name="request">The launch values to write; the command line is rendered from this request.</param>
    /// <exception cref="ArgumentNullException">The surface or the request is null.</exception>
    /// <exception cref="ArgumentException">A launch value cannot be put on a command line.</exception>
    /// <exception cref="RdpSurfaceException">The control did not keep a written value.</exception>
    public static void Apply(IRdpLaunchSurface surface, AgentLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(request);

        // The tier is detected inside the child session, so the production request declares none: a
        // declaration that the machine contradicts would stop the agent at startup.
        string commandLine = AgentLaunchCommandLine.Build(
            request.ExecutablePath,
            request.SessionId,
            request.Nonce,
            request.AuthTier,
            request.GateStrength);

        // A command line that did not stick would leave the child session running the usual shell,
        // where nothing connects the agent back, so each write is verified before the next one.
        surface.StartProgram = commandLine;
        EnsureStuck(StartProgramProperty, commandLine, surface.StartProgram);

        surface.WorkingDirectory = request.WorkingDirectory;
        EnsureStuck(WorkingDirectoryProperty, request.WorkingDirectory, surface.WorkingDirectory);
    }

    private static void EnsureStuck(string propertyName, string expected, string actual)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return;
        }

        // The property name is the only value in the message. The written text is the command line,
        // which carries the binding nonce, so it can never appear here.
        throw new RdpSurfaceException(
            "set_" + propertyName,
            $"property={propertyName}",
            0,
            $"'{propertyName}' did not stick after being written, so the session is not configured to start the agent");
    }
}
