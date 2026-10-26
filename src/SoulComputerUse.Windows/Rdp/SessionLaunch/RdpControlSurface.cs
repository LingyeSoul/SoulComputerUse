using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The production surface: it owns the ActiveX host, creates the control on first use and exposes only
/// the raw control object and its window handle.
/// </summary>
/// <remarks>
/// <para>
/// The control is created on the thread that creates this object, which must be an STA thread: an
/// ActiveX control cannot be instantiated anywhere else.
/// </para>
/// <para>
/// An <c>AxHost</c> only creates its window once it has a parent control, so the host is sited in a hidden
/// owner form whose handle is forced here. The product window then adopts the control's window with
/// <c>SetParent</c>, which is why <see cref="WindowHandle"/> is the control's own handle and not the
/// carrier's. Without the carrier the handle stays zero and the picture surface can never be attached.
/// </para>
/// <para>
/// This type deliberately exposes no Windows Forms type in its public surface. The host process only needs
/// the control object and the handle, and keeping the carrier behind this type means the host project needs
/// no Windows Forms reference of its own.
/// </para>
/// </remarks>
public sealed class RdpControlSurface : IRdpControlSurface, IDisposable
{
    private readonly Form _carrier;
    private readonly RdpControlHost _control;
    private bool _disposed;

    /// <summary>Creates the surface, its hidden carrier and one ActiveX host for the control.</summary>
    public RdpControlSurface()
    {
        _carrier = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(1, 1),
            Visible = false,
        };

        _control = new RdpControlHost
        {
            Dock = DockStyle.Fill,
        };
        _carrier.Controls.Add(_control);

        // The carrier is never shown, but its handle must exist before the control can create its own.
        _ = _carrier.Handle;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The host did not attach a control object.</exception>
    public object Ocx
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The host attaches the control object while it creates its window, so the handle is
            // touched first and the attached object is then read.
            _ = _control.Handle;
            object? ocx = _control.Ocx;
            if (ocx is null)
            {
                throw new InvalidOperationException(
                    "The ActiveX host did not attach the remote desktop control, so no session can be configured on it.");
            }

            return ocx;
        }
    }

    /// <inheritdoc />
    public nint WindowHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _control.Handle;
        }
    }

    /// <summary>
    /// The client's own connected state: 0 idle, 1 connected, 2 connecting, 3 waiting for the operator to
    /// log on. This is the value the logon gate reads, so it must come from the control and nowhere else.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The surface was already disposed.</exception>
    public int ConnectedState
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _control.ConnectedState;
        }
    }

    /// <summary>Shows the control after the product window adopted it as its picture surface.</summary>
    /// <exception cref="ObjectDisposedException">The surface was already disposed.</exception>
    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _control.Visible = true;
    }

    /// <summary>Destroys the hosted control. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _control.Dispose();
        _carrier.Dispose();
    }
}