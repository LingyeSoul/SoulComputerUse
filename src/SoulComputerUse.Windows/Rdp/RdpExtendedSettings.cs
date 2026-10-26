using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>
/// <c>IMsRdpExtendedSettings</c>: the only route to properties the scriptable interface does not
/// expose, such as child-session connections.
/// </summary>
/// <remarks>
/// It must stay a plain <c>IUnknown</c>-derived vtable. Declaring it dual or dispatch-based shifts
/// every slot by the <c>IDispatch</c> methods and the call fails or corrupts the stack. This is a
/// measured fact from the prototype, not a style choice.
/// </remarks>
[ComImport]
[Guid("302d8188-0052-4807-806a-362b628f9ac5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    [PreserveSig]
    int set_Property([MarshalAs(UnmanagedType.BStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] object value);

    [PreserveSig]
    int get_Property([MarshalAs(UnmanagedType.BStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] out object value);
}

/// <summary>
/// Reads and writes extended settings on the raw RDP ActiveX object, and exposes the read-back check
/// the interface contract requires: a property that did not stick must never be trusted.
/// </summary>
public static class RdpExtendedSettings
{
    /// <summary>The setting that makes the connection land in a child session instead of this one.</summary>
    public const string ConnectToChildSessionProperty = "ConnectToChildSession";

    /// <summary>Writes one extended setting.</summary>
    public static void SetProperty(object ocx, string propertyName, object value)
    {
        ArgumentNullException.ThrowIfNull(ocx);
        ArgumentException.ThrowIfNullOrEmpty(propertyName);

        int result = AsSettings(ocx).set_Property(propertyName, value);
        if (result != 0)
        {
            throw new RdpSurfaceException(
                "set_Property",
                $"property={propertyName}",
                result,
                "IMsRdpExtendedSettings::set_Property failed");
        }
    }

    /// <summary>Reads one extended setting back.</summary>
    public static object GetProperty(object ocx, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(ocx);
        ArgumentException.ThrowIfNullOrEmpty(propertyName);

        int result = AsSettings(ocx).get_Property(propertyName, out object value);
        if (result != 0)
        {
            throw new RdpSurfaceException(
                "get_Property",
                $"property={propertyName}",
                result,
                "IMsRdpExtendedSettings::get_Property failed");
        }

        return value;
    }

    /// <summary>
    /// Writes a boolean setting and reads it back. A setting that did not stick is a hard failure:
    /// continuing would silently connect into the operator's own session.
    /// </summary>
    public static void SetBooleanAndVerify(object ocx, string propertyName, bool value)
    {
        SetProperty(ocx, propertyName, value);

        object readBack = GetProperty(ocx, propertyName);
        bool? actual = readBack as bool?;
        if (actual is null)
        {
            throw new RdpSurfaceException(
                "get_Property",
                $"property={propertyName}",
                0,
                $"the value read back for '{propertyName}' is not a boolean, so the setting cannot be verified");
        }

        if (actual.Value != value)
        {
            throw new RdpSurfaceException(
                "set_Property",
                $"property={propertyName} expected={value} actual={actual.Value}",
                0,
                $"'{propertyName}' did not stick after being written, so the connection is refused instead of risking the operator's own session");
        }
    }

    private static IMsRdpExtendedSettings AsSettings(object ocx)
    {
        // The cast performs the QueryInterface and lets the runtime own the reference count, which
        // cannot leak or double-release the way manual QueryInterface calls do.
        if (ocx is IMsRdpExtendedSettings settings)
        {
            return settings;
        }

        throw new RdpSurfaceException(
            "QueryInterface",
            "iid=302d8188-0052-4807-806a-362b628f9ac5",
            0,
            "the remote desktop client does not expose IMsRdpExtendedSettings");
    }
}