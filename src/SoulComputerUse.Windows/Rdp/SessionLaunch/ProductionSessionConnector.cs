using System;
using System.Collections.Generic;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The production connector: it reads the machine state and the session table for the gate, then opens
/// the connection through the product's single connection entry point.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PrepareGate"/> delegates to <see cref="ChildSessionConnection.DecideGate"/>, so the child
/// session switch, both policy values and the leftover child sessions are judged exactly as the contract
/// states, by the same code the connection entry point enforces again.
/// </para>
/// <para>
/// <see cref="Open"/> delegates to <see cref="ChildSessionConnection.Connect"/>. This file is the only
/// place outside that entry point that names it, and it never asks the control to connect itself: the
/// sweep, the child-session flag and the policy checks stay on the one path that owns them.
/// </para>
/// <para>
/// The server name is the local host, because the only session this product opens is a child session of
/// this machine. It is not taken from an argument: a caller-provided server could point the control at
/// another machine, which the product has no use for.
/// </para>
/// </remarks>
public sealed class ProductionSessionConnector : ISessionConnector
{
    /// <summary>The server a child-session connection names: this machine.</summary>
    public const string LocalServer = "localhost";

    private readonly IWtsSessionTable _sessionTable;

    /// <summary>Creates the connector over the machine's real session table.</summary>
    public ProductionSessionConnector()
        : this(new WtsSessionTable())
    {
    }

    /// <summary>Creates the connector over an injected session table.</summary>
    /// <exception cref="ArgumentNullException">The session table is null.</exception>
    public ProductionSessionConnector(IWtsSessionTable sessionTable)
    {
        ArgumentNullException.ThrowIfNull(sessionTable);
        _sessionTable = sessionTable;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This gate is a re-check in front of the connection, so it holds no recorded child session id: the
    /// startup sweep, which does hold the taken-over record's id, has already run. The account this
    /// process runs as is part of the evidence, read from the session table, because a child session of
    /// this product always runs as that account and nothing else may be signed off.
    /// </remarks>
    public ConnectionGate PrepareGate()
    {
        int consoleSessionId = _sessionTable.ActiveConsoleSessionId();
        List<SessionCandidate> candidates = [];
        foreach (WtsSessionEntry entry in _sessionTable.Read())
        {
            candidates.Add(new SessionCandidate(entry.Id, entry.StationName, entry.State, entry.AccountName));
        }

        object? denyTsConnections = RdpExperimentPreconditions.ReadPolicyValue(
            ChildSessionsPolicy.RdpInboundValueName);
        object? denyChildConnections = RdpExperimentPreconditions.ReadPolicyValue(
            ChildSessionsPolicy.ChildConnectionsValueName);
        bool childSessionsEnabled = ChildSessionsProbe.IsEnabled();

        return ChildSessionConnection.DecideGate(
            candidates,
            consoleSessionId,
            childSessionsEnabled,
            denyTsConnections,
            denyChildConnections,
            new LeftoverEvidence(0, _sessionTable.CurrentSessionAccountName()));
    }

    /// <inheritdoc />
    public void Open(object ocx, int width, int height, ConnectionGate gate)
    {
        ChildSessionConnection.Connect(ocx, LocalServer, width, height, gate);
    }
}
