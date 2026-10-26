using System;

namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// The policy a <see cref="ControlFrameCapture"/> applies to one frame: how much of a printed frame
/// counts as painted, how much overlap by a foreign window is tolerated, and whether the screen
/// region may be read when the control refuses to print itself.
/// </summary>
public sealed record ControlCaptureOptions
{
    /// <summary>The lowest value a mean frame brightness can take.</summary>
    public const int BrightnessMinimum = 0;

    /// <summary>The highest value a mean frame brightness can take.</summary>
    public const int BrightnessMaximum = 255;

    /// <summary>The lowest overlap ratio, meaning nothing of the target is covered.</summary>
    public const double OverlapMinimum = 0.0;

    /// <summary>The highest overlap ratio, meaning the foreground window covers the whole target.</summary>
    public const double OverlapMaximum = 1.0;

    /// <summary>Creates the capture policy.</summary>
    /// <param name="minimumBrightness">
    /// The mean brightness a printed frame must reach to count as painted, in 0..255.
    /// </param>
    /// <param name="maximumOverlap">
    /// The covered fraction of the control that is tolerated before a screen-region frame is refused,
    /// in 0..1. The comparison is strict: a ratio equal to this value is accepted.
    /// </param>
    /// <param name="allowScreenRegionFallback">
    /// Whether the visible screen may be read when the control does not paint itself.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The brightness is outside 0..255, or the overlap is outside 0..1 or not a number.
    /// </exception>
    public ControlCaptureOptions(int minimumBrightness, double maximumOverlap, bool allowScreenRegionFallback)
    {
        if (minimumBrightness < BrightnessMinimum || minimumBrightness > BrightnessMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumBrightness),
                minimumBrightness,
                "The minimum brightness must lie in 0..255.");
        }

        if (double.IsNaN(maximumOverlap) || maximumOverlap < OverlapMinimum || maximumOverlap > OverlapMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOverlap),
                maximumOverlap,
                "The maximum overlap must lie in 0..1.");
        }

        MinimumBrightness = minimumBrightness;
        MaximumOverlap = maximumOverlap;
        AllowScreenRegionFallback = allowScreenRegionFallback;
    }

    /// <summary>
    /// The production policy: a frame counts as painted above a near-black mean, a small overlap is
    /// tolerated so a tooltip cannot block the frame, and the screen region is an allowed fallback.
    /// </summary>
    public static ControlCaptureOptions Default { get; } = new(8, 0.02, true);

    /// <summary>The mean brightness a printed frame must reach to count as painted, in 0..255.</summary>
    public int MinimumBrightness { get; }

    /// <summary>The covered fraction of the control tolerated before a screen-region frame is refused.</summary>
    public double MaximumOverlap { get; }

    /// <summary>Whether the visible screen may be read when the control does not paint itself.</summary>
    public bool AllowScreenRegionFallback { get; }
}
