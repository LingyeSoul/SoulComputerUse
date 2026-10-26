using System;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.Rdp.SessionLaunch;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// Opens the child-session connection that shows the Windows logon screen, through the product's single
/// connection entry point and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This type never connects on its own. It asks <see cref="ISessionConnector"/> to judge the gate and then
/// to open the connection; the production connector calls <c>ChildSessionConnection.Connect</c>, which
/// carries the leftover-child-session sweep, the machine-state check and the state mapping. A second route
/// that asked the control to connect directly would skip the sweep and could present a session that never
/// asked the operator to log on.
/// </para>
/// <para>
/// The gate is a separate call on purpose: the decision is taken against the machine immediately before the
/// connection, so a refusal stops the run without touching the control. A gate that refuses by throwing
/// therefore leaves the control untouched, which is exactly what a start that must not connect needs.
/// </para>
/// </remarks>
public sealed class SessionConnectionOpener : ISessionConnectionOpener
{
    /// <summary>The largest desktop side a connection may request.</summary>
    public const int MaximumDesktopSide = 8192;

    private readonly IRdpControlSurface _controlSurface;
    private readonly ISessionConnector _connector;
    private readonly int _desktopWidth;
    private readonly int _desktopHeight;

    /// <summary>Creates one opener over the hosted control and the product's connector.</summary>
    /// <exception cref="ArgumentNullException">The control surface or the connector is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A desktop side is not positive or exceeds <see cref="MaximumDesktopSide"/>.
    /// </exception>
    public SessionConnectionOpener(
        IRdpControlSurface controlSurface,
        ISessionConnector connector,
        int desktopWidth,
        int desktopHeight)
    {
        ArgumentNullException.ThrowIfNull(controlSurface);
        ArgumentNullException.ThrowIfNull(connector);

        if (desktopWidth <= 0 || desktopWidth > MaximumDesktopSide)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desktopWidth),
                desktopWidth,
                $"The desktop width must be positive and at most {MaximumDesktopSide}.");
        }

        if (desktopHeight <= 0 || desktopHeight > MaximumDesktopSide)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desktopHeight),
                desktopHeight,
                $"The desktop height must be positive and at most {MaximumDesktopSide}.");
        }

        _controlSurface = controlSurface;
        _connector = connector;
        _desktopWidth = desktopWidth;
        _desktopHeight = desktopHeight;
    }

    /// <summary>
    /// Judges the gate and then asks the connector to open the connection on the hosted control.
    /// </summary>
    /// <exception cref="InvalidOperationException">The gate refuses the connection, or the control refuses to open it.</exception>
    public void Open()
    {
        ConnectionGate gate = _connector.PrepareGate();
        _connector.Open(_controlSurface.Ocx, _desktopWidth, _desktopHeight, gate);
    }
}
