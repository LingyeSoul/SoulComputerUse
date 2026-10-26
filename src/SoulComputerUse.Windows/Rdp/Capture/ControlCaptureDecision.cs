using System;

namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// How one frame is obtained, as decided by <see cref="ControlCaptureDecision"/>.
/// </summary>
public enum ControlCapturePath
{
    /// <summary>The control painted itself into the memory device context, so those pixels are used.</summary>
    PrintWindow = 0,

    /// <summary>The visible screen is read over the control rectangle.</summary>
    ScreenRegion = 1,

    /// <summary>No trustworthy frame can be produced, so the capture must fail.</summary>
    Refuse = 2,
}

/// <summary>
/// Why a frame was refused. The values name the situation, not a window: no title, class name or
/// handle ever leaves the capture layer.
/// </summary>
public enum ControlCaptureRefusal
{
    /// <summary>A foreign window covers too much of the control to trust a screen-region frame.</summary>
    Occluded = 0,

    /// <summary>
    /// The control produced no painted frame and the policy forbids reading the visible screen, so
    /// there is nothing to fall back to.
    /// </summary>
    PrintWindowBlankAndScreenDisabled = 1,
}

/// <summary>
/// The outcome of one capture decision: which path to take, and, when the frame is refused, the
/// enumerated reason.
/// </summary>
public sealed record ControlCaptureDecision
{
    private ControlCaptureDecision(ControlCapturePath path, ControlCaptureRefusal? refusal)
    {
        Path = path;
        Refusal = refusal;
    }

    /// <summary>The path the capture must take.</summary>
    public ControlCapturePath Path { get; }

    /// <summary>The refusal reason, set exactly when <see cref="Path"/> is <see cref="ControlCapturePath.Refuse"/>.</summary>
    public ControlCaptureRefusal? Refusal { get; }

    /// <summary>The pixels the control printed are usable as they are.</summary>
    public static ControlCaptureDecision UsePrintWindow()
    {
        return new ControlCaptureDecision(ControlCapturePath.PrintWindow, null);
    }

    /// <summary>The printed pixels are unusable, but the visible screen may be read instead.</summary>
    public static ControlCaptureDecision UseScreenRegion()
    {
        return new ControlCaptureDecision(ControlCapturePath.ScreenRegion, null);
    }

    /// <summary>No trustworthy frame can be produced; the reason says which situation applies.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The reason is not a defined value.</exception>
    public static ControlCaptureDecision Refuse(ControlCaptureRefusal reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown capture refusal reason.");
        }

        return new ControlCaptureDecision(ControlCapturePath.Refuse, reason);
    }

    /// <summary>
    /// Decides one frame while the screen-region fallback is allowed. The printed frame wins whenever
    /// it succeeded and reached the brightness threshold; otherwise an overlap above the threshold
    /// refuses the frame, and only a small enough overlap falls back to the screen.
    /// </summary>
    /// <param name="printWindowSucceeded">Whether the control accepted the print request.</param>
    /// <param name="printedBrightness">The mean brightness of the printed frame, in 0..255.</param>
    /// <param name="minimumBrightness">The mean brightness a frame must reach to count as painted, in 0..255.</param>
    /// <param name="overlapRatio">The fraction of the control covered by a foreign window, in 0..1.</param>
    /// <param name="maximumOverlap">The tolerated covered fraction, in 0..1.</param>
    /// <returns>The path to take.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value lies outside its documented range.</exception>
    public static ControlCaptureDecision Decide(
        bool printWindowSucceeded,
        int printedBrightness,
        int minimumBrightness,
        double overlapRatio,
        double maximumOverlap)
    {
        RequireBrightness(printedBrightness, nameof(printedBrightness));
        RequireBrightness(minimumBrightness, nameof(minimumBrightness));
        RequireRatio(overlapRatio, nameof(overlapRatio));
        RequireRatio(maximumOverlap, nameof(maximumOverlap));

        if (printWindowSucceeded && printedBrightness >= minimumBrightness)
        {
            return UsePrintWindow();
        }

        if (overlapRatio > maximumOverlap)
        {
            return Refuse(ControlCaptureRefusal.Occluded);
        }

        return UseScreenRegion();
    }

    /// <summary>
    /// Decides one frame while the screen-region fallback is forbidden, so a printed frame that did
    /// not paint is the end of the line.
    /// </summary>
    /// <param name="printWindowSucceeded">Whether the control accepted the print request.</param>
    /// <param name="printedBrightness">The mean brightness of the printed frame, in 0..255.</param>
    /// <param name="minimumBrightness">The mean brightness a frame must reach to count as painted, in 0..255.</param>
    /// <returns>The path to take.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A brightness lies outside 0..255.</exception>
    public static ControlCaptureDecision DecideWithoutScreenFallback(
        bool printWindowSucceeded,
        int printedBrightness,
        int minimumBrightness)
    {
        RequireBrightness(printedBrightness, nameof(printedBrightness));
        RequireBrightness(minimumBrightness, nameof(minimumBrightness));

        if (printWindowSucceeded && printedBrightness >= minimumBrightness)
        {
            return UsePrintWindow();
        }

        return Refuse(ControlCaptureRefusal.PrintWindowBlankAndScreenDisabled);
    }

    private static void RequireBrightness(int brightness, string parameterName)
    {
        if (brightness < ControlCaptureOptions.BrightnessMinimum || brightness > ControlCaptureOptions.BrightnessMaximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                brightness,
                "A frame brightness must lie in 0..255.");
        }
    }

    private static void RequireRatio(double ratio, string parameterName)
    {
        if (double.IsNaN(ratio) || ratio < ControlCaptureOptions.OverlapMinimum || ratio > ControlCaptureOptions.OverlapMaximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                ratio,
                "An overlap ratio must lie in 0..1.");
        }
    }
}
