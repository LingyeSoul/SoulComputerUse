using System;
using System.Windows.Forms;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>
/// Hosts the remote desktop control the way it has to be hosted: through an ActiveX host created from
/// the control class, so the object handed back is the OLE control object rather than a bare COM
/// instance.
/// </summary>
/// <remarks>
/// The host is a WinForms control on purpose. The product window is a plain Win32 window, and the
/// control is attached to it as a child window over the rectangle the letterbox layout computes, so the
/// remote desktop picture never paints over the black bars.
/// </remarks>
public sealed class RdpControlHost : AxHost
{
    /// <summary>The control class that has been proven to host and connect on this machine.</summary>
    public const string ClientClassId = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";

    /// <summary>Creates the host. The control itself is instantiated on first use of <see cref="Handle"/>.</summary>
    public RdpControlHost()
        : base(ClientClassId)
    {
    }

    /// <summary>The raw OLE object the host attached, or null until the control exists.</summary>
    public object? Ocx { get; private set; }

    /// <summary>
    /// The client connected state as the control reports it: 0 idle, 1 connected, 2 connecting,
    /// 3 waiting for the operator to log on.
    /// </summary>
    public int ConnectedState
    {
        get
        {
            object? ocx = Ocx;
            if (ocx is null)
            {
                return 0;
            }

            dynamic client = ocx;
            return (int)client.Connected;
        }
    }

    /// <inheritdoc />
    protected override void AttachInterfaces()
    {
        Ocx = GetOcx();
    }
}
