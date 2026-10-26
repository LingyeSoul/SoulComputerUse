using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// One memory device context with a compatible bitmap selected into it and a top-down BGRA buffer
/// beside it, used for exactly one capture attempt.
/// </summary>
/// <remarks>
/// The instance owns three GDI resources: the memory DC, the bitmap and the bitmap that was selected
/// before this one. Every one of them is released by <see cref="Dispose"/>, and the original bitmap
/// is selected back first, so a capture that throws halfway cannot leak a handle. The source device
/// context belongs to the caller and is never released here.
/// </remarks>
internal sealed class GdiFrameBuffer : IDisposable
{
    private readonly nint _memoryDeviceContext;
    private readonly nint _bitmap;
    private readonly nint _previousBitmap;
    private readonly byte[] _pixels;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    private GdiFrameBuffer(
        nint memoryDeviceContext,
        nint bitmap,
        nint previousBitmap,
        byte[] pixels,
        int width,
        int height)
    {
        _memoryDeviceContext = memoryDeviceContext;
        _bitmap = bitmap;
        _previousBitmap = previousBitmap;
        _pixels = pixels;
        _width = width;
        _height = height;
    }

    /// <summary>The memory device context the capture draws into.</summary>
    internal nint DeviceContext => _memoryDeviceContext;

    /// <summary>
    /// Creates the memory surface for one frame. A failure at any step releases every handle taken so
    /// far and rethrows, so a repeated failure cannot drain the process GDI quota.
    /// </summary>
    /// <param name="sourceDeviceContext">The device context the bitmap is made compatible with.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive.</exception>
    /// <exception cref="Win32Exception">A GDI call failed.</exception>
    internal static GdiFrameBuffer Create(nint sourceDeviceContext, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The frame width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The frame height must be positive.");
        }

        nint memoryDeviceContext = ControlCaptureNative.CreateCompatibleDC(sourceDeviceContext);
        if (memoryDeviceContext == 0)
        {
            throw Failure("CreateCompatibleDC", Marshal.GetLastWin32Error());
        }

        nint bitmap = 0;
        nint previousBitmap = 0;
        try
        {
            bitmap = ControlCaptureNative.CreateCompatibleBitmap(sourceDeviceContext, width, height);
            if (bitmap == 0)
            {
                throw Failure("CreateCompatibleBitmap", Marshal.GetLastWin32Error());
            }

            previousBitmap = ControlCaptureNative.SelectObject(memoryDeviceContext, bitmap);
            if (previousBitmap == 0)
            {
                throw Failure("SelectObject", Marshal.GetLastWin32Error());
            }

            byte[] pixels = new byte[width * height * 4];
            return new GdiFrameBuffer(memoryDeviceContext, bitmap, previousBitmap, pixels, width, height);
        }
        catch
        {
            if (previousBitmap != 0)
            {
                _ = ControlCaptureNative.SelectObject(memoryDeviceContext, previousBitmap);
            }

            if (bitmap != 0)
            {
                _ = ControlCaptureNative.DeleteObject(bitmap);
            }

            _ = ControlCaptureNative.DeleteDC(memoryDeviceContext);
            throw;
        }
    }

    /// <summary>
    /// Reads the selected bitmap into a top-down BGRA buffer. The bitmap is deselected for the read,
    /// because <c>GetDIBits</c> refuses a bitmap that is still selected into a device context, and
    /// selected back afterwards so <see cref="Dispose"/> always finds it owned by this surface.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The surface was already released.</exception>
    /// <exception cref="Win32Exception">Reading the pixels failed.</exception>
    internal byte[] ReadPixels()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        BitmapInfoHeader header = new()
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = _width,
            // A negative height asks for top-down rows, which is the order the PNG writer expects.
            Height = -_height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        _ = ControlCaptureNative.SelectObject(_memoryDeviceContext, _previousBitmap);
        int scanLines;
        int errorCode;
        try
        {
            scanLines = ControlCaptureNative.GetDIBits(
                _memoryDeviceContext,
                _bitmap,
                0,
                (uint)_height,
                _pixels,
                ref header,
                ControlCaptureNative.DibRgbColors);
            // Read before the finally block, because restoring the bitmap overwrites the last error.
            errorCode = scanLines == 0 ? Marshal.GetLastWin32Error() : 0;
        }
        finally
        {
            _ = ControlCaptureNative.SelectObject(_memoryDeviceContext, _bitmap);
        }

        if (scanLines == 0)
        {
            throw Failure("GetDIBits", errorCode);
        }

        if (scanLines != _height)
        {
            throw new InvalidOperationException(
                "GetDIBits read "
                + scanLines.ToString(CultureInfo.InvariantCulture)
                + " of "
                + _height.ToString(CultureInfo.InvariantCulture)
                + " scan lines, so the frame is incomplete.");
        }

        return _pixels;
    }

    /// <summary>
    /// Restores the original bitmap and releases the bitmap and the memory DC.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = ControlCaptureNative.SelectObject(_memoryDeviceContext, _previousBitmap);
        _ = ControlCaptureNative.DeleteObject(_bitmap);
        _ = ControlCaptureNative.DeleteDC(_memoryDeviceContext);
    }

    private static Win32Exception Failure(string operation, int errorCode)
    {
        return new Win32Exception(
            errorCode,
            operation
            + " failed with Win32 error "
            + errorCode.ToString(CultureInfo.InvariantCulture)
            + ".");
    }
}
