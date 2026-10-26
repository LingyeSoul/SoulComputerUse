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
/// Runs the child-session agent: identity gate, claim gate, shell bootstrap, then one secured pipe
/// connection that serves the desktop tools. It returns a process exit code for every gate verdict.
/// </summary>
public static class AgentSessionRunner
{
    /// <summary>How long the agent waits to be claimed before it exits on its own.</summary>
    public static readonly TimeSpan UnclaimedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the agent waits for the owner record to appear before the claim gate fails.</summary>
    public static readonly TimeSpan OwnerRecordWaitTimeout = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan OwnerRecordPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Runs one agent with the parsed startup options.</summary>
    /// <exception cref="SessionOperationException">The pipe handshake or protocol failed.</exception>
    /// <exception cref="InvalidOperationException">A system gate or the shell bootstrap failed.</exception>
    public static async Task<int> RunAsync(AgentOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

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

        SessionOwnerRecord? record = ReadRecord(store);
        AgentClaimVerdict verdict = AgentClaimPolicy.Evaluate(record, options.SessionId, processSessionId);
        if (verdict == AgentClaimVerdict.NoOwnerRecord)
        {
            AgentLog.Write("information", "gate.awaiting_owner");
            record = await WaitForOwnerRecordAsync(store, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                AgentLog.Write("error", "gate.unclaimed");
                return AgentExitCode.Unclaimed;
            }

            verdict = AgentClaimPolicy.Evaluate(record, options.SessionId, processSessionId);
        }

        if (verdict is AgentClaimVerdict.NoOwnerRecord or AgentClaimVerdict.WrongSession)
        {
            AgentLog.Write("error", "gate.wrong_session");
            return AgentExitCode.WrongSession;
        }

        if (!WindowStationProbe.IsInteractiveDesktop())
        {
            AgentLog.Write("error", "gate.wrong_window_station");
            return AgentExitCode.WrongSession;
        }

        ShellBootstrap.StartExplorer();
        AgentLog.Write("information", "shell.started");

        DesktopSurface surface = new();
        DesktopSessionClient client = new(surface, options.SessionId, options.AuthTier, options.GateStrength);
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
}