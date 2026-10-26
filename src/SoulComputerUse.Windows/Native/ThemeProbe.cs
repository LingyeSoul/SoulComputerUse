using System;
using Microsoft.Win32;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// Reads whether the operator's apps use the light system theme. The caption follows it; there is no
/// product switch of its own, so a window always looks like the rest of the desktop.
/// </summary>
public static class ThemeProbe
{
    private const string PersonalizeKeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    /// <summary>Returns the theme the caption should use.</summary>
    public static ThemeKind GetTheme()
    {
        object? rawValue = Registry.GetValue(PersonalizeKeyPath, AppsUseLightThemeValueName, null);
        if (rawValue is null)
        {
            // Windows without the personalisation value behaves as the dark default of this product.
            return ThemeKind.Dark;
        }

        if (rawValue is not int value)
        {
            throw new InvalidOperationException(
                $"The system theme value is a {rawValue.GetType().Name}, but an integer flag was expected.");
        }

        if (value is not (0 or 1))
        {
            throw new InvalidOperationException($"The system theme value {value} is neither 0 nor 1.");
        }

        return value == 1 ? ThemeKind.Light : ThemeKind.Dark;
    }
}
