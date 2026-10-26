using System;
using System.Drawing;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// Where the desktop picture goes inside the client area. The picture is scaled to fit and centred;
/// whatever is left over is the letterbox, which is always pure black and never carries meaning.
/// </summary>
public static class DesktopPictureLayout
{
    /// <summary>
    /// Computes the destination rectangle of a session picture inside the client area.
    /// </summary>
    /// <param name="clientWidth">Client width in physical pixels.</param>
    /// <param name="clientHeight">Client height in physical pixels.</param>
    /// <param name="sessionWidth">Session picture width in pixels.</param>
    /// <param name="sessionHeight">Session picture height in pixels.</param>
    public static Rectangle Fit(int clientWidth, int clientHeight, int sessionWidth, int sessionHeight)
    {
        if (clientWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientWidth), clientWidth, "The client width must be positive.");
        }

        if (clientHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientHeight), clientHeight, "The client height must be positive.");
        }

        if (sessionWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionWidth), sessionWidth, "The session width must be positive.");
        }

        if (sessionHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionHeight), sessionHeight, "The session height must be positive.");
        }

        // Compare the two aspect ratios without dividing, so no rounding error can pick the wrong side.
        long clientProduct = (long)clientWidth * sessionHeight;
        long sessionProduct = (long)sessionWidth * clientHeight;

        if (clientProduct == sessionProduct)
        {
            return new Rectangle(0, 0, clientWidth, clientHeight);
        }

        if (clientProduct > sessionProduct)
        {
            // The client is wider than the picture, so height is the limiting side: fit the height and
            // leave black bars on both sides.
            int fittedWidth = (int)Math.Round((double)clientHeight * sessionWidth / sessionHeight, MidpointRounding.AwayFromZero);
            fittedWidth = Math.Clamp(fittedWidth, 1, clientWidth);
            return new Rectangle(
                (clientWidth - fittedWidth) / 2,
                0,
                fittedWidth,
                clientHeight);
        }

        // The client is taller than the picture, so width is the limiting side: fit the width and leave
        // black bars above and below.
        int fittedHeight = (int)Math.Round((double)clientWidth * sessionHeight / sessionWidth, MidpointRounding.AwayFromZero);
        fittedHeight = Math.Clamp(fittedHeight, 1, clientHeight);
        return new Rectangle(
            0,
            (clientHeight - fittedHeight) / 2,
            clientWidth,
            fittedHeight);
    }
}
