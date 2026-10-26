namespace SoulComputerUse.Windows.Rdp.Launch;

/// <summary>
/// The two alternate-shell properties this product writes on the remote desktop control, as a surface
/// that a test can substitute for the real control.
/// </summary>
/// <remarks>
/// Only the properties are modelled. Whether the session's shell is maximised is not a decision this
/// product makes, so no member stands for it: the interface stays at what is actually written.
/// </remarks>
public interface IRdpLaunchSurface
{
    /// <summary>The program the session runs instead of the usual shell.</summary>
    string StartProgram { get; set; }

    /// <summary>The directory the alternate shell starts in.</summary>
    string WorkingDirectory { get; set; }
}
