using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Windows.Hosting.Startup;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The session a launch produced: the start report, the release ports that undo it, and the release
/// operations a caller needs to shut it down.
/// </summary>
/// <remarks>
/// <para>
/// The handle owns everything the start handed over, so a caller that never disposes it leaks the lock
/// handle, the agent connection and the owner pipe. Both release operations are idempotent and each
/// resource is released at most once across them.
/// </para>
/// <para>
/// Two orders exist because the shutdown contract and a failed launch need different ones. A failed
/// launch uses <see cref="DisposeAsync"/>, which stops the owner pipe, closes the agent and releases
/// the record and the lock. A clean close runs the contract's teardown plan first — entering
/// <c>closing</c>, logging the child session off, restoring the switch, stopping the owner pipe and
/// releasing the lock — and only then closes the agent with
/// <see cref="CloseAgentAfterShutdownAsync"/>, because the record must disappear before the transports
/// that depended on it.
/// </para>
/// </remarks>
public sealed class HostedSessionHandle : IAsyncDisposable
{
    private readonly SessionStartReport _report;
    private readonly SessionReleasePorts _ports;
    private int _disposed;

    /// <summary>Creates the handle over the report and the ports that release it.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public HostedSessionHandle(SessionStartReport report, SessionReleasePorts ports)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(ports);
        _report = report;
        _ports = ports;
    }

    /// <summary>The start report this handle owns.</summary>
    public SessionStartReport Report => _report;

    /// <summary>Releases every resource once, and reports the failures together.</summary>
    /// <exception cref="AggregateException">At least one release step failed.</exception>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IReadOnlyList<Exception> failures = await _ports.ReleaseAllAsync(_report).ConfigureAwait(false);
        ThrowIfFailed(failures);
    }

    /// <summary>
    /// Closes the agent connection after the contract's teardown plan already stopped the owner pipe
    /// and released the ownership record and the lock handle. Idempotent, and it never touches the lock.
    /// </summary>
    /// <exception cref="AggregateException">Closing the agent connection failed.</exception>
    public async ValueTask CloseAgentAfterShutdownAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IReadOnlyList<Exception> failures = await _ports.ReleaseAgentAsync(_report).ConfigureAwait(false);
        ThrowIfFailed(failures);
    }

    private static void ThrowIfFailed(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        List<Exception> combined = [.. failures];
        throw new AggregateException(
            "The launched session could not release every resource, so the ownership record, the lock handle, the agent connection or the owner pipe may remain.",
            combined);
    }
}