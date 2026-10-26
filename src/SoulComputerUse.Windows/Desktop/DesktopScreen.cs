using System;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>
/// One desktop rectangle in native physical pixels. The origin is the top-left corner, x grows to
/// the right and y grows down.
/// </summary>
public sealed record DesktopScreen
{
    /// <summary>Creates a screen rectangle.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive.</exception>
    public DesktopScreen(int x, int y, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The screen width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The screen height must be positive.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>The left edge in native physical pixels.</summary>
    public int X { get; }

    /// <summary>The top edge in native physical pixels.</summary>
    public int Y { get; }

    /// <summary>The width in native physical pixels.</summary>
    public int Width { get; }

    /// <summary>The height in native physical pixels.</summary>
    public int Height { get; }
}