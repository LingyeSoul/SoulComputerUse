using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// The real probe behind owner discovery: it opens the lock for <c>SYNCHRONIZE</c> only and connects
/// to the owner pipe for a bounded moment, disposing both again immediately.
/// </summary>
/// <remarks>
/// This layer can distinguish "a lock object exists but nothing answers on the pipe" from "no owner is
/// running". It cannot identify the process that holds the lock: enumerating machine-wide handles
/// needs privileges an ordinary account does not have, so there is deliberately no holder enumeration
/// here. That boundary matches the contract's <c>E_LOCK_FOREIGN_HANDLE</c> diagnosis rule, which
/// refuses to turn "not found" into "not there".
/// <para>
/// The pipe probe connects and disconnects without sending a frame. It therefore proves reachability,
/// not protocol identity; the typed client repeats the peer account check when the first real call is
/// made.
/// </para>
/// </remarks>
public sealed class OwnerProbe : IOwnerProbe
{
    /// <summary>How long a single owner pipe probe may take before the owner is treated as silent.</summary>
    public static readonly TimeSpan PipeProbeTimeout = TimeSpan.FromMilliseconds(300);

    private readonly string _lockName;
    private readonly string _pipeName;

    /// <summary>Creates the probe for the production lock and owner pipe names.</summary>
    public OwnerProbe()
        : this(SingleInstanceLock.MutexName, IpcPipeNames.Owner)
    {
    }

    /// <summary>
    /// Creates the probe for one lock and one pipe name. Internal so only test assemblies can point the
    /// probe at a name of their own instead of the contract-pinned production names.
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty.</exception>
    internal OwnerProbe(string lockName, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _lockName = lockName;
        _pipeName = pipeName;
    }

    /// <inheritdoc />
    public bool LockExists()
    {
        return SingleInstanceLock.IsHeldByAnyone(_lockName);
    }

    /// <inheritdoc />
    public async Task<bool> IsOwnerPipeReachableAsync(CancellationToken cancellationToken)
    {
        NamedPipeClientStream stream = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync((int)PipeProbeTimeout.TotalMilliseconds, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // A silent pipe, a missing pipe and a pipe owned by another account all mean the same
            // thing for discovery: there is no owner this client may attach to.
            return false;
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }
}
