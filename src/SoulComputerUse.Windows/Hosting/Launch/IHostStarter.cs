using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Makes a session exist: attaches to an owner that is already running, or launches one host and waits for
/// it to take the single-instance lock.
/// </summary>
/// <remarks>
/// The CLI and the MCP adapter depend only on this surface. Keeping them here means neither caller reaches
/// the launch guards, the wait clock or the executable path, and both get the same decision order.
/// </remarks>
public interface IHostStarter
{
    /// <summary>Ensures a session exists and reports whether one was already running.</summary>
    /// <param name="cancellationToken">Cancels the probe and the wait for the lock.</param>
    /// <returns>
    /// <see cref="HostStartOutcome.AlreadyRunning"/> when an owner already answered, otherwise
    /// <see cref="HostStartOutcome.Started"/> once the launched host took the lock.
    /// </returns>
    /// <exception cref="SessionOperationException">
    /// The caller must retry (<c>E_NOT_READY</c>), the lock is held without an answer (<c>E_LOCK_HELD</c>),
    /// or the host could not be launched (<c>E_HOST_LAUNCH_FAILED</c>).
    /// </exception>
    Task<HostStartOutcome> StartAsync(CancellationToken cancellationToken);
}
