using System;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// The size of the remote desktop a session should request, in the session's native physical pixels.
/// </summary>
/// <remarks>
/// <para>
/// The session is created at the primary display's physical size, measured with
/// <c>GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)</c>. That is the right size for two reasons: the remote
/// desktop then has the same number of pixels as the display it is shown on, so text and icons are
/// rendered at their native size instead of being rescaled, and the tool coordinates the agent reports are
/// the same pixels the operator sees.
/// </para>
/// <para>
/// The measurement is only correct in a per-monitor DPI aware process: a DPI-unaware process sees
/// virtualized coordinates and would request a session smaller than the display. The host's manifest asks
/// for <c>PerMonitorV2</c>, and the process refuses to fall back silently, because a session at the wrong
/// size is not a cosmetic problem: every coordinate the tools accept would be off by the scale factor.
/// </para>
/// </remarks>
public static class SessionDesktopSize
{
    /// <summary>The smallest width a session can usefully have, matching the product window's minimum.</summary>
    public const int MinimumWidth = 640;

    /// <summary>The smallest height a session can usefully have, matching the product window's minimum.</summary>
    public const int MinimumHeight = 480;

    /// <summary>
    /// Reads the primary display's physical size and returns it as a session desktop size.
    /// </summary>
    /// <exception cref="InvalidOperationException">The system reports no usable primary display size.</exception>
    public static (int Width, int Height) PrimaryDisplay()
    {
        int width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen);
        if (width < MinimumWidth || height < MinimumHeight)
        {
            throw new InvalidOperationException(
                "The primary display reports "
                + width.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "x"
                + height.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " physical pixels, which cannot host a usable remote desktop. The process must be per-monitor"
                + " DPI aware for this measurement to be the real pixel size.");
        }

        return (width, height);
    }
}