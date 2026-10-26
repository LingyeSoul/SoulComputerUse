namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The hosted remote desktop control as the launch path needs it: the raw control object that receives
/// the alternate-shell properties and starts the connection, and the window handle the product window
/// uses to display it.
/// </summary>
/// <remarks>
/// The surface is a port so a test can stand in for the ActiveX control without creating a real one.
/// Production passes <see cref="RdpControlSurface"/>, which owns the <see cref="RdpControlHost"/>.
/// </remarks>
public interface IRdpControlSurface
{
    /// <summary>The raw control object the ActiveX host attached.</summary>
    object Ocx { get; }

    /// <summary>The window handle of the hosted control.</summary>
    nint WindowHandle { get; }
}
