using System;
using System.IO;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Derives the host executable's path from this program's own directory.
/// </summary>
/// <remarks>
/// The path is derived from the application directory, never taken from an argument and never resolved
/// through <c>PATH</c>: a name resolved through <c>PATH</c> could start an unrelated executable, and the
/// start would then report a host that is not this product's.
/// </remarks>
public static class HostExecutablePath
{
    /// <summary>The host executable name, next to this program.</summary>
    public const string FileName = "SoulComputerUse.Host.exe";

    /// <summary>
    /// Resolves the host executable inside <paramref name="applicationDirectory"/> and returns the
    /// complete absolute path.
    /// </summary>
    /// <exception cref="ArgumentException">The application directory is empty.</exception>
    /// <exception cref="FileNotFoundException">No host executable sits in that directory.</exception>
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
                $"The host executable '{FileName}' is missing from the application directory.",
                FileName);
        }

        return executablePath;
    }
}
