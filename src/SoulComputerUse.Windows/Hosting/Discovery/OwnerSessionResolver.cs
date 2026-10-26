using System;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// Turns the two observations of <see cref="IOwnerProbe"/> into a usable session or an explicit
/// failure, following section 3 of the interface contract.
/// </summary>
/// <remarks>
/// A reachable owner takes precedence: the caller attaches to it as a client and starts nothing. A free
/// lock without an answer may start a session, but only when a starter was injected; without one this
/// version fails with <c>E_NOT_READY</c> instead of returning <see cref="UnavailableSessionClient"/>,
/// because a client that fails every call would look like a session that is merely disconnected. A
/// held lock without an answer fails with <c>E_LOCK_HELD</c> and never waits, retries or unbinds the
/// lock.
/// <para>
/// Every failure message is a fixed template without a path, account, session id or nonce.
/// </para>
/// </remarks>
public sealed class OwnerSessionResolver
{
    private const string LockHeldMessage =
        "Another process holds the single-instance lock but does not answer on the owner pipe, so no session was started. "
        + "Use Task Manager's Details tab to look for a running SoulComputerUse process, or close the existing session and retry.";

    private const string NoStarterMessage =
        "No session owner is running and this version cannot start a session on its own, so nothing was started. "
        + "Start the SoulComputerUse Host first, then retry.";

    private readonly IOwnerProbe _probe;
    private readonly Func<ISessionClient> _existingOwnerClientFactory;
    private readonly IOwnerSessionStarter? _sessionStarter;

    /// <summary>Creates the resolver from a probe, a client factory and an optional session starter.</summary>
    /// <param name="probe">Reads the lock and pipe observations.</param>
    /// <param name="existingOwnerClientFactory">Builds the client used when an owner answers.</param>
    /// <param name="sessionStarter">Starts a session when the lock is free, or null when this version cannot start one.</param>
    /// <exception cref="ArgumentNullException">The probe or the factory is null.</exception>
    public OwnerSessionResolver(
        IOwnerProbe probe,
        Func<ISessionClient> existingOwnerClientFactory,
        IOwnerSessionStarter? sessionStarter)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(existingOwnerClientFactory);
        _probe = probe;
        _existingOwnerClientFactory = existingOwnerClientFactory;
        _sessionStarter = sessionStarter;
    }

    /// <summary>Probes the machine and returns the session the caller should use.</summary>
    /// <exception cref="SessionOperationException">The lock is held without an answer, or no starter is available.</exception>
    /// <exception cref="InvalidOperationException">The lock or pipe state could not be observed.</exception>
    public async Task<ResolvedSession> ResolveAsync(CancellationToken cancellationToken)
    {
        bool lockExists = _probe.LockExists();
        bool ownerPipeReachable = await _probe.IsOwnerPipeReachableAsync(cancellationToken);
        OwnerDiscoveryDecision decision = OwnerDiscovery.Decide(lockExists, ownerPipeReachable);

        switch (decision)
        {
            case OwnerDiscoveryDecision.UseExistingOwner:
                return new ResolvedSession(_existingOwnerClientFactory(), null);

            case OwnerDiscoveryDecision.StartOwnSession:
                return await StartOwnSessionAsync(cancellationToken);

            case OwnerDiscoveryDecision.RefuseLockHeld:
                throw new SessionOperationException(
                    SessionErrorCode.LockHeld,
                    LockHeldMessage,
                    SessionErrorCatalog.IsRetryable(SessionErrorCode.LockHeld));

            default:
                throw new InvalidOperationException(
                    $"The owner discovery table produced the undefined decision value {decision}.");
        }
    }

    private async Task<ResolvedSession> StartOwnSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessionStarter is null)
        {
            throw new SessionOperationException(
                SessionErrorCode.NotReady,
                NoStarterMessage,
                SessionErrorCatalog.IsRetryable(SessionErrorCode.NotReady));
        }

        // The starter owns the lock and the session lifetime; discovery only wraps the client it returns.
        ISessionClient client = await _sessionStarter.StartAsync(cancellationToken);
        return new ResolvedSession(client, null);
    }
}
