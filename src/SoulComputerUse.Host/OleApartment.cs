using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Host;

/// <summary>
/// Initializes the OLE apartment the new-session thread needs before any ActiveX control is created.
/// </summary>
/// <remarks>
/// An ActiveX control is a COM object with an in-place site, and hosting one requires the calling thread to
/// have called <c>OleInitialize</c>; without it the control host cannot create its window and the surface
/// handle stays zero. This type is the only place that calls it, and it always pairs the call with
/// <c>OleUninitialize</c>.
/// </remarks>
internal sealed class OleApartment : IDisposable
{
    private const int SFalse = 1;
    private const int STrue = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private bool _initialized;
    private bool _disposed;

    private OleApartment(bool initialized)
    {
        _initialized = initialized;
    }

    /// <summary>Initializes OLE on the calling thread, which must already be single-threaded.</summary>
    /// <exception cref="InvalidOperationException">OLE could not be initialized.</exception>
    internal static OleApartment Initialize()
    {
        int result = OleInitialize(0);
        if (result is STrue or SFalse)
        {
            return new OleApartment(initialized: true);
        }

        if (result == RpcEChangedMode)
        {
            // The thread was already initialized with another apartment model, which the caller set up.
            // Hosting the control is still possible, so this is not treated as a failure, and the matching
            // uninitialization is the caller's.
            return new OleApartment(initialized: false);
        }

        throw new InvalidOperationException(
            "OleInitialize failed with HRESULT 0x"
            + result.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
            + ", so the remote desktop control cannot be hosted on this thread.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed || !_initialized)
        {
            _disposed = true;
            return;
        }

        _disposed = true;
        _initialized = false;
        OleUninitialize();
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
}