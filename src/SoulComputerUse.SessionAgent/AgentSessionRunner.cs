using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Windows.Desktop;
using SoulComputerUse.Windows.Hosting;
using SoulComputerUse.Windows.Ipc.Agent;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// Runs the child-session agent: identity gate, claim gate, window station gate, credential gate
/// detection, then one secured pipe connection that serves the desktop tools. It returns a process
/// exit code for every gate verdict.
/// </summary>
/// <remarks>
/// <para>
/// The three self-exit gates of section 3.2 and the credential tier of section 7.2 are decided here,
/// in that order, before the pipe serves a single call. Every gate fails closed: a fact that cannot
/// be read ends the agent instead of being replaced by an assumption, because an agent that guessed
/// its location or its gate would serve the wrong desktop under a false security claim.
/// </para>
/// <para>
/// The credential tier is detected by this process, never taken from the command line. A launcher
/// declaration only has to agree with the detection; a disagreement ends the agent, so a misreported
/// gate can never reach <c>get_status</c>.
/// </para>
/// </remarks>
public static class AgentSessionRunner
{
    /// <summary>How long the agent waits to be claimed before it exits on its own.</summary>
    public static readonly TimeSpan UnclaimedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the agent waits for the owner record to appear before the claim gate fails.</summary>
    public static readonly TimeSpan OwnerRecordWaitTimeout = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan OwnerRecordPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Runs one agent with the parsed startup options.</summary>
    /// <param name="options">The parsed startup configuration, including any optional declared gate values.</param>
    /// <param name="helloFacts">The Windows Hello facts to detect the gate tier from.</param>
    /// <param name="cancellationToken">Cancels the run; an observed cancellation is the caller's.</param>
    /// <exception cref="SessionOperationException">The pipe handshake or protocol failed.</exception>
    /// <exception cref="InvalidOperationException">A system gate or the tier detection failed.</exception>
    /// <exception cref="ArgumentException">A declared gate value contradicts the detected tier.</exception>
    public static async Task<int> RunAsync(
        AgentOptions options,
        AgentHelloFacts helloFacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(helloFacts);

        int processSessionId = WtsSessionProbe.GetCurrentProcessSessionId();
        int consoleSessionId = WtsSessionProbe.GetActiveConsoleSessionId();
        if (AgentSessionGate.Evaluate(processSessionId, consoleSessionId) == AgentSessionVerdict.ConsoleSession)
        {
            AgentLog.Write("error", "gate.console_session");
            return AgentExitCode.ConsoleSession;
        }

        SessionOwnerStore store;
        try
        {
            store = SessionOwnerStore.ForCurrentUser();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AgentLog.Write("error", "gate.owner_store_unavailable");
            throw new InvalidOperationException("The per-user ownership record store could not be resolved, so the agent cannot prove it was claimed.", error);
        }

        int claimExitCode = await ApplyClaimGateAsync(store, options.SessionId, processSessionId, cancellationToken).ConfigureAwait(false);
        if (claimExitCode != AgentExitCode.Success)
        {
            return claimExitCode;
        }

        if (!WindowStationProbe.IsInteractiveDesktop())
        {
            AgentLog.Write("error", "gate.wrong_window_station");
            return AgentExitCode.WrongSession;
        }

        AgentAuthTier detected;
        try
        {
            detected = AgentAuthTierDetector.DetectFailClosed(
                helloFacts.AvailableAndEnrolled,
                helloFacts.HardwarePresentButNotEnrolled);
        }
        catch (InvalidOperationException)
        {
            AgentLog.Write("critical", "gate.auth_tier_probe_failed");
            throw;
        }

        if (detected.RefusesStartup)
        {
            // The refusal tier is a startup refusal, not a degraded session: the contract forbids
            // running without a gate, and the agent has no way to add one.
            AgentLog.Write("error", "gate.auth_tier_none");
            return AgentExitCode.AuthTierNone;
        }

        try
        {
            AgentCommandLine.EnsureDeclarationMatches(options, detected);
        }
        catch (AgentGateDeclarationException)
        {
            AgentLog.Write("error", "gate.auth_tier_mismatch");
            throw;
        }

        // The child session's desktop shell is already running: the Run entry that starts this agent
        // is read by explorer itself at logon (see ChildSessionAgentBootstrap). Launching explorer.exe
        // here would open a File Explorer window on every start.
        DesktopSurface surface = new();
        BlockedGuard blockedGuard = new();
        GuardedDesktopSurface guarded = new(surface, blockedGuard);
        DesktopSessionClient client = new(
            guarded,
            options.SessionId,
            detected.Tier,
            detected.RequireStrength(),
            detected.HelloHint,
            blockedGuard.Observe);
        SessionClientToolHandler handler = new(client);
        await using AgentPipeServer server = new(
            AgentPipeNames.ForSession(options.SessionId),
            options.Nonce,
            handler,
            AgentServerTimings.Contract);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource unclaimed = new();

        Task run = server.RunAsync(lifetime.Token);
        await server.WaitUntilListeningAsync(lifetime.Token);

        Task claimed = server.WaitUntilClaimedAsync(lifetime.Token);
        Task unclaimedDelay = Task.Delay(UnclaimedTimeout, unclaimed.Token);
        Task first = await Task.WhenAny(claimed, run, unclaimedDelay).ConfigureAwait(false);
        unclaimed.Cancel();

        if (ReferenceEquals(first, unclaimedDelay))
        {
            lifetime.Cancel();
            await server.DisposeAsync().ConfigureAwait(false);
            AgentLog.Write("error", "gate.unclaimed");
            return AgentExitCode.Unclaimed;
        }

        if (ReferenceEquals(first, run))
        {
            await run.ConfigureAwait(false);
            AgentLog.Write("information", "pipe.disconnected");
            return AgentExitCode.Success;
        }

        AgentLog.Write("information", "pipe.claimed");
        await run.ConfigureAwait(false);
        AgentLog.Write("information", "pipe.disconnected");
        return AgentExitCode.Success;
    }

    /// <summary>
    /// Runs the claim gate. When no ownership record exists yet the agent waits for the host to write
    /// one, because the alternate shell may start before the owner finishes writing. A wait that
    /// times out is the only path that reports the unclaimed verdict from this gate.
    /// </summary>
    /// <returns><see cref="AgentExitCode.Success"/> when the gate passed, otherwise the exit code.</returns>
    private static async Task<int> ApplyClaimGateAsync(
        SessionOwnerStore store,
        string expectedSessionId,
        int processSessionId,
        CancellationToken cancellationToken)
    {
        SessionOwnerRecord? record = ReadRecord(store);
        AgentClaimDecision decision = AgentClaimPolicy.Decide(record, expectedSessionId, processSessionId);
        if (decision.Verdict == AgentClaimVerdict.NoOwnerRecord)
        {
            // The record does not exist yet. This is the documented launch order, not a failed claim,
            // so the wait is bounded by the contract's owner-record timeout instead of ending at once.
            AgentLog.Write("information", AgentClaimLogEvents.OwnerRecordMissingWaiting);
            record = await WaitForOwnerRecordAsync(store, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                // No record appeared within the timeout: no owner ever launched this agent.
                AgentLog.Write("error", AgentClaimLogEvents.OwnerRecordWaitExpired);
                return AgentExitCode.Unclaimed;
            }

            decision = AgentClaimPolicy.Decide(record, expectedSessionId, processSessionId);
        }

        if (decision.Verdict == AgentClaimVerdict.WrongSession)
        {
            // A record exists but does not bind this agent. This is a refusal, not a wait: waiting
            // again would not change a record that already names something else.
            AgentLog.Write("error", AgentClaimLogEvents.ForRefusal(decision.Reason));
            return AgentExitCode.WrongSession;
        }

        // A pending host claim (zero child session id), a bound record and a claimed record all
        // continue to the pipe, where the nonce handshake is the final claim.
        AgentLog.Write("information", decision.Verdict == AgentClaimVerdict.PendingHostClaim
            ? AgentClaimLogEvents.HostClaimPending
            : AgentClaimLogEvents.Claimed);
        return AgentExitCode.Success;
    }

    /// <summary>
    /// Reads the ownership record, reporting an unreadable record as an explicit start failure. A
    /// record that exists but cannot be read must never be treated as "no record", because the claim
    /// gate would then wait for an owner that is already there.
    /// </summary>
    private static SessionOwnerRecord? ReadRecord(SessionOwnerStore store)
    {
        try
        {
            return store.Read();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AgentLog.Write("error", "gate.owner_record_unreadable");
            throw new InvalidOperationException("The ownership record could not be read, so the agent cannot prove it was claimed.", error);
        }
    }

    private static async Task<SessionOwnerRecord?> WaitForOwnerRecordAsync(
        SessionOwnerStore store,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + OwnerRecordWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionOwnerRecord? record = ReadRecord(store);
            if (record is not null)
            {
                return record;
            }

            await Task.Delay(OwnerRecordPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}

/// <summary>The process exit codes the agent uses for its gates.</summary>
public static class AgentExitCode
{
    /// <summary>The agent served normally and the host disconnected.</summary>
    public const int Success = 0;

    /// <summary>The command line was invalid.</summary>
    public const int InvalidArguments = 2;

    /// <summary>The agent landed in the operator's console session.</summary>
    public const int ConsoleSession = 3;

    /// <summary>The pipe, handshake or a system dependency failed.</summary>
    public const int StartFailure = 4;

    /// <summary>No host claimed the agent within the contract's timeout.</summary>
    public const int Unclaimed = 5;

    /// <summary>The ownership record does not bind this agent to its child session.</summary>
    public const int WrongSession = 6;

    /// <summary>
    /// The detected credential gate tier refuses startup: the machine has neither an enrolled Hello
    /// nor an interactive logon, so section 7.2 forbids running.
    /// </summary>
    public const int AuthTierNone = 7;
}