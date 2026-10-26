using System;
using System.Drawing;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// The measured layout of the self-drawn caption for one window size and one DPI. Every size comes
/// from the design tokens in <c>docs/native-titlebar-spec.md</c> times <c>dpi / 96</c>; nothing here
/// reads machine state, so the same inputs always produce the same layout.
/// </summary>
/// <remarks>
/// The caption is drawn inside the client area: the window procedure answers <c>WM_NCCALCSIZE</c>
/// with zero, so the client rectangle covers the whole window and the desktop picture is the only
/// thing below the caption strip.
/// </remarks>
public sealed class CaptionMetrics
{
    /// <summary>The DPI the design tokens are defined at.</summary>
    public const int BaseDpi = 96;

    /// <summary>Caption strip height at 100% scaling.</summary>
    public const int TitleBarHeight = 32;

    /// <summary>Distance from the left window edge to the window icon.</summary>
    public const int PaddingLeft = 12;

    /// <summary>Window icon edge length.</summary>
    public const int IconSize = 16;

    /// <summary>Gap between the icon and the first character of the title.</summary>
    public const int IconTitleGap = 8;

    /// <summary>Caption button width at 100% scaling.</summary>
    public const int ButtonWidth = 46;

    /// <summary>Minimum gap between the title, the status group and the buttons.</summary>
    public const int ContentGap = 8;

    /// <summary>Status dot diameter.</summary>
    public const int StatusDotSize = 8;

    /// <summary>Title font size at 100% scaling, before the system text scale factor.</summary>
    public const int TitleFontSize = 12;

    /// <summary>Status font size at 100% scaling, before the system text scale factor.</summary>
    public const int StatusFontSize = 12;

    /// <summary>Upper bound of the title, as a percentage of the client width.</summary>
    public const int TitleMaxWidthPercent = 40;

    /// <summary>
    /// Narrowest client width the caption still lays out without overlapping: three adjacent buttons,
    /// the icon block and the gaps around the text.
    /// </summary>
    public const int MinimumClientWidth =
        (ButtonWidth * 3) + PaddingLeft + IconSize + IconTitleGap + ContentGap;

    private CaptionMetrics(
        int clientWidth,
        int dpi,
        int textScalePercent,
        Rectangle iconBounds,
        Rectangle titleBounds,
        Rectangle minimizeBounds,
        Rectangle maximizeBounds,
        Rectangle closeBounds,
        int statusRightLimit,
        int titleFontPixels,
        int statusFontPixels,
        int statusDotPixels)
    {
        ClientWidth = clientWidth;
        Dpi = dpi;
        TextScalePercent = textScalePercent;
        Height = Scale(TitleBarHeight, dpi);
        IconBounds = iconBounds;
        TitleBounds = titleBounds;
        MinimizeBounds = minimizeBounds;
        MaximizeBounds = maximizeBounds;
        CloseBounds = closeBounds;
        StatusRightLimit = statusRightLimit;
        TitleFontPixels = titleFontPixels;
        StatusFontPixels = statusFontPixels;
        StatusDotPixels = statusDotPixels;
    }

    /// <summary>The client width the layout was measured for, in physical pixels.</summary>
    public int ClientWidth { get; }

    /// <summary>The DPI the layout was measured for.</summary>
    public int Dpi { get; }

    /// <summary>The system text scale factor in percent, applied to font sizes only.</summary>
    public int TextScalePercent { get; }

    /// <summary>The caption strip height in physical pixels.</summary>
    public int Height { get; }

    /// <summary>The window icon rectangle, inside the caption strip.</summary>
    public Rectangle IconBounds { get; }

    /// <summary>The rectangle the title text may occupy; its width is already clamped and capped.</summary>
    public Rectangle TitleBounds { get; }

    /// <summary>The minimize button rectangle.</summary>
    public Rectangle MinimizeBounds { get; }

    /// <summary>The maximize or restore button rectangle.</summary>
    public Rectangle MaximizeBounds { get; }

    /// <summary>The close button rectangle, always flush with the right edge.</summary>
    public Rectangle CloseBounds { get; }

    /// <summary>The rightmost coordinate the status group may use.</summary>
    public int StatusRightLimit { get; }

    /// <summary>Title font size in physical pixels, after DPI and text scaling.</summary>
    public int TitleFontPixels { get; }

    /// <summary>Status font size in physical pixels, after DPI and text scaling.</summary>
    public int StatusFontPixels { get; }

    /// <summary>Status dot diameter in physical pixels.</summary>
    public int StatusDotPixels { get; }

    /// <summary>
    /// Lays the caption out for a client width, a DPI and a text scale factor. Throws instead of
    /// producing an overlapping layout, because an overlapping caption cannot be clicked correctly.
    /// </summary>
    public static CaptionMetrics Create(int clientWidth, int dpi, int textScalePercent)
    {
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "The DPI must be positive.");
        }

        if (textScalePercent <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(textScalePercent),
                textScalePercent,
                "The text scale factor must be positive.");
        }

        int minimumWidth = Scale(MinimumClientWidth, dpi);
        if (clientWidth < minimumWidth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientWidth),
                clientWidth,
                $"The client width must be at least {minimumWidth} pixels at DPI {dpi}, otherwise the caption buttons overlap.");
        }

        int height = Scale(TitleBarHeight, dpi);
        int buttonsFromRight = Scale(ButtonWidth, dpi);
        int closeLeft = clientWidth - buttonsFromRight;

        // The three buttons are adjacent with no gap: an empty strip between two of them is not a
        // button, it is a drag area, and aiming for the close button one notch off would drag the
        // window instead of closing it.
        int maximizeLeft = closeLeft - buttonsFromRight;
        int minimizeLeft = maximizeLeft - buttonsFromRight;

        Rectangle closeBounds = new(closeLeft, 0, buttonsFromRight, height);
        Rectangle maximizeBounds = new(maximizeLeft, 0, buttonsFromRight, height);
        Rectangle minimizeBounds = new(minimizeLeft, 0, buttonsFromRight, height);

        int iconOffset = Scale(PaddingLeft, dpi);
        int iconEdge = Scale(IconSize, dpi);
        Rectangle iconBounds = new(iconOffset, (height - iconEdge) / 2, iconEdge, iconEdge);

        int titleLeft = iconOffset + iconEdge + Scale(IconTitleGap, dpi);
        int titleCap = (clientWidth * TitleMaxWidthPercent) / 100;
        int titleRightLimit = minimizeBounds.Left - Scale(ContentGap, dpi);
        int titleWidth = Math.Min(titleCap, Math.Max(0, titleRightLimit - titleLeft));
        Rectangle titleBounds = new(titleLeft, 0, titleWidth, height);

        int statusRightLimit = minimizeBounds.Left - Scale(ContentGap, dpi);
        int titleFont = ScaleFont(TitleFontSize, dpi, textScalePercent);
        int statusFont = ScaleFont(StatusFontSize, dpi, textScalePercent);
        // A dot that rounds down to nothing would make the session state unreadable, so it never
        // goes below one pixel even at a DPI no real monitor reports.
        int statusDot = ScaleVisible(StatusDotSize, dpi);

        return new CaptionMetrics(
            clientWidth,
            dpi,
            textScalePercent,
            iconBounds,
            titleBounds,
            minimizeBounds,
            maximizeBounds,
            closeBounds,
            statusRightLimit,
            titleFont,
            statusFont,
            statusDot);
    }

    /// <summary>Scales one design token from the 96 DPI baseline to a DPI.</summary>
    public static int Scale(int logicalValue, int dpi)
    {
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "The DPI must be positive.");
        }

        return (int)Math.Round(logicalValue * (double)dpi / BaseDpi, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Scales a token that has to stay visible, so it never rounds down to nothing. Only the tokens
    /// that would disappear without the floor use this: a status dot, a border, a hairline. A spacing
    /// token keeps <see cref="Scale"/> so a zero gap stays a zero gap.
    /// </summary>
    public static int ScaleVisible(int logicalValue, int dpi)
    {
        if (logicalValue <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalValue),
                logicalValue,
                "Only a positive token can be scaled as a visible element.");
        }

        return Math.Max(1, Scale(logicalValue, dpi));
    }

    private static int ScaleFont(int logicalValue, int dpi, int textScalePercent)
    {
        double scaled = logicalValue * (double)dpi / BaseDpi * textScalePercent / 100.0;
        return Math.Max(1, (int)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }
}
