using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>
/// Reads and drives the desktop of the calling Windows session. Coordinates are native physical
/// pixels with the origin at the top-left corner of the virtual desktop.
/// </summary>
public sealed class DesktopSurface : IDesktopSurface
{
    private readonly ISendInput _sendInput;

    /// <summary>Creates a surface that injects input through user32 SendInput.</summary>
    public DesktopSurface()
        : this(new SendInputAdapter())
    {
    }

    /// <summary>Creates a surface with an injected input sink.</summary>
    /// <exception cref="ArgumentNullException">The sink is null.</exception>
    public DesktopSurface(ISendInput sendInput)
    {
        ArgumentNullException.ThrowIfNull(sendInput);
        _sendInput = sendInput;
    }

    /// <summary>Returns the bounds of the whole virtual desktop in native physical pixels.</summary>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The surface exposes one uniform instance API for screen, capture and input operations.")]
    public DesktopScreen GetScreen()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        return new DesktopScreen(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>Captures the whole virtual desktop as one PNG frame without writing it to disk.</summary>
    /// <exception cref="InvalidOperationException">The capture or the PNG encoding failed.</exception>
    public byte[] CapturePng()
    {
        DesktopScreen screen = GetScreen();
        using Bitmap bitmap = new(screen.Width, screen.Height, PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            try
            {
                graphics.CopyFromScreen(
                    screen.X,
                    screen.Y,
                    0,
                    0,
                    new Size(screen.Width, screen.Height),
                    CopyPixelOperation.SourceCopy);
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException(
                    $"Desktop capture failed with Win32 error {error.NativeErrorCode}.",
                    error);
            }
            catch (ExternalException error)
            {
                throw new InvalidOperationException(
                    $"Desktop capture failed with GDI+ error 0x{error.ErrorCode:X8}.",
                    error);
            }
        }

        using MemoryStream stream = new();
        try
        {
            bitmap.Save(stream, ImageFormat.Png);
        }
        catch (ExternalException error)
        {
            throw new InvalidOperationException(
                $"PNG encoding failed with GDI+ error 0x{error.ErrorCode:X8}.",
                error);
        }

        return stream.ToArray();
    }

    /// <summary>Moves the pointer to one native pixel coordinate.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The coordinate lies outside the virtual desktop.</exception>
    /// <exception cref="SessionOperationException">The system rejected the input.</exception>
    public void MovePointer(int x, int y)
    {
        DesktopScreen screen = GetScreen();
        INPUT move = DesktopInputPlanner.CreateMove(screen, x, y);
        Dispatch(new INPUT[] { move });
    }

    /// <summary>Moves the pointer and clicks one button the requested number of times.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The coordinate lies outside the virtual desktop, the button is unknown, or the count is outside 1..3.
    /// </exception>
    /// <exception cref="SessionOperationException">The system rejected the input.</exception>
    public void Click(int x, int y, MouseButton button, int count)
    {
        SessionToolArguments.RequireClickCount(count);

        DesktopScreen screen = GetScreen();
        INPUT[] inputs = new INPUT[1 + (count * 2)];
        inputs[0] = DesktopInputPlanner.CreateMove(screen, x, y);

        int index = 1;
        for (int repetition = 0; repetition < count; repetition++)
        {
            inputs[index] = DesktopInputPlanner.CreateButtonDown(button);
            index++;
            inputs[index] = DesktopInputPlanner.CreateButtonUp(button);
            index++;
        }

        Dispatch(inputs);
    }

    /// <summary>
    /// Moves the pointer and injects the raw Windows wheel deltas. A non-zero vertical delta is sent
    /// first, then a non-zero horizontal delta.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The coordinate lies outside the virtual desktop.</exception>
    /// <exception cref="SessionOperationException">The system rejected the input.</exception>
    public void Scroll(int x, int y, int deltaX, int deltaY)
    {
        DesktopScreen screen = GetScreen();
        int eventCount = 1;
        if (deltaY != 0)
        {
            eventCount++;
        }

        if (deltaX != 0)
        {
            eventCount++;
        }

        INPUT[] inputs = new INPUT[eventCount];
        inputs[0] = DesktopInputPlanner.CreateMove(screen, x, y);

        int index = 1;
        if (deltaY != 0)
        {
            inputs[index] = DesktopInputPlanner.CreateVerticalWheel(deltaY);
            index++;
        }

        if (deltaX != 0)
        {
            inputs[index] = DesktopInputPlanner.CreateHorizontalWheel(deltaX);
        }

        Dispatch(inputs);
    }

    /// <summary>Types text verbatim, one UTF-16 code unit per Unicode event pair.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="SessionOperationException">The system rejected the input.</exception>
    public void TypeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return;
        }

        INPUT[] inputs = new INPUT[text.Length * 2];
        int index = 0;
        for (int characterIndex = 0; characterIndex < text.Length; characterIndex++)
        {
            char codeUnit = text[characterIndex];
            inputs[index] = DesktopInputPlanner.CreateUnicodeDown(codeUnit);
            index++;
            inputs[index] = DesktopInputPlanner.CreateUnicodeUp(codeUnit);
            index++;
        }

        Dispatch(inputs);
    }

    /// <summary>Presses every key in order, then releases them in reverse order.</summary>
    /// <exception cref="ArgumentNullException">The key list is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The key list is empty, holds more than five keys, or holds an unknown key.</exception>
    /// <exception cref="SessionOperationException">The system rejected the input.</exception>
    public void PressKeys(IReadOnlyList<SessionKey> keys)
    {
        SessionToolArguments.RequireKeys(keys);

        INPUT[] inputs = new INPUT[keys.Count * 2];
        int index = 0;
        for (int keyIndex = 0; keyIndex < keys.Count; keyIndex++)
        {
            inputs[index] = DesktopInputPlanner.CreateKeyDown(keys[keyIndex]);
            index++;
        }

        for (int keyIndex = keys.Count - 1; keyIndex >= 0; keyIndex--)
        {
            inputs[index] = DesktopInputPlanner.CreateKeyUp(keys[keyIndex]);
            index++;
        }

        Dispatch(inputs);
    }

    private void Dispatch(INPUT[] inputs)
    {
        uint accepted = _sendInput.Send(inputs);
        if (accepted != (uint)inputs.Length)
        {
            throw new SessionOperationException(SessionErrorCode.AgentError, "input-rejected", true);
        }
    }
}