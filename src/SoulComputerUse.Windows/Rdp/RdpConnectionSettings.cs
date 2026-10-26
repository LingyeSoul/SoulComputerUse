using System;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>
/// The connection settings that are written into the in-memory connection configuration. They never
/// touch the registry or the disk, and they carry no credential material: the operator's password is
/// only ever collected by the Windows remote desktop dialog itself.
/// </summary>
public sealed record RdpConnectionSettings(string Server, int DesktopWidth, int DesktopHeight, bool ConnectToChildSession)
{
    /// <summary>Rejects a configuration that could not produce a usable session.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            throw new ArgumentException("The connection server must not be empty.", nameof(Server));
        }

        if (DesktopWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DesktopWidth), DesktopWidth, "The desktop width must be positive.");
        }

        if (DesktopHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DesktopHeight), DesktopHeight, "The desktop height must be positive.");
        }
    }

    /// <summary>Renders the settings for a log line. Nothing here is secret.</summary>
    public string Describe()
    {
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "size={0}x{1} childSession={2}",
            DesktopWidth,
            DesktopHeight,
            ConnectToChildSession);
    }
}