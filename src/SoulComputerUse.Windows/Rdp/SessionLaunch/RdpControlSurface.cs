using System;
using System.Drawing;
using System.Windows.Forms;
using Interop = SoulComputerUse.Windows.Native.NativeMethods;

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

    /// <summary>Creates the surface, its carrier and one ActiveX host for the control.</summary>
    /// <remarks>
    /// The carrier is an ordinary window that is parented to the product window later, so nothing here
    /// decides where the picture appears. It starts hidden and one pixel wide only so its handle exists
    /// before the control needs a parent; both are replaced by the picture rectangle.
    /// </remarks>
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

        // The carrier is never shown on its own, but its handle must exist before the control can create
        // its own, and the product window needs a real handle to adopt.
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
    /// <remarks>
    /// The handle this returns is the carrier's. The remote desktop control is docked inside it, and
    /// Windows Forms owns that control's placement; handing out the control's own window would let a caller
    /// reparent it away from the layout that sizes it, which is how the picture came to cover the caption.
    /// </remarks>
    public nint WindowHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = _control.Handle;
            return _carrier.Handle;
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

    /// <summary>
    /// Parents the carrier to the product window and keeps it exactly on the picture rectangle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The carrier is what gets adopted, not the control. The control is docked inside the carrier and
    /// Windows Forms owns its placement; reparenting the control itself would put it outside the layer that
    /// sizes it, and the picture would then sit at the product window's origin and cover the caption strip
    /// and the resize border. Once the carrier is a child of the product window, its bounds are already in
    /// the coordinate space the picture rectangle uses.
    /// </para>
    /// <para>
    /// The carrier's frame is removed before it is reparented, because a reparented window must not keep the
    /// border and caption of a top-level one.
    /// </para>
    /// </remarks>
    /// <param name="parent">The product window's handle.</param>
    /// <param name="bounds">The picture rectangle, in the product window's client coordinates.</param>
    /// <exception cref="ObjectDisposedException">The surface was already disposed.</exception>
    public void Adopt(nint parent, Rectangle bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (parent == 0)
        {
            throw new ArgumentException("The parent window handle must not be zero.", nameof(parent));
        }

        nint carrier = _carrier.Handle;
        nint style = Interop.GetWindowLongPtrW(carrier, Interop.GwlpStyle);

        // A child window must carry WS_CHILD and must not carry WS_POPUP, which is the style a WinForms Form
        // is created with. The visible bit is kept, because the picture is shown as soon as it is attached.
        long childStyle = (style.ToInt64() & ~Interop.WsPopup) | Interop.WsChild | Interop.WsVisible;
        _ = Interop.SetWindowLongPtrW(carrier, Interop.GwlpStyle, (nint)childStyle);

        if (Interop.GetParent(carrier) != parent)
        {
            _ = Interop.SetParent(carrier, parent);
        }

        _ = Interop.SetWindowPos(
            carrier,
            0,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            Interop.SwpNoZOrder | Interop.SwpNoActivate | Interop.SwpNoSendChanging);

        _control.Visible = true;
    }

    /// <summary>
    /// Shows the control after the product window adopted it as its picture surface.
    /// </summary>
    /// <remarks>
    /// The managed <c>Visible</c> flag alone is not enough here. The control's managed parent is the hidden
    /// carrier, and Windows Forms keeps a child of a hidden window hidden: it never writes the visible style
    /// bit, so the window the product window adopted stays invisible and its client area renders black while
    /// the session is live. The style bit is therefore written directly, which the integration test
    /// <c>RdpPictureSurfaceTests</c> pins.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The surface was already disposed.</exception>
    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _control.Visible = true;
        _carrier.Visible = true;

        nint handle = _carrier.Handle;
        nint style = Interop.GetWindowLongPtrW(handle, Interop.GwlpStyle);
        nint visible = (nint)(long)Interop.WsVisible;
        _ = Interop.SetWindowLongPtrW(handle, Interop.GwlpStyle, style | visible);
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