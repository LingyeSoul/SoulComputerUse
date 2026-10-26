using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>A session that may or may not be one of this product's child sessions.</summary>
public sealed record SessionCandidate(int Id, string StationName, int State);

/// <summary>What the pre-connection sweep decided.</summary>
public enum LeftoverVerdict
{
    /// <summary>Nothing of ours is left over, so a connection presents a fresh logon screen.</summary>
    NoLeftover = 0,

    /// <summary>A leftover child session was found and must be logged off before connecting.</summary>
    LeftoverFound = 1,
}

/// <summary>What the pre-connection decision found.</summary>
public sealed record ConnectionGate(LeftoverVerdict LeftoverVerdict, IReadOnlyList<SessionCandidate> Leftovers);

/// <summary>
/// Decides, before anything connects, whether a child session of this product is already logged on.
/// </summary>
/// <remarks>
/// This is the gate's weak point, and the reason the sweep exists: a child session that is still logged
/// on answers a new connection without asking for a password, so the operator would silently receive a
/// session nobody authenticated for. A leftover therefore has to be logged off first, and if it cannot
/// be, the run must stop instead of connecting into an unguarded session.
/// </remarks>
public static class LeftoverChildSessionPolicy
{
    /// <summary>The service session, which must never be treated as one of ours.</summary>
    public const int ServiceSessionId = 0;

    /// <summary>The station-name suffix every child session has.</summary>
    private const string ChildStationSuffix = "#0";

    /// <summary>
    /// Finds child sessions of this product, and only those: a session is a candidate when it is not
    /// the service session, not the operator's console session, and its station name has the child
    /// session shape.
    /// </summary>
    public static IReadOnlyList<SessionCandidate> FindLeftovers(
        IReadOnlyList<SessionCandidate> sessions,
        int activeConsoleSessionId)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        List<SessionCandidate> leftovers = [];
        foreach (SessionCandidate session in sessions)
        {
            if (IsOneOfOurs(session, activeConsoleSessionId))
            {
                leftovers.Add(session);
            }
        }

        return leftovers;
    }

    /// <summary>Returns the verdict for a sweep.</summary>
    public static LeftoverVerdict Decide(IReadOnlyList<SessionCandidate> leftovers)
    {
        ArgumentNullException.ThrowIfNull(leftovers);
        return leftovers.Count == 0 ? LeftoverVerdict.NoLeftover : LeftoverVerdict.LeftoverFound;
    }

    /// <summary>
    /// Tells whether a session must be treated as a child session of this product. A child session's
    /// station ends with <c>#0</c>, while the console's station is <c>Console</c> and the service
    /// session's is <c>Services</c>. The console session id always wins over the station name: leaving
    /// one stray session behind is far cheaper than signing the operator out.
    /// </summary>
    public static bool IsOneOfOurs(SessionCandidate session, int activeConsoleSessionId)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Id == ServiceSessionId || session.Id == activeConsoleSessionId)
        {
            return false;
        }

        return session.StationName.EndsWith(ChildStationSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The refusal raised when a leftover cannot be removed. Connecting anyway would hand over a session
    /// that skips the operator's logon, so this is a manual-intervention case, never a warning.
    /// </summary>
    public static InvalidOperationException ManualInterventionRequired(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new InvalidOperationException(
            $"manual_intervention_required: {reason} A leftover child session answers a new connection without asking the operator to log on, so the gate would be bypassed.");
    }
}

/// <summary>
/// The one entry point that connects the product to a child session.
/// </summary>
/// <remarks>
/// It exists so that no other code can open a connection: the sweep for a leftover child session, the
/// machine-state check and the state mapping all live on this path, and a second path could skip any of
/// them. The gate is only real if every connection goes through here.
/// </remarks>
public static class ChildSessionConnection
{
    /// <summary>Decides whether a connection may be started at all, before anything is created.</summary>
    public static ConnectionGate DecideGate(
        IReadOnlyList<SessionCandidate> sessions,
        int activeConsoleSessionId,
        bool childSessionsEnabled,
        object? denyTsConnections,
        object? denyChildConnections)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        // The machine state first: without it a connection could take over the operator's console.
        RdpExperimentPreconditions.Check(childSessionsEnabled, denyTsConnections, denyChildConnections);

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(sessions, activeConsoleSessionId);
        return new ConnectionGate(LeftoverChildSessionPolicy.Decide(leftovers), leftovers);
    }

    /// <summary>
    /// Refuses to go on when a leftover child session is still around. A leftover answers the connection
    /// with its own logon, so the operator would never be asked to log on.
    /// </summary>
    public static void EnsureNoLeftover(ConnectionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        if (gate.LeftoverVerdict == LeftoverVerdict.NoLeftover)
        {
            return;
        }

        throw LeftoverChildSessionPolicy.ManualInterventionRequired(
            $"{gate.Leftovers.Count} child session(s) of this product are still logged on and could not be removed before connecting.");
    }

    /// <summary>
    /// Turns one observed client state into the session state the caption shows, and checks the chain so
    /// far. That check is the guarantee the operator was actually asked to log on.
    /// </summary>
    public static SessionState Observe(RdpConnectedState connectedState, IList<SessionState> chainSoFar)
    {
        ArgumentNullException.ThrowIfNull(chainSoFar);

        SessionState state = RdpStateMapping.ToSessionState(connectedState);
        chainSoFar.Add(state);
        RdpStateMapping.EnsureLogonWasObserved(chainSoFar.AsReadOnly());
        return state;
    }

    /// <summary>
    /// Configures the hosted control and starts the connection. This is the only place in the product
    /// that asks the control to connect; every other path has to call it, so the sweep above cannot be
    /// skipped by a second caller.
    /// </summary>
    /// <param name="ocx">The raw control object the ActiveX host attached.</param>
    /// <param name="server">The server to connect to.</param>
    /// <param name="desktopWidth">The remote desktop width to request.</param>
    /// <param name="desktopHeight">The remote desktop height to request.</param>
    /// <param name="gate">What the pre-connection sweep decided; a leftover refuses the connection.</param>
    public static void Connect(
        object ocx,
        string server,
        int desktopWidth,
        int desktopHeight,
        ConnectionGate gate)
    {
        ArgumentNullException.ThrowIfNull(ocx);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        if (desktopWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desktopWidth), desktopWidth, "The desktop width must be positive.");
        }

        if (desktopHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desktopHeight), desktopHeight, "The desktop height must be positive.");
        }

        ArgumentNullException.ThrowIfNull(gate);

        // The gate comes first: connecting into a session that is already logged on would skip the
        // operator's logon, and nothing above this line may have connected on its own.
        EnsureNoLeftover(gate);

        dynamic client = ocx;
        client.Server = server;
        client.DesktopWidth = desktopWidth;
        client.DesktopHeight = desktopHeight;
        client.AdvancedSettings2.EnableCredSspSupport = true;

        // A child-session connection is the only kind this product makes; a flag that did not stick
        // would take over the operator's own session instead.
        RdpExtendedSettings.SetBooleanAndVerify(ocx, RdpExtendedSettings.ConnectToChildSessionProperty, true);

        client.Connect();
    }
}
