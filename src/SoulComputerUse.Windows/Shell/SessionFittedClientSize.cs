using System;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// The client size a product window needs so the letterboxed desktop picture fills the whole area below
/// the caption with no black bars.
/// </summary>
/// <remarks>
/// <para>
/// The picture rectangle keeps a resize border free on every side and the caption strip on top, and the
/// desktop picture is fitted into what remains while preserving its aspect ratio. Black bars appear
/// exactly when that remaining rectangle's ratio differs from the session's, so the window has to be
/// sized from the session: derive the picture's size from the session ratio at the requested share of
/// its native size, and add the caption and border back. The fitted picture then equals the remaining
/// rectangle and the letterbox has nothing left to fill.
/// </para>
/// <para>
/// The rounding on the derived height is the one unavoidable error: it leaves at most one physical row
/// of black, which no smaller honest size can avoid.
/// </para>
/// </remarks>
public static class SessionFittedClientSize
{
    /// <summary>
    /// Computes the client size whose picture area has the session's aspect ratio and fits the work
    /// area.
    /// </summary>
    /// <param name="sessionWidth">The session desktop width in physical pixels.</param>
    /// <param name="sessionHeight">The session desktop height in physical pixels.</param>
    /// <param name="captionHeight">The caption strip height in physical pixels.</param>
    /// <param name="borderPixels">The resize border kept free on every side, in physical pixels.</param>
    /// <param name="workWidth">The work-area width available to the window, in physical pixels.</param>
    /// <param name="workHeight">The work-area height available to the window, in physical pixels.</param>
    /// <param name="targetScalePercent">
    /// What share of the session's native width the picture aims for, in percent. The work area can
    /// still cap it, but the aim is what keeps a default window from covering the operator's screen.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A session side, the caption height or the border is not positive, the target share is not in
    /// 1..100, or the work area cannot hold a window with that caption and border.
    /// </exception>
    public static (int Width, int Height) Fit(
        int sessionWidth,
        int sessionHeight,
        int captionHeight,
        int borderPixels,
        int workWidth,
        int workHeight,
        int targetScalePercent = 100)
    {
        if (sessionWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionWidth), sessionWidth, "The session width must be positive.");
        }

        if (sessionHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionHeight), sessionHeight, "The session height must be positive.");
        }

        if (captionHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captionHeight), captionHeight, "The caption height must be positive.");
        }

        if (borderPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(borderPixels), borderPixels, "The border must be positive.");
        }

        if (targetScalePercent < 1 || targetScalePercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetScalePercent),
                targetScalePercent,
                "The target share of the session width must be between 1 and 100 percent.");
        }

        int availableWidth = workWidth - (borderPixels * 2);
        int availableHeight = workHeight - captionHeight - borderPixels;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workWidth),
                workWidth,
                $"The work area {workWidth}x{workHeight} cannot hold a window with a caption of {captionHeight} and a border of {borderPixels}.");
        }

        // The width is capped by the work area directly, by the height limit through the session ratio,
        // and by the requested share of the session's native width; the smallest of the three is the
        // picture that both fits whole and honours the default window size.
        int pictureWidth = Math.Min(
            availableWidth,
            Math.Min(
                (int)((long)availableHeight * sessionWidth / sessionHeight),
                (int)((long)sessionWidth * targetScalePercent / 100)));
        if (pictureWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workHeight), workHeight, "The work area is too small for a session of this ratio.");
        }

        int pictureHeight = (int)Math.Round((double)pictureWidth * sessionHeight / sessionWidth);
        return (pictureWidth + (borderPixels * 2), captionHeight + pictureHeight + borderPixels);
    }
}
