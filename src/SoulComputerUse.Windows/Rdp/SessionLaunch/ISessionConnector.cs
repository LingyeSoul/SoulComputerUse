using System;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The connection half of the launch path: judge whether a connection may be made at all, then ask the
/// control to connect.
/// </summary>
/// <remarks>
/// <para>
/// The production implementation must call <see cref="ChildSessionConnection.DecideGate"/> and
/// <see cref="ChildSessionConnection.Connect"/>. Those two are the product's only connection decision
/// and its only connection call: the leftover-child-session sweep, the machine-state check and the
/// child-session flag all live there, and a second route could skip any of them. This interface exists
/// so the launcher never names the control's connect call itself.
/// </para>
/// <para>
/// <see cref="PrepareGate"/> is separate from <see cref="Open"/> because the gate is a decision taken
/// against the machine just before the connection, while <see cref="Open"/> is the effect that must
/// not be attempted when the decision refuses.
/// </para>
/// </remarks>
public interface ISessionConnector
{
    /// <summary>
    /// Judges whether a connection may be started: machine state, policy values and leftover child
    /// sessions. Returns the gate the connection call requires.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The machine refuses the connection, or a policy value that decides it cannot be read.
    /// </exception>
    ConnectionGate PrepareGate();

    /// <summary>Configures the control and starts the connection.</summary>
    /// <param name="ocx">The raw control object the ActiveX host attached.</param>
    /// <param name="width">The desktop width to request.</param>
    /// <param name="height">The desktop height to request.</param>
    /// <param name="gate">The gate <see cref="PrepareGate"/> decided; a leftover refuses the connection.</param>
    /// <exception cref="InvalidOperationException">The gate still reports a leftover child session.</exception>
    void Open(object ocx, int width, int height, ConnectionGate gate);
}
