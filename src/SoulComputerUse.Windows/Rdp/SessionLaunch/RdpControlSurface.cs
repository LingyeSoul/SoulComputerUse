using System;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The production surface: it owns the ActiveX host, creates the control on first use and exposes only
/// the raw control object and its window handle.
/// </summary>
/// <remarks>
/// <para>
/// The control is created on the thread that creates this object, which must be an STA thread: an
/// ActiveX control cannot be instantiated anywhere else. Reading <see cref="Ocx"/> forces the host to
/// create the control, so a caller cannot reach the launch path with a control that was never attached.
/// </para>
/// <para>
/// This type deliberately exposes no Windows Forms type in its public surface. The host process only
/// needs the two values the launch path and the product window consume, and keeping the control host
/// behind this type means the host project needs no Windows Forms reference of its own.
/// </para>
/// </remarks>
public sealed class RdpControlSurface : IRdpControlSurface, IDisposable
{
    private readonly RdpControlHost _control;
    private bool _disposed;

    /// <summary>Creates the surface and one ActiveX host for the remote desktop control.</summary>
    public RdpControlSurface()
    {
        _control = new RdpControlHost();
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
    }
}
