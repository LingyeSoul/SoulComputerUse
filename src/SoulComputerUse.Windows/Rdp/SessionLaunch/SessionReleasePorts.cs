using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SoulComputerUse.Windows.Hosting.Startup;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The set of release ports one launched session uses, so a caller cannot mix the ports of two
/// sessions.
/// </summary>
public sealed record SessionReleasePorts
{
    /// <summary>Creates the set around the three ports.</summary>
    /// <exception cref="ArgumentNullException">A port is null.</exception>
    public SessionReleasePorts(ILockRelease lockRelease, ISessionAgentRelease agentRelease, IOwnerPipeRelease ownerPipeRelease)
    {
        ArgumentNullException.ThrowIfNull(lockRelease);
        ArgumentNullException.ThrowIfNull(agentRelease);
        ArgumentNullException.ThrowIfNull(ownerPipeRelease);

        LockRelease = lockRelease;
        AgentRelease = agentRelease;
        OwnerPipeRelease = ownerPipeRelease;
    }

    /// <summary>The lock-handle release.</summary>
    public ILockRelease LockRelease { get; }

    /// <summary>The agent-client release.</summary>
    public ISessionAgentRelease AgentRelease { get; }

    /// <summary>The owner-pipe release.</summary>
    public IOwnerPipeRelease OwnerPipeRelease { get; }

    /// <summary>
    /// Releases everything a ready start held, in the order a failed launch needs: stop the owner pipe,
    /// close the agent, then release the record and the lock. Every step is attempted even when an
    /// earlier one failed, so a failure cannot leave a later resource behind.
    /// </summary>
    /// <returns>The failures, which the caller reports beside the original failure.</returns>
    /// <exception cref="ArgumentNullException">The report is null.</exception>
    public async Task<IReadOnlyList<Exception>> ReleaseAllAsync(SessionStartReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<Exception> failures = [];
        await CollectAsync(failures, OwnerPipeRelease.ReleaseAsync).ConfigureAwait(false);
        await CollectAsync(failures, () => AgentRelease.ReleaseAsync(report.Agent.Client)).ConfigureAwait(false);
        await CollectAsync(failures, () =>
        {
            LockRelease.Release(report.LockHandle);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return failures.AsReadOnly();
    }

    /// <summary>
    /// Closes only the agent connection after the shutdown plan already stopped the owner pipe and
    /// released the ownership record and the lock.
    /// </summary>
    /// <returns>The failures, which the caller reports beside the shutdown result.</returns>
    /// <exception cref="ArgumentNullException">The report is null.</exception>
    public async Task<IReadOnlyList<Exception>> ReleaseAgentAsync(SessionStartReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<Exception> failures = [];
        await CollectAsync(failures, () => AgentRelease.ReleaseAsync(report.Agent.Client)).ConfigureAwait(false);
        return failures.AsReadOnly();
    }

    private static async Task CollectAsync(List<Exception> failures, Func<ValueTask> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failures.Add(error);
        }
    }
}