using System;

namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// Pure frame statistics on a raw BGRA buffer. The mean brightness is the evidence that separates a
/// painted control surface from an empty one, so the capture can decide whether a printed frame is
/// usable without touching a device context itself.
/// </summary>
public static class FrameBrightness
{
    /// <summary>
    /// Returns the mean of the three colour channels of one BGRA frame, sampled every
    /// <paramref name="step"/> pixels along both axes. The result lies in 0..255; 0 means a black
    /// frame, which is what a surface that never painted reports.
    /// </summary>
    /// <param name="bgra">Top-down BGRA pixels, four bytes per pixel, at least width * height * 4 bytes.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="step">Sampling step in pixels; 1 reads every pixel.</param>
    /// <returns>The mean brightness in 0..255.</returns>
    /// <exception cref="ArgumentNullException">The buffer is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width, height or step is not positive.</exception>
    /// <exception cref="ArgumentException">The buffer is shorter than the frame requires.</exception>
    public static int Mean(byte[] bgra, int width, int height, int step)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The frame width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The frame height must be positive.");
        }

        if (step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "The sampling step must be positive.");
        }

        // A long stride keeps the size check honest for sizes an int-indexed array could never hold.
        long stride = (long)width * 4;
        long requiredBytes = stride * height;
        if (bgra.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"A {width}x{height} BGRA frame needs at least {requiredBytes} bytes, but the buffer holds {bgra.Length}.",
                nameof(bgra));
        }

        int rowStride = (int)stride;
        long blue = 0;
        long green = 0;
        long red = 0;
        long samples = 0;
        for (int y = 0; y < height; y += step)
        {
            int rowOffset = y * rowStride;
            for (int x = 0; x < width; x += step)
            {
                int offset = rowOffset + (x * 4);
                blue += bgra[offset];
                green += bgra[offset + 1];
                red += bgra[offset + 2];
                samples++;
            }
        }

        long channelTotal = blue + green + red;
        return (int)(channelTotal / (samples * 3));
    }
}
