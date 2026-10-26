using System;
using System.IO;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Rdp.Launch;

/// <summary>
/// The immutable facts of one agent launch through the alternate shell: which executable runs, the
/// working directory it starts in, and the binding values the agent must report back.
/// </summary>
/// <remarks>
/// The request carries the nonce because the command line has to carry it, so the request itself is
/// never rendered with its values: a record's generated <c>ToString</c> would print every positional
/// value, including the nonce. The override below therefore renders only the fields section 10 allows.
/// The description meant to leave this layer is <see cref="AgentLaunchCommandLine.DescribeForLog"/>.
/// </remarks>
public sealed record AgentLaunchRequest(
    string ExecutablePath,
    string WorkingDirectory,
    string SessionId,
    string Nonce,
    AuthTier AuthTier,
    GateStrength GateStrength)
{
    /// <summary>
    /// Renders the request without the binding nonce and without either path, so that an accidental
    /// interpolation into a log line or an exception message cannot leak one.
    /// </summary>
    public override string ToString()
    {
        return $"session={SessionId} tier={AuthTier} strength={GateStrength}";
    }

    /// <summary>
    /// Builds a request from this program's own directory alone. This is the production route: the
    /// executable is the agent installed beside this program and the working directory is that same
    /// directory, so no caller can point the alternate shell at another executable or another
    /// directory.
    /// </summary>
    /// <exception cref="ArgumentException">The application directory is empty.</exception>
    /// <exception cref="FileNotFoundException">The agent executable is missing from that directory.</exception>
    public static AgentLaunchRequest ForApplicationDirectory(
        string applicationDirectory,
        string sessionId,
        string nonce,
        AuthTier authTier,
        GateStrength gateStrength)
    {
        string executablePath = AgentExecutablePath.Resolve(applicationDirectory);
        string workingDirectory = Path.GetFullPath(applicationDirectory);
        return new AgentLaunchRequest(executablePath, workingDirectory, sessionId, nonce, authTier, gateStrength);
    }
}
