using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// Starts a headless session and returns a client attached to it. The host layer implements this once
/// a launcher exists that can take the machine-wide lock and establish a session.
/// </summary>
/// <remarks>
/// The implementation owns the lifetime of the session it starts, including the single-instance lock.
/// The client it returns must release that session, and with it the lock, when it is disposed as
/// <see cref="System.IAsyncDisposable"/>; discovery only decides when to call the starter and wraps
/// the returned client. A client that cannot release the session, or a starter that leaks the lock on
/// shutdown, is a defect in the host layer rather than something discovery can repair.
/// </remarks>
public interface IOwnerSessionStarter
{
    /// <summary>Starts a new session owner and returns a client connected to that owner.</summary>
    Task<ISessionClient> StartAsync(CancellationToken cancellationToken);
}
