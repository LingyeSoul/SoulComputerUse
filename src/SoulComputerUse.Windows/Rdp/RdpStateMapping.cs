using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>The connected-state numbers the remote desktop client reports.</summary>
public enum RdpConnectedState
{
    /// <summary>Not connected.</summary>
    Idle = 0,

    /// <summary>The transport is connected and the session is being established.</summary>
    Connecting = 2,

    /// <summary>The session exists but is waiting for the operator to log on at its logon screen.</summary>
    AwaitingLogon = 3,

    /// <summary>The session is live.</summary>
    Connected = 1,
}

/// <summary>The four facts that together mean the remote desktop is really on screen.</summary>
public sealed record DesktopReadinessFacts(
    bool OperatorIsLoggedOn,
    bool HasLogonName,
    double SecondsSinceLogon,
    int ConsecutiveBrightFrames);

/// <summary>
/// Translates the client's connected-state numbers into session states, and decides when the remote
/// desktop is really usable.
/// </summary>
/// <remarks>
/// The logon screen is its own state on purpose. It is the only moment the product can prove that the
/// operator logged on themselves, so a step that jumps from connecting straight to ready means the
/// logon was skipped by a leftover session, and that has to fail rather than pass quietly.
/// </remarks>
public static class RdpStateMapping
{
    /// <summary>How long after the logon to keep waiting before the desktop counts as settled.</summary>
    public const double SettleSecondsAfterLogon = 2.0;

    /// <summary>How many bright frames in a row are needed before the desktop counts as settled.</summary>
    public const int SettleBrightFramesRequired = 3;

    /// <summary>Maps a client connected-state number to the session state it means.</summary>
    public static SessionState ToSessionState(RdpConnectedState connectedState)
    {
        return connectedState switch
        {
            RdpConnectedState.Idle => SessionState.Disconnected,
            RdpConnectedState.Connecting => SessionState.Connecting,
            RdpConnectedState.AwaitingLogon => SessionState.AwaitingCredentials,
            RdpConnectedState.Connected => SessionState.Ready,
            _ => throw new ArgumentOutOfRangeException(
                nameof(connectedState),
                connectedState,
                "Unknown client connected state."),
        };
    }

    /// <summary>Parses the raw number the client reports, refusing values it never produces.</summary>
    public static RdpConnectedState FromRawValue(int rawValue)
    {
        return rawValue switch
        {
            0 => RdpConnectedState.Idle,
            1 => RdpConnectedState.Connected,
            2 => RdpConnectedState.Connecting,
            3 => RdpConnectedState.AwaitingLogon,
            _ => throw new ArgumentOutOfRangeException(
                nameof(rawValue),
                rawValue,
                "The client connected state must be 0, 1, 2 or 3."),
        };
    }

    /// <summary>
    /// Decides whether the desktop is settled. A bright picture alone is not enough: the welcome screen
    /// is bright too, so the logon must be proven and the picture must stay bright for several frames.
    /// </summary>
    public static bool IsSettled(DesktopReadinessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.OperatorIsLoggedOn
            && facts.HasLogonName
            && facts.SecondsSinceLogon >= SettleSecondsAfterLogon
            && facts.ConsecutiveBrightFrames >= SettleBrightFramesRequired;
    }

    /// <summary>
    /// Checks the observed state chain and refuses one that reached the ready state without ever
    /// showing the logon step. A chain that starts at the logon step is the only acceptable way to
    /// ready, because anything else means the operator was never asked to log on.
    /// </summary>
    public static void EnsureLogonWasObserved(IReadOnlyList<SessionState> observedStates)
    {
        ArgumentNullException.ThrowIfNull(observedStates);

        bool sawLogon = false;
        foreach (SessionState state in observedStates)
        {
            if (state == SessionState.AwaitingCredentials)
            {
                sawLogon = true;
            }

            if (state == SessionState.Ready && !sawLogon)
            {
                throw new InvalidOperationException(
                    "The session reached ready without ever entering awaiting-credentials, so the logon step was skipped, most likely because a leftover child session answered the connection. That skips the operator's own logon and the run is refused.");
            }
        }
    }
}
