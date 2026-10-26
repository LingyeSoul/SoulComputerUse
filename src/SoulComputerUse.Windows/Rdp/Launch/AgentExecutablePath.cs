using System;
using System.IO;

namespace SoulComputerUse.Windows.Rdp.Launch;

/// <summary>
/// Derives the child-session agent's executable path from this program's own directory.
/// </summary>
/// <remarks>
/// The path is derived, never taken from an argument and never resolved through <c>PATH</c>: the agent
/// has to be the copy installed next to this program, exactly as the elevation helper is. A name
/// resolved through <c>PATH</c> could start an unrelated executable inside the child session, and the
/// alternate shell would then run that program with the operator's own identity.
/// </remarks>
public static class AgentExecutablePath
{
    /// <summary>The agent executable name, next to this program.</summary>
    public const string FileName = "SoulComputerUse.SessionAgent.exe";

    /// <summary>
    /// Resolves the agent executable inside <paramref name="applicationDirectory"/> and returns the
    /// complete absolute path.
    /// </summary>
    /// <exception cref="ArgumentException">The application directory is empty.</exception>
    /// <exception cref="FileNotFoundException">No agent executable sits in that directory.</exception>
    public static string Resolve(string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            throw new ArgumentException("The application directory must not be empty.", nameof(applicationDirectory));
        }

        string executablePath = Path.GetFullPath(Path.Combine(applicationDirectory, FileName));
        if (!File.Exists(executablePath))
        {
            // The message names the executable only: the real installation path must never reach a
            // console, a screenshot or an issue.
            throw new FileNotFoundException(
                $"The child-session agent '{FileName}' is missing from the application directory.",
                FileName);
        }

        return executablePath;
    }
}
