using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace SoulComputerUse.Windows.Shell;

/// <summary>The brand icon embedded in this assembly, drawn by the caption and reused by Win32.</summary>
public static class BrandIcon
{
    private const string ResourceName = "SoulComputerUse.Windows.BrandIcon.ico";

    private static readonly Icon Resource = LoadBrandIcon();

    /// <summary>The native icon handle used by the window class and explicit WM_SETICON messages.</summary>
    public static nint Handle => Resource.Handle;

    /// <summary>Draws the brand icon into the caption rectangle.</summary>
    public static void Draw(Graphics graphics, Rectangle bounds)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        graphics.DrawIcon(Resource, bounds);
    }

    private static Icon LoadBrandIcon()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded brand icon resource '{ResourceName}' was not found in assembly '{assembly.GetName().Name}'.");
        return new Icon(stream);
    }
}
