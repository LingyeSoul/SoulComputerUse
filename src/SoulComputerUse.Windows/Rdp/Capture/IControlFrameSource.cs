namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// One source of PNG frames taken from the hosted remote desktop control. The host uses it while the
/// child-session agent is absent or unusable, which is exactly when the picture only exists in the
/// control window.
/// </summary>
public interface IControlFrameSource
{
    /// <summary>Captures one frame of the control window as encoded PNG bytes, without writing them to disk.</summary>
    /// <param name="controlHandle">The control window to read.</param>
    /// <returns>A PNG image of the control's window rectangle.</returns>
    byte[] CapturePng(nint controlHandle);
}
