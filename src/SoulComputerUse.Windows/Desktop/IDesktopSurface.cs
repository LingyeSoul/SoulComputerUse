using System.Collections.Generic;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>
/// The desktop operations a child-session agent exposes: one frame, pointer input and keyboard input.
/// Coordinates are native physical pixels with the origin at the top-left corner of the desktop.
/// </summary>
public interface IDesktopSurface
{
    /// <summary>Returns the current desktop bounds.</summary>
    DesktopScreen GetScreen();

    /// <summary>Captures the whole desktop as one PNG frame.</summary>
    byte[] CapturePng();

    /// <summary>Moves the pointer.</summary>
    void MovePointer(int x, int y);

    /// <summary>Clicks one mouse button the requested number of times.</summary>
    void Click(int x, int y, MouseButton button, int count);

    /// <summary>Scrolls the wheel by the raw Windows deltas.</summary>
    void Scroll(int x, int y, int deltaX, int deltaY);

    /// <summary>Types text verbatim.</summary>
    void TypeText(string text);

    /// <summary>Presses a key combination.</summary>
    void PressKeys(IReadOnlyList<SessionKey> keys);
}