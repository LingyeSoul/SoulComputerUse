using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>
/// Decides a tool call against the desktop before it reaches the surface. While the desktop is
/// locked, protected or non-interactive every input call is refused with <c>E_BLOCKED</c>, and a
/// session whose client is gone is refused with <c>E_DISCONNECTED</c>; the inner surface is never
/// reached either way, because a refused call must not leave a keystroke or a click on a UAC prompt
/// behind.
/// </summary>
/// <remarks>
/// <see cref="GetScreen"/> and <see cref="CapturePng"/> are deliberately not guarded. Section 1.3 of
/// the contract keeps the read-only tools available in the blocked state, and a frame is exactly what
/// the operator needs to judge a UAC prompt; reading bounds and capturing change nothing on the
/// desktop. Only input is gated, and the blocked state itself is reported through the status path.
/// </remarks>
public sealed class GuardedDesktopSurface : IDesktopSurface
{
    private readonly IDesktopSurface _inner;
    private readonly BlockedGuard _guard;

    /// <summary>Creates a surface that checks the desktop before forwarding capture and input.</summary>
    /// <exception cref="ArgumentNullException">The inner surface or the guard is null.</exception>
    public GuardedDesktopSurface(IDesktopSurface inner, BlockedGuard guard)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(guard);
        _inner = inner;
        _guard = guard;
    }

    /// <summary>Returns the inner surface's desktop bounds without a blocked check.</summary>
    public DesktopScreen GetScreen()
    {
        return _inner.GetScreen();
    }

    /// <summary>
    /// Captures one frame without a blocked check: section 1.3 keeps the read-only screenshot tool
    /// working while blocked, because the operator needs the frame to judge the prompt.
    /// </summary>
    public byte[] CapturePng()
    {
        return _inner.CapturePng();
    }

    /// <summary>Moves the pointer after the desktop check passes.</summary>
    /// <exception cref="SessionOperationException">The desktop cannot accept the call.</exception>
    public void MovePointer(int x, int y)
    {
        _guard.EnsureUsable();
        _inner.MovePointer(x, y);
    }

    /// <summary>Clicks a mouse button after the desktop check passes.</summary>
    /// <exception cref="SessionOperationException">The desktop cannot accept the call.</exception>
    public void Click(int x, int y, MouseButton button, int count)
    {
        _guard.EnsureUsable();
        _inner.Click(x, y, button, count);
    }

    /// <summary>Scrolls the wheel after the desktop check passes.</summary>
    /// <exception cref="SessionOperationException">The desktop cannot accept the call.</exception>
    public void Scroll(int x, int y, int deltaX, int deltaY)
    {
        _guard.EnsureUsable();
        _inner.Scroll(x, y, deltaX, deltaY);
    }

    /// <summary>Types text after the desktop check passes.</summary>
    /// <exception cref="SessionOperationException">The desktop cannot accept the call.</exception>
    public void TypeText(string text)
    {
        _guard.EnsureUsable();
        _inner.TypeText(text);
    }

    /// <summary>Presses a key combination after the desktop check passes.</summary>
    /// <exception cref="SessionOperationException">The desktop cannot accept the call.</exception>
    public void PressKeys(IReadOnlyList<SessionKey> keys)
    {
        _guard.EnsureUsable();
        _inner.PressKeys(keys);
    }
}
