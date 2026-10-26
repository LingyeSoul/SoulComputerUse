using System.Threading;
using System.Threading.Tasks;

namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// The two observations owner discovery needs. Probing never creates the lock object and never
/// creates a pipe server, so a client can look before it decides to become an owner.
/// </summary>
public interface IOwnerProbe
{
    /// <summary>Reports whether the single-instance lock object exists, without creating it.</summary>
    bool LockExists();

    /// <summary>
    /// Reports whether a session owner answered on the owner pipe within the probe timeout. A timeout
    /// or a refused connection is reported as <c>false</c>; a failure that is not a connection failure
    /// is thrown, because it means the machine state could not be observed at all.
    /// </summary>
    Task<bool> IsOwnerPipeReachableAsync(CancellationToken cancellationToken);
}
