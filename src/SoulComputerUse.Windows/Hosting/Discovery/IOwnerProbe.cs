using System.Threading;
using System.Threading.Tasks;

namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// The observations owner discovery needs. Probing never creates the lock object and never creates a
/// pipe server, so a client can look before it decides to become an owner.
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

    /// <summary>
    /// Reports whether this account's ownership record names a live owner process whose state is still
    /// starting or connecting, which is the logon window in which the lock is taken but the pipe is not
    /// serving yet.
    /// </summary>
    /// <remarks>
    /// Read-only: a missing record, an unreadable record and a process that died after it was read all
    /// mean <c>false</c>. Nothing here throws for those states, because "not observed" and "observed as
    /// not starting" lead to the same caller decision.
    /// <para>
    /// Callers consult this only when the lock exists and the pipe did not answer, so a healthy session
    /// costs no record read.
    /// </para>
    /// </remarks>
    bool IsLiveOwnerStarting();
}
