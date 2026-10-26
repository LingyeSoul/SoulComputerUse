using System;
using System.Drawing;

namespace SoulComputerUse.Windows.Shell;

/// <summary>An opaque sRGB colour, kept free of drawing types so it can be asserted as a value.</summary>
public readonly record struct CaptionColor(byte R, byte G, byte B)
{
    /// <summary>The pure black used for the letterbox around the desktop picture.</summary>
    public static CaptionColor Black { get; } = new(0, 0, 0);

    /// <summary>Builds a colour from a packed <c>0xRRGGBB</c> literal.</summary>
    public static CaptionColor FromRgb(int packedRgb)
    {
        if (packedRgb is < 0 or > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(packedRgb), packedRgb, "The colour must pack into 24 bits.");
        }

        return new CaptionColor(
            (byte)((packedRgb >> 16) & 0xFF),
            (byte)((packedRgb >> 8) & 0xFF),
            (byte)(packedRgb & 0xFF));
    }

    /// <summary>Relative luminance as defined by WCAG 2.1.</summary>
    public double RelativeLuminance =>
        (0.2126 * Linearize(R)) + (0.7152 * Linearize(G)) + (0.0722 * Linearize(B));

    /// <summary>WCAG 2.1 contrast ratio against another colour, from 1.0 to 21.0.</summary>
    public double ContrastRatio(CaptionColor other)
    {
        double first = RelativeLuminance;
        double second = other.RelativeLuminance;
        double lighter = Math.Max(first, second);
        double darker = Math.Min(first, second);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Converts to the drawing type used by the painter.</summary>
    public Color ToDrawingColor() => Color.FromArgb(255, R, G, B);

    /// <summary>
    /// Mixes this colour with another one, used to derive a text colour exactly one step weaker than
    /// its base without inventing a colour that is not in the token table.
    /// </summary>
    public CaptionColor Mix(CaptionColor other, int otherWeightPercent)
    {
        if (otherWeightPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(otherWeightPercent),
                otherWeightPercent,
                "The weight must be a percentage between 0 and 100.");
        }

        return new CaptionColor(
            BlendChannel(R, other.R, otherWeightPercent),
            BlendChannel(G, other.G, otherWeightPercent),
            BlendChannel(B, other.B, otherWeightPercent));
    }

    private static byte BlendChannel(byte self, byte other, int otherWeightPercent)
    {
        int blended = ((self * (100 - otherWeightPercent)) + (other * otherWeightPercent)) / 100;
        return (byte)Math.Clamp(blended, 0, 255);
    }

    private static double Linearize(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}

/// <summary>Which of the two system themes the window follows.</summary>
public enum ThemeKind
{
    /// <summary>The dark theme, which is the product default.</summary>
    Dark = 0,

    /// <summary>The light theme.</summary>
    Light = 1,
}

/// <summary>The three caption buttons, in their fixed left-to-right order.</summary>
public enum CaptionButtonKind
{
    /// <summary>Minimize.</summary>
    Minimize = 0,

    /// <summary>Maximize or restore, depending on the window state.</summary>
    Maximize = 1,

    /// <summary>Close, always the rightmost.</summary>
    Close = 2,
}

/// <summary>The interaction state of a caption button.</summary>
public enum CaptionButtonState
{
    /// <summary>Not hovered and not pressed.</summary>
    Normal = 0,

    /// <summary>Hovered, or pressed while the pointer is still over the button.</summary>
    Hover = 1,

    /// <summary>Pressed.</summary>
    Pressed = 2,
}

/// <summary>
/// The colour tokens of the caption, one set per theme and focus state, exactly as tabulated in
/// <c>docs/native-titlebar-spec.md</c>. Focus is expressed by text brightness and the bottom border;
/// the status dot never fades, so the session state stays readable in a background window.
/// </summary>
public sealed class TitleBarPalette
{
    private TitleBarPalette(
        CaptionColor background,
        CaptionColor border,
        CaptionColor titleText,
        CaptionColor statusText,
        CaptionColor captionHoverBackground,
        CaptionColor captionPressedBackground,
        CaptionColor closeHoverBackground,
        CaptionColor closePressedBackground,
        CaptionColor captionIcon,
        CaptionColor closeHoverIcon)
    {
        Background = background;
        Border = border;
        TitleText = titleText;
        StatusText = statusText;
        CaptionHoverBackground = captionHoverBackground;
        CaptionPressedBackground = captionPressedBackground;
        CloseHoverBackground = closeHoverBackground;
        ClosePressedBackground = closePressedBackground;
        CaptionIcon = captionIcon;
        CloseHoverIcon = closeHoverIcon;
    }

    /// <summary>The caption strip fill.</summary>
    public CaptionColor Background { get; }

    /// <summary>The one-pixel line between the caption strip and the desktop picture.</summary>
    public CaptionColor Border { get; }

    /// <summary>The title text colour.</summary>
    public CaptionColor TitleText { get; }

    /// <summary>The status text colour.</summary>
    public CaptionColor StatusText { get; }

    /// <summary>The fill of a hovered minimize or maximize button.</summary>
    public CaptionColor CaptionHoverBackground { get; }

    /// <summary>The fill of a pressed minimize or maximize button.</summary>
    public CaptionColor CaptionPressedBackground { get; }

    /// <summary>The fill of a hovered close button.</summary>
    public CaptionColor CloseHoverBackground { get; }

    /// <summary>The fill of a pressed close button.</summary>
    public CaptionColor ClosePressedBackground { get; }

    /// <summary>The glyph colour of the minimize and maximize buttons.</summary>
    public CaptionColor CaptionIcon { get; }

    /// <summary>The glyph colour of a hovered or pressed close button, always white.</summary>
    public CaptionColor CloseHoverIcon { get; }

    /// <summary>Returns the palette for one theme and focus state.</summary>
    public static TitleBarPalette For(ThemeKind theme, bool isActive)
    {
        return theme switch
        {
            ThemeKind.Dark when isActive => new TitleBarPalette(
                CaptionColor.FromRgb(0x202124),
                CaptionColor.FromRgb(0x000000),
                CaptionColor.FromRgb(0xFFFFFF),
                CaptionColor.FromRgb(0xC7C9CD),
                CaptionColor.FromRgb(0x38393D),
                CaptionColor.FromRgb(0x4A4B50),
                CaptionColor.FromRgb(0xC42B1C),
                CaptionColor.FromRgb(0xA72619),
                CaptionColor.FromRgb(0xFFFFFF),
                CaptionColor.FromRgb(0xFFFFFF)),
            ThemeKind.Dark => new TitleBarPalette(
                CaptionColor.FromRgb(0x1B1C1E),
                CaptionColor.FromRgb(0x3A3B3E),
                CaptionColor.FromRgb(0x9CA0A6),
                CaptionColor.FromRgb(0x7A7E84),
                CaptionColor.FromRgb(0x333438),
                CaptionColor.FromRgb(0x424348),
                CaptionColor.FromRgb(0xB3281A),
                CaptionColor.FromRgb(0x9C2417),
                CaptionColor.FromRgb(0x9CA0A6),
                CaptionColor.FromRgb(0xFFFFFF)),
            ThemeKind.Light when isActive => new TitleBarPalette(
                CaptionColor.FromRgb(0xF3F3F3),
                CaptionColor.FromRgb(0xD0D0D0),
                CaptionColor.FromRgb(0x1B1B1B),
                CaptionColor.FromRgb(0x4A4A4A),
                CaptionColor.FromRgb(0xE5E5E5),
                CaptionColor.FromRgb(0xD6D6D6),
                CaptionColor.FromRgb(0xC42B1C),
                CaptionColor.FromRgb(0xA72619),
                CaptionColor.FromRgb(0x1B1B1B),
                CaptionColor.FromRgb(0xFFFFFF)),
            ThemeKind.Light => new TitleBarPalette(
                CaptionColor.FromRgb(0xE8E8E8),
                CaptionColor.FromRgb(0xC4C4C4),
                CaptionColor.FromRgb(0x8A8A8A),
                CaptionColor.FromRgb(0x8A8A8A),
                CaptionColor.FromRgb(0xDEDEDE),
                CaptionColor.FromRgb(0xCFCFCF),
                CaptionColor.FromRgb(0xB3281A),
                CaptionColor.FromRgb(0x9C2417),
                CaptionColor.FromRgb(0x8A8A8A),
                CaptionColor.FromRgb(0xFFFFFF)),
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme."),
        };
    }

    /// <summary>Returns the fill behind one caption button.</summary>
    public CaptionColor ButtonBackground(CaptionButtonKind kind, CaptionButtonState state)
    {
        if (state == CaptionButtonState.Normal)
        {
            return Background;
        }

        return kind switch
        {
            CaptionButtonKind.Close => state == CaptionButtonState.Hover
                ? CloseHoverBackground
                : ClosePressedBackground,
            CaptionButtonKind.Minimize or CaptionButtonKind.Maximize => state == CaptionButtonState.Hover
                ? CaptionHoverBackground
                : CaptionPressedBackground,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown caption button."),
        };
    }

    /// <summary>Returns the glyph colour of one caption button.</summary>
    public CaptionColor ButtonIcon(CaptionButtonKind kind, CaptionButtonState state)
    {
        return kind switch
        {
            CaptionButtonKind.Close when state != CaptionButtonState.Normal => CloseHoverIcon,
            CaptionButtonKind.Close => CaptionIcon,
            CaptionButtonKind.Minimize or CaptionButtonKind.Maximize => CaptionIcon,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown caption button."),
        };
    }
}
