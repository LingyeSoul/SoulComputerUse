namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Starts the host executable as a process the caller cannot take with it when it ends.
/// </summary>
/// <remarks>
/// The port exists so the launcher's decision table can be tested without starting a real executable.
/// Production has exactly one implementation, <see cref="HostProcessStarter"/>.
/// </remarks>
public interface IHostProcessStarter
{
    /// <summary>
    /// Starts <paramref name="executablePath"/> with <paramref name="arguments"/> and returns a handle
    /// that observes the new process.
    /// </summary>
    /// <param name="executablePath">The complete path of the executable to start.</param>
    /// <param name="arguments">The command-line arguments, already formatted for the executable.</param>
    /// <returns>A handle that observes the started process.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// The process could not be created, or the caller's job object forbids the breakaway the host
    /// needs in order to outlive the caller.
    /// </exception>
    IHostProcessHandle Start(string executablePath, string arguments);
}
