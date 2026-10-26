using System;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Rdp.Capture;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Exercises the host-side control capture: the pure brightness, overlap and decision functions, and a
/// real capture of a real Win32 window that paints one solid colour. The remote desktop control is
/// deliberately not started, because hosting it needs the ActiveX registration and a live session.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ControlCaptureTests
{
    private const int WindowLeft = 40;
    private const int WindowTop = 40;
    private const int WindowWidth = 320;
    private const int WindowHeight = 200;

    /// <summary>A black frame has no brightness at all.</summary>
    [TestMethod]
    public void MeanReturnsZeroForABlackFrame()
    {
        byte[] bgra = CreateFrame(4, 4, 0, 0, 0);

        Assert.AreEqual(0, FrameBrightness.Mean(bgra, 4, 4, 1));
    }

    /// <summary>A white frame is the maximum of the brightness range.</summary>
    [TestMethod]
    public void MeanReturnsFullScaleForAWhiteFrame()
    {
        byte[] bgra = CreateFrame(4, 4, 255, 255, 255);

        Assert.AreEqual(255, FrameBrightness.Mean(bgra, 4, 4, 1));
    }

    /// <summary>Half the frame white and half black averages to the middle of the range.</summary>
    [TestMethod]
    public void MeanAveragesPartialCoverage()
    {
        byte[] bgra = new byte[4 * 4 * 4];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int offset = ((y * 4) + x) * 4;
                bgra[offset] = 255;
                bgra[offset + 1] = 255;
                bgra[offset + 2] = 255;
            }
        }

        Assert.AreEqual(127, FrameBrightness.Mean(bgra, 4, 4, 1), "Half of 255 is 127 after integer division.");
    }

    /// <summary>Sampling every second or fourth pixel must report the same picture, not another one.</summary>
    [TestMethod]
    public void MeanSamplingStepKeepsTheResultInTheSameRange()
    {
        byte[] bgra = new byte[16 * 16 * 4];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int offset = ((y * 16) + x) * 4;
                bgra[offset] = 240;
                bgra[offset + 1] = 240;
                bgra[offset + 2] = 240;
            }
        }

        int everyPixel = FrameBrightness.Mean(bgra, 16, 16, 1);
        int everySecond = FrameBrightness.Mean(bgra, 16, 16, 2);
        int everyFourth = FrameBrightness.Mean(bgra, 16, 16, 4);

        Assert.AreEqual(120, everyPixel, "Half the rows are bright, so the mean is half of 240.");
        Assert.AreEqual(everyPixel, everySecond, "Sampling every second pixel must not change a uniform-row frame.");
        Assert.AreEqual(everyPixel, everyFourth, "Sampling every fourth pixel must not change a uniform-row frame.");
    }

    /// <summary>Every impossible input is refused explicitly instead of producing a misleading number.</summary>
    [TestMethod]
    public void MeanRejectsInvalidArguments()
    {
        byte[] valid = CreateFrame(2, 2, 0, 0, 0);

        Assert.ThrowsExactly<ArgumentNullException>(() => FrameBrightness.Mean(null!, 2, 2, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FrameBrightness.Mean(valid, 0, 2, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FrameBrightness.Mean(valid, 2, -1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FrameBrightness.Mean(valid, 2, 2, 0));
        Assert.ThrowsExactly<ArgumentException>(() => FrameBrightness.Mean(valid, 3, 3, 1));
    }

    /// <summary>Rectangles that do not touch cover nothing.</summary>
    [TestMethod]
    public void OverlapRatioReturnsZeroWhenRectanglesDoNotIntersect()
    {
        double ratio = OcclusionCalculator.OverlapRatio(
            new Rectangle(0, 0, 100, 100),
            new Rectangle(200, 200, 50, 50));

        Assert.AreEqual(0.0, ratio);
    }

    /// <summary>A foreground rectangle that contains the target covers all of it.</summary>
    [TestMethod]
    public void OverlapRatioReturnsOneWhenTheTargetIsFullyCovered()
    {
        double ratio = OcclusionCalculator.OverlapRatio(
            new Rectangle(10, 10, 100, 100),
            new Rectangle(0, 0, 400, 400));

        Assert.AreEqual(1.0, ratio);
    }

    /// <summary>A foreground rectangle covering the left half reports roughly one half.</summary>
    [TestMethod]
    public void OverlapRatioReturnsHalfForHalfCoverage()
    {
        double ratio = OcclusionCalculator.OverlapRatio(
            new Rectangle(0, 0, 100, 100),
            new Rectangle(0, 0, 50, 100));

        Assert.AreEqual(0.5, ratio, 0.0001);
    }

    /// <summary>A target without area makes the ratio meaningless, so it is refused.</summary>
    [TestMethod]
    public void OverlapRatioRejectsAnEmptyTarget()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => OcclusionCalculator.OverlapRatio(Rectangle.Empty, new Rectangle(0, 0, 10, 10)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => OcclusionCalculator.OverlapRatio(new Rectangle(0, 0, 0, 100), new Rectangle(0, 0, 10, 10)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => OcclusionCalculator.OverlapRatio(new Rectangle(0, 0, 100, -5), new Rectangle(0, 0, 10, 10)));
    }

    /// <summary>A printed frame above the threshold is used as it is.</summary>
    [TestMethod]
    public void DecideUsesPrintWindowWhenTheFrameIsBrightEnough()
    {
        ControlCaptureDecision decision = ControlCaptureDecision.Decide(
            printWindowSucceeded: true,
            printedBrightness: 120,
            minimumBrightness: 8,
            overlapRatio: 0.0,
            maximumOverlap: 0.02);

        Assert.AreEqual(ControlCapturePath.PrintWindow, decision.Path);
        Assert.IsNull(decision.Refusal);
    }

    /// <summary>A blank print falls back to the screen while nothing covers the control.</summary>
    [TestMethod]
    public void DecideFallsBackToScreenWhenThePrintIsBlankAndTheControlIsVisible()
    {
        ControlCaptureDecision decision = ControlCaptureDecision.Decide(
            printWindowSucceeded: true,
            printedBrightness: 0,
            minimumBrightness: 8,
            overlapRatio: 0.0,
            maximumOverlap: 0.02);

        Assert.AreEqual(ControlCapturePath.ScreenRegion, decision.Path);
        Assert.IsNull(decision.Refusal);
    }

    /// <summary>A covered control never produces a screen frame, because it would show the cover.</summary>
    [TestMethod]
    public void DecideRefusesWhenTheControlIsOccluded()
    {
        ControlCaptureDecision decision = ControlCaptureDecision.Decide(
            printWindowSucceeded: true,
            printedBrightness: 0,
            minimumBrightness: 8,
            overlapRatio: 0.5,
            maximumOverlap: 0.02);

        Assert.AreEqual(ControlCapturePath.Refuse, decision.Path);
        Assert.AreEqual(ControlCaptureRefusal.Occluded, decision.Refusal);
    }

    /// <summary>With the fallback disabled a blank print leaves nothing to use.</summary>
    [TestMethod]
    public void DecideRefusesWhenThePrintIsBlankAndScreenCaptureIsDisabled()
    {
        ControlCaptureDecision decision = ControlCaptureDecision.DecideWithoutScreenFallback(
            printWindowSucceeded: false,
            printedBrightness: 0,
            minimumBrightness: 8);

        Assert.AreEqual(ControlCapturePath.Refuse, decision.Path);
        Assert.AreEqual(ControlCaptureRefusal.PrintWindowBlankAndScreenDisabled, decision.Refusal);
    }

    /// <summary>A bright print is still used when the fallback is forbidden.</summary>
    [TestMethod]
    public void DecideWithoutScreenFallbackUsesABrightPrintedFrame()
    {
        ControlCaptureDecision decision = ControlCaptureDecision.DecideWithoutScreenFallback(
            printWindowSucceeded: true,
            printedBrightness: 200,
            minimumBrightness: 8);

        Assert.AreEqual(ControlCapturePath.PrintWindow, decision.Path);
        Assert.IsNull(decision.Refusal);
    }

    /// <summary>The production policy refuses a brightness or an overlap outside its documented range.</summary>
    [TestMethod]
    public void OptionsRejectValuesOutsideTheirRange()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ControlCaptureOptions(-1, 0.0, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ControlCaptureOptions(256, 0.0, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ControlCaptureOptions(8, -0.01, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ControlCaptureOptions(8, 1.01, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ControlCaptureOptions(8, double.NaN, true));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ControlFrameCapture(null!));
    }

    /// <summary>The decision function refuses every fact outside its documented range.</summary>
    [TestMethod]
    public void DecideRejectsValuesOutsideTheirRange()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ControlCaptureDecision.Decide(true, 256, 8, 0.0, 0.02));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ControlCaptureDecision.Decide(true, 100, -1, 0.0, 0.02));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ControlCaptureDecision.Decide(true, 100, 8, -0.1, 0.02));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ControlCaptureDecision.Decide(true, 100, 8, 0.0, 1.5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ControlCaptureDecision.Refuse((ControlCaptureRefusal)99));
    }

    /// <summary>
    /// A covered control is refused with the contract's agent-error code and is not retryable, and its
    /// message names neither the covering window nor anything else about the machine.
    /// </summary>
    [TestMethod]
    public void OcclusionRefusalCarriesTheContractCodeAndAFixedMessage()
    {
        SessionOperationException error = Assert.ThrowsExactly<SessionOperationException>(
            () => throw ControlCaptureFailure.ForRefusal(ControlCaptureRefusal.Occluded, printedSucceeded: true, printErrorCode: 0));

        Assert.AreEqual(SessionErrorCode.AgentError, error.Code);
        Assert.IsFalse(error.Retryable, "Clearing the cover is the operator's step, so the call is not retryable.");
        Assert.AreEqual(ControlCaptureFailure.OccludedMessage, error.Message);
        StringAssert.Contains(error.Message, "covered", "The message must say the picture is covered.");
    }

    /// <summary>A rejected print with the fallback disabled surfaces the Win32 code it reported.</summary>
    [TestMethod]
    public void PrintRefusalWithoutFallbackCarriesTheWin32Code()
    {
        System.ComponentModel.Win32Exception error = Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(
            () => throw ControlCaptureFailure.ForRefusal(
                ControlCaptureRefusal.PrintWindowBlankAndScreenDisabled,
                printedSucceeded: false,
                printErrorCode: 5));

        Assert.AreEqual(5, error.NativeErrorCode);
    }

    /// <summary>A real window that paints a solid colour produces a real PNG of its own size.</summary>
    [TestMethod]
    public void CapturePngReturnsARealPngForASolidColorWindow()
    {
        using SolidColorWindow window = SolidColorWindow.Create(WindowWidth, WindowHeight);
        ControlFrameCapture capture = new(ControlCaptureOptions.Default);

        byte[] png = capture.CapturePng(window.Handle);

        Assert.IsNotEmpty(png, "A capture must produce bytes.");
        Assert.AreEqual((byte)0x89, png[0], "The PNG signature starts with 0x89.");
        Assert.AreEqual((byte)0x50, png[1], "The PNG signature continues with 0x50.");
        Assert.AreEqual((byte)0x4E, png[2], "The PNG signature continues with 0x4E.");
        Assert.AreEqual((byte)0x47, png[3], "The PNG signature continues with 0x47.");
        Assert.IsGreaterThanOrEqualTo(24, png.Length, "A PNG needs a complete IHDR chunk to be readable.");

        (int width, int height) = ReadPngSize(png);
        Assert.AreEqual(WindowWidth, width, "The frame must be as wide as the captured window.");
        Assert.AreEqual(WindowHeight, height, "The frame must be as tall as the captured window.");

        // The centre of the frame lies inside the painted client area, so a frame that came from the
        // window must carry the painted colour there. A blank frame or another window's picture would
        // not.
        using Bitmap decoded = new(new MemoryStream(png, writable: false));
        Color centre = decoded.GetPixel(width / 2, height / 2);
        Assert.IsGreaterThanOrEqualTo(
            SolidColorWindow.PaintedLevel - 8,
            centre.R,
            "The captured frame must show the colour the window painted, so the pixels really came from the window.");
        Assert.IsLessThanOrEqualTo(
            SolidColorWindow.PaintedLevel + 8,
            centre.R,
            "The captured frame must show the colour the window painted, so the pixels really came from the window.");
    }

    /// <summary>A zero handle is refused before any Win32 call is made.</summary>
    [TestMethod]
    public void CapturePngRejectsAZeroHandle()
    {
        ControlFrameCapture capture = new(ControlCaptureOptions.Default);

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => capture.CapturePng(0));

        Assert.AreEqual("controlHandle", error.ParamName);
    }

    /// <summary>
    /// With the brightness threshold above what the window prints, the capture must read the visible
    /// screen instead. The frame is asserted structurally, because what the screen shows at that
    /// rectangle depends on the machine: this case proves the fallback produced a real frame of the
    /// control's own size, not that a particular pixel is visible.
    /// </summary>
    [TestMethod]
    public void CapturePngFallsBackToTheVisibleScreenWhenThePrintIsBlank()
    {
        using SolidColorWindow window = SolidColorWindow.Create(WindowWidth, WindowHeight);
        ControlCaptureOptions options = new(
            minimumBrightness: ControlCaptureOptions.BrightnessMaximum,
            maximumOverlap: ControlCaptureOptions.OverlapMaximum,
            allowScreenRegionFallback: true);
        ControlFrameCapture capture = new(options);

        byte[] png;
        try
        {
            png = capture.CapturePng(window.Handle);
        }
        catch (InvalidOperationException error) when (
            error.Message.Contains("foreground window", StringComparison.OrdinalIgnoreCase))
        {
            // A session without a foreground window (a locked or disconnected desktop) cannot prove the
            // control is visible, so the capture refuses. That is the documented behaviour, not a
            // product defect, and the machine state that produces it cannot be controlled from a test.
            Assert.Inconclusive("This desktop exposes no foreground window, so the screen fallback cannot be proven here.");
            return;
        }

        Assert.IsNotEmpty(png, "The screen-region fallback must produce a frame.");
        Assert.AreEqual((byte)0x89, png[0], "The fallback output is still a PNG.");
        Assert.AreEqual((byte)0x50, png[1], "The fallback output is still a PNG.");
        Assert.AreEqual((byte)0x4E, png[2], "The fallback output is still a PNG.");
        Assert.AreEqual((byte)0x47, png[3], "The fallback output is still a PNG.");

        (int width, int height) = ReadPngSize(png);
        Assert.AreEqual(WindowWidth, width, "The fallback frame must cover the control's own width.");
        Assert.AreEqual(WindowHeight, height, "The fallback frame must cover the control's own height.");
    }

    /// <summary>A handle that names no window fails with a Win32 error instead of a crash or a blank frame.</summary>
    [TestMethod]
    public void CapturePngRejectsAHandleThatIsNotAWindow()
    {
        ControlFrameCapture capture = new(ControlCaptureOptions.Default);
        nint notAWindow = unchecked((nint)0x7FFF_FFF0);

        Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(() => capture.CapturePng(notAWindow));
    }

    /// <summary>
    /// Repeated captures of the same window keep working and do not grow the process GDI handle count
    /// by anything close to one handle per attempt, which is how a leaked DC or bitmap would show up.
    /// </summary>
    [TestMethod]
    public void CapturePngSurvivesRepeatedCapturesWithoutLeakingHandles()
    {
        using SolidColorWindow window = SolidColorWindow.Create(WindowWidth, WindowHeight);
        ControlFrameCapture capture = new(ControlCaptureOptions.Default);

        // The first capture warms up the GDI and GDI+ caches, which are not leaks.
        _ = capture.CapturePng(window.Handle);
        uint handlesBefore = CountGdiObjects();

        const int Repetitions = 20;
        for (int index = 0; index < Repetitions; index++)
        {
            byte[] png = capture.CapturePng(window.Handle);
            Assert.IsNotEmpty(png, "Every repetition must produce a frame.");
        }

        uint handlesAfter = CountGdiObjects();
        Assert.IsLessThanOrEqualTo(
            handlesBefore + 8,
            handlesAfter,
            "Twenty captures must not leave a pair of GDI handles behind each time.");
    }

    /// <summary>Builds a one-colour BGRA frame.</summary>
    private static byte[] CreateFrame(int width, int height, byte blue, byte green, byte red)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int offset = 0; offset < bgra.Length; offset += 4)
        {
            bgra[offset] = blue;
            bgra[offset + 1] = green;
            bgra[offset + 2] = red;
            bgra[offset + 3] = 255;
        }

        return bgra;
    }

    /// <summary>Reads the image size out of the PNG IHDR chunk, which starts after the 8-byte signature.</summary>
    private static (int Width, int Height) ReadPngSize(byte[] png)
    {
        // The chunk length is a 4-byte big-endian value, so 13 (0x0D) is its last byte.
        Assert.AreEqual(13, ReadBigEndianInt32(png, 8), "The first chunk length is 13 bytes.");
        Assert.AreEqual((byte)0x49, png[12], "The first chunk is IHDR.");
        Assert.AreEqual((byte)0x48, png[13], "The first chunk is IHDR.");
        Assert.AreEqual((byte)0x44, png[14], "The first chunk is IHDR.");
        Assert.AreEqual((byte)0x52, png[15], "The first chunk is IHDR.");

        int width = ReadBigEndianInt32(png, 16);
        int height = ReadBigEndianInt32(png, 20);
        return (width, height);
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private static uint CountGdiObjects()
    {
        return SolidColorWindowNative.GetGuiResources(SolidColorWindowNative.GetCurrentProcess(), 0);
    }

    /// <summary>A real top-level window that paints its whole client area in one solid colour.</summary>
    private sealed class SolidColorWindow : IDisposable
    {
        private const string WindowClassName = "SoulComputerUse.ControlCapture.SolidColorWindow";

        /// <summary>The red, green and blue level the whole client area is painted with.</summary>
        internal const int PaintedLevel = 226;

        private static readonly WindowProcedure WindowDelegate = HandleWindowMessage;
        private static readonly object RegistrationLock = new();

        private static ushort _classAtom;

        private nint _handle;

        private SolidColorWindow(nint handle)
        {
            _handle = handle;
        }

        internal nint Handle => _handle;

        internal static SolidColorWindow Create(int width, int height)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "The window width must be positive.");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "The window height must be positive.");
            }

            lock (RegistrationLock)
            {
                _ = EnsureClassRegistered();
                nint handle = SolidColorWindowNative.CreateWindowExW(
                    0,
                    WindowClassName,
                    "SoulComputerUse control capture test",
                    SolidColorWindowNative.WsOverlappedWindow | SolidColorWindowNative.WsVisible,
                    WindowLeft,
                    WindowTop,
                    width,
                    height,
                    0,
                    0,
                    SolidColorWindowNative.GetModuleHandleW(null),
                    0);
                if (handle == 0)
                {
                    throw Failure("CreateWindowExW", Marshal.GetLastWin32Error());
                }

                if (!SolidColorWindowNative.UpdateWindow(handle))
                {
                    _ = SolidColorWindowNative.DestroyWindow(handle);
                    throw Failure("UpdateWindow", Marshal.GetLastWin32Error());
                }

                return new SolidColorWindow(handle);
            }
        }

        public void Dispose()
        {
            nint handle = _handle;
            _handle = 0;
            if (handle != 0)
            {
                _ = SolidColorWindowNative.DestroyWindow(handle);
            }
        }

        private static nint HandleWindowMessage(nint handle, uint message, nint wParam, nint lParam)
        {
            switch (message)
            {
                case SolidColorWindowNative.WmPaint:
                    Paint(handle);
                    return 0;
                case SolidColorWindowNative.WmEraseBkgnd:
                    return 1;
                default:
                    return SolidColorWindowNative.DefWindowProcW(handle, message, wParam, lParam);
            }
        }

        private static void Paint(nint handle)
        {
            nint deviceContext = SolidColorWindowNative.BeginPaint(handle, out PaintStruct paintStruct);
            if (deviceContext == 0)
            {
                return;
            }

            try
            {
                if (!SolidColorWindowNative.GetClientRect(handle, out WindowRect client))
                {
                    throw Failure("GetClientRect", Marshal.GetLastWin32Error());
                }

                using Graphics graphics = Graphics.FromHdc(deviceContext);
                using SolidBrush fill = new(Color.FromArgb(255, PaintedLevel, PaintedLevel, PaintedLevel));
                graphics.FillRectangle(fill, 0, 0, client.Right - client.Left, client.Bottom - client.Top);
            }
            finally
            {
                _ = SolidColorWindowNative.EndPaint(handle, ref paintStruct);
            }
        }

        private static ushort EnsureClassRegistered()
        {
            if (_classAtom != 0)
            {
                return _classAtom;
            }

            nint instance = SolidColorWindowNative.GetModuleHandleW(null);
            if (instance == 0)
            {
                throw Failure("GetModuleHandleW", Marshal.GetLastWin32Error());
            }

            WindowClassEx windowClass = new()
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Style = 0,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowDelegate),
                ClassExtraBytes = 0,
                WindowExtraBytes = 0,
                Instance = instance,
                Icon = 0,
                Cursor = SolidColorWindowNative.LoadCursorW(0, SolidColorWindowNative.ArrowCursor),
                BackgroundBrush = 0,
                MenuName = null,
                ClassName = WindowClassName,
                SmallIcon = 0,
            };
            _classAtom = SolidColorWindowNative.RegisterClassExW(ref windowClass);
            if (_classAtom == 0)
            {
                throw Failure("RegisterClassExW", Marshal.GetLastWin32Error());
            }

            return _classAtom;
        }

        private static System.ComponentModel.Win32Exception Failure(string operation, int errorCode)
        {
            return new System.ComponentModel.Win32Exception(
                errorCode,
                operation
                + " failed with Win32 error "
                + errorCode.ToString(CultureInfo.InvariantCulture)
                + ".");
        }

        private delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);
    }

    /// <summary>The user32 and kernel32 calls the test window needs.</summary>
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use 'LibraryImport' attributes instead of 'DllImport'",
        Justification = "A handful of plain user32 calls in a test fixture, kept beside the window they create.")]
    private static class SolidColorWindowNative
    {
        internal const uint WmPaint = 0x000F;
        internal const uint WmEraseBkgnd = 0x0014;
        internal const uint WsVisible = 0x10000000;
        internal const uint WsOverlappedWindow = 0x00CF0000;
        internal const nint ArrowCursor = 32512;

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern nint CreateWindowExW(
            uint exStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
        internal static extern nint DefWindowProcW(nint handle, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint handle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateWindow(nint handle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint handle, out WindowRect rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint BeginPaint(nint handle, out PaintStruct paintStruct);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EndPaint(nint handle, ref PaintStruct paintStruct);

        [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
        internal static extern nint LoadCursorW(nint instance, nint cursorName);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetGuiResources(nint process, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GetCurrentProcess();
    }

    /// <summary>One window rectangle in screen pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    /// <summary>The Windows <c>PAINTSTRUCT</c>, sized for x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        internal nint DeviceContext;
        internal int Erase;
        internal WindowRect Paint;
        internal int Restore;
        internal int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] Reserved;
    }

    /// <summary>The Windows <c>WNDCLASSEXW</c>.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }
}
