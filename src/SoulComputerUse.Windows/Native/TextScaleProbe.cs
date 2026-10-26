using System;
using System.Globalization;
using Microsoft.Win32;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// Reads the system text scale factor, which is the Settings slider that makes text bigger without
/// changing the display DPI. Font sizes in the caption follow it; layout sizes follow the DPI only.
/// </summary>
public static class TextScaleProbe
{
    /// <summary>The scale factor a machine reports when the operator never moved the slider.</summary>
    public const int DefaultTextScalePercent = 100;

    /// <summary>The smallest factor Windows offers.</summary>
    public const int MinimumTextScalePercent = 100;

    /// <summary>The largest factor Windows offers.</summary>
    public const int MaximumTextScalePercent = 225;

    private const string AccessibilityKeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Accessibility";
    private const string TextScaleValueName = "TextScaleFactor";

    /// <summary>Returns the text scale factor in percent, or the default when it was never set.</summary>
    public static int GetTextScalePercent()
    {
        object? rawValue = Registry.GetValue(AccessibilityKeyPath, TextScaleValueName, null);
        if (rawValue is null)
        {
            return DefaultTextScalePercent;
        }

        if (rawValue is not int value)
        {
            throw new InvalidOperationException(
                $"The system text scale value is a {rawValue.GetType().Name}, but an integer percentage was expected.");
        }

        if (value is < MinimumTextScalePercent or > MaximumTextScalePercent)
        {
            throw new InvalidOperationException(
                $"The system text scale value {value.ToString(CultureInfo.InvariantCulture)} is outside the supported range {MinimumTextScalePercent}..{MaximumTextScalePercent}.");
        }

        return value;
    }
}
