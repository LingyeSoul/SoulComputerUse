using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Desktop;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Exercises desktop capture and input planning. Every input case uses a recording sink, so no test
/// moves the real pointer, clicks a real button or types into a real window.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DesktopSurfaceTests
{
    /// <summary>Maps the inclusive edges of a 1920x1080 screen onto the full native range.</summary>
    [TestMethod]
    public void NormalizationMapsTheScreenEdgesToTheFullRange()
    {
        DesktopScreen screen = new(0, 0, 1920, 1080);

        Assert.AreEqual(0, DesktopInputPlanner.NormalizeCoordinate(screen.X, screen.X, screen.Width));
        Assert.AreEqual(0, DesktopInputPlanner.NormalizeCoordinate(screen.Y, screen.Y, screen.Height));
        Assert.AreEqual(65535, DesktopInputPlanner.NormalizeCoordinate(1919, screen.X, screen.Width));
        Assert.AreEqual(65535, DesktopInputPlanner.NormalizeCoordinate(1079, screen.Y, screen.Height));
    }

    /// <summary>Maps the centre of the same screen to the middle of the native range.</summary>
    [TestMethod]
    public void NormalizationMapsTheScreenCentreInsideTheRange()
    {
        DesktopScreen screen = new(0, 0, 1920, 1080);

        int centreX = DesktopInputPlanner.NormalizeCoordinate(960, screen.X, screen.Width);
        int centreY = DesktopInputPlanner.NormalizeCoordinate(540, screen.Y, screen.Height);

        Assert.AreEqual(32784, centreX);
        Assert.AreEqual(32797, centreY);
        Assert.IsTrue(centreX is > 0 and < 65535, "The horizontal centre must stay strictly inside the native range.");
        Assert.IsTrue(centreY is > 0 and < 65535, "The vertical centre must stay strictly inside the native range.");
    }

    /// <summary>Rejects every coordinate on or beyond the screen edge, and a non-positive extent.</summary>
    [TestMethod]
    public void NormalizationRejectsCoordinatesOutsideTheScreen()
    {
        DesktopScreen screen = new(0, 0, 1920, 1080);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DesktopInputPlanner.NormalizeCoordinate(-1, screen.X, screen.Width));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DesktopInputPlanner.NormalizeCoordinate(1920, screen.X, screen.Width));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DesktopInputPlanner.NormalizeCoordinate(-1, screen.Y, screen.Height));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DesktopInputPlanner.NormalizeCoordinate(1080, screen.Y, screen.Height));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DesktopInputPlanner.NormalizeCoordinate(0, 0, 0));
    }

    /// <summary>Keeps the native range anchored to a virtual screen that starts left of the primary display.</summary>
    [TestMethod]
    public void NormalizationHandlesANegativeScreenOrigin()
    {
        DesktopScreen screen = new(-1920, 0, 1920, 1080);

        Assert.AreEqual(0, DesktopInputPlanner.NormalizeCoordinate(-1920, screen.X, screen.Width));
        Assert.AreEqual(65535, DesktopInputPlanner.NormalizeCoordinate(-1, screen.X, screen.Width));
    }

    /// <summary>A move event carries the normalized corner values and the absolute virtual-desktop flags.</summary>
    [TestMethod]
    public void CreateMoveUsesNormalizedAbsoluteVirtualDeskCoordinates()
    {
        DesktopScreen screen = new(0, 0, 1920, 1080);

        INPUT move = DesktopInputPlanner.CreateMove(screen, 1919, 1079);

        Assert.AreEqual(DesktopInputFlags.InputMouse, move.Type);
        Assert.AreEqual(65535, move.Data.Mouse.Dx);
        Assert.AreEqual(65535, move.Data.Mouse.Dy);
        uint expectedFlags = DesktopInputFlags.MouseEventFMove
            | DesktopInputFlags.MouseEventFAbsolute
            | DesktopInputFlags.MouseEventFVirtualDesk;
        Assert.AreEqual(expectedFlags, move.Data.Mouse.Flags);
    }

    /// <summary>A refused coordinate must throw before any input reaches the sink.</summary>
    [TestMethod]
    public void MovePointerRejectsCoordinatesOutsideTheScreen()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => surface.MovePointer(screen.X - 1, screen.Y));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => surface.MovePointer(screen.X, screen.Y + screen.Height));
        Assert.IsEmpty(recorder.Batches, "A refused coordinate must not reach the input sink.");
    }

    /// <summary>One left click is a move followed by exactly one down/up pair.</summary>
    [TestMethod]
    public void LeftClickSendsMoveThenPrimaryDownAndUp()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        surface.Click(screen.X, screen.Y, MouseButton.Left, 1);

        INPUT[] events = SingleBatch(recorder);
        Assert.HasCount(3, events);
        AssertMouseMove(events[0]);
        AssertMouseButton(events[1], DesktopInputFlags.MouseEventFLeftDown);
        AssertMouseButton(events[2], DesktopInputFlags.MouseEventFLeftUp);
    }

    /// <summary>One right click uses the secondary button flags.</summary>
    [TestMethod]
    public void RightClickSendsSecondaryDownAndUp()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        surface.Click(screen.X, screen.Y, MouseButton.Right, 1);

        INPUT[] events = SingleBatch(recorder);
        Assert.HasCount(3, events);
        AssertMouseMove(events[0]);
        AssertMouseButton(events[1], DesktopInputFlags.MouseEventFRightDown);
        AssertMouseButton(events[2], DesktopInputFlags.MouseEventFRightUp);
    }

    /// <summary>One middle click uses the middle button flags.</summary>
    [TestMethod]
    public void MiddleClickSendsMiddleDownAndUp()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        surface.Click(screen.X, screen.Y, MouseButton.Middle, 1);

        INPUT[] events = SingleBatch(recorder);
        Assert.HasCount(3, events);
        AssertMouseMove(events[0]);
        AssertMouseButton(events[1], DesktopInputFlags.MouseEventFMiddleDown);
        AssertMouseButton(events[2], DesktopInputFlags.MouseEventFMiddleUp);
    }

    /// <summary>A triple click emits three consecutive down/up pairs after the move.</summary>
    [TestMethod]
    public void TripleClickSendsThreeDownUpPairsInOrder()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        surface.Click(screen.X, screen.Y, MouseButton.Left, 3);

        INPUT[] events = SingleBatch(recorder);
        Assert.HasCount(7, events);
        AssertMouseMove(events[0]);

        uint[] expectedFlags =
        [
            DesktopInputFlags.MouseEventFLeftDown,
            DesktopInputFlags.MouseEventFLeftUp,
            DesktopInputFlags.MouseEventFLeftDown,
            DesktopInputFlags.MouseEventFLeftUp,
            DesktopInputFlags.MouseEventFLeftDown,
            DesktopInputFlags.MouseEventFLeftUp,
        ];

        for (int index = 0; index < expectedFlags.Length; index++)
        {
            AssertMouseButton(events[index + 1], expectedFlags[index]);
        }
    }

    /// <summary>One vertical wheel notch travels unchanged in mouseData.</summary>
    [TestMethod]
    public void VerticalScrollSendsOneWheelEventWithTheRawDelta()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        surface.Scroll(screen.X, screen.Y, 0, 120);

        INPUT[] events = SingleBatch(recorder);
        Assert.HasCount(2, events);
        AssertMouseMove(events[0]);
        Assert.AreEqual(DesktopInputFlags.InputMouse, events[1].Type);
        Assert.AreEqual(DesktopInputFlags.MouseEventFWheel, events[1].Data.Mouse.Flags);
        Assert.AreEqual(120u, events[1].Data.Mouse.MouseData);
    }

    /// <summary>One horizontal wheel notch keeps the signed delta in mouseData.</summary>
    [TestMethod]
    public void HorizontalScrollSendsOneHorizontalWheelEventWithTheRawDelta()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        surface.Scroll(screen.X, screen.Y, -120, 0);

        INPUT[] events = SingleBatch(recorder);
        Assert.HasCount(2, events);
        AssertMouseMove(events[0]);
        Assert.AreEqual(DesktopInputFlags.InputMouse, events[1].Type);
        Assert.AreEqual(DesktopInputFlags.MouseEventFHWheel, events[1].Data.Mouse.Flags);
        Assert.AreEqual(unchecked((uint)-120), events[1].Data.Mouse.MouseData);
    }

    /// <summary>A diagonal scroll sends the vertical event first and the horizontal event second.</summary>
    [TestMethod]
    public void DiagonalScrollSendsVerticalThenHorizontalEvents()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);

        DesktopScreen screen = surface.GetScreen();
        surface.Scroll(screen.X, screen.Y, 120, -240);

        INPUT[] events = SingleBatch(recorder);
        Assert.HasCount(3, events);
        AssertMouseMove(events[0]);
        Assert.AreEqual(DesktopInputFlags.MouseEventFWheel, events[1].Data.Mouse.Flags);
        Assert.AreEqual(unchecked((uint)-240), events[1].Data.Mouse.MouseData);
        Assert.AreEqual(DesktopInputFlags.MouseEventFHWheel, events[2].Data.Mouse.Flags);
        Assert.AreEqual(120u, events[2].Data.Mouse.MouseData);
    }

    /// <summary>Ctrl+Alt+Delete is pressed in order and released in reverse order.</summary>
    [TestMethod]
    public void KeyCombinationPressesInOrderAndReleasesInReverse()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);

        surface.PressKeys(new SessionKey[] { SessionKey.Ctrl, SessionKey.Alt, SessionKey.Delete });

        INPUT[] events = SingleBatch(recorder);
        Assert.HasCount(6, events);

        ushort[] expectedKeys = [0x11, 0x12, 0x2E];
        for (int index = 0; index < expectedKeys.Length; index++)
        {
            Assert.AreEqual(DesktopInputFlags.InputKeyboard, events[index].Type);
            Assert.AreEqual(expectedKeys[index], events[index].Data.Keyboard.VirtualKey);
            Assert.AreEqual(0u, events[index].Data.Keyboard.Flags);
            Assert.AreEqual((ushort)0, events[index].Data.Keyboard.ScanCode);
        }

        for (int index = 0; index < expectedKeys.Length; index++)
        {
            INPUT release = events[expectedKeys.Length + index];
            Assert.AreEqual(DesktopInputFlags.InputKeyboard, release.Type);
            Assert.AreEqual(expectedKeys[expectedKeys.Length - 1 - index], release.Data.Keyboard.VirtualKey);
            Assert.AreEqual(DesktopInputFlags.KeyEventFKeyUp, release.Data.Keyboard.Flags);
            Assert.AreEqual((ushort)0, release.Data.Keyboard.ScanCode);
        }
    }

    /// <summary>Every UTF-16 code unit, including both halves of a surrogate pair, becomes its own event pair.</summary>
    [TestMethod]
    public void UnicodeTextSendsOneDownUpPairPerUtf16CodeUnit()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        const string Text = "A😀";

        surface.TypeText(Text);

        INPUT[] events = SingleBatch(recorder);
        Assert.AreEqual(3, Text.Length, "The sample must contain one BMP character and one surrogate pair.");
        Assert.HasCount(6, events);

        for (int codeUnitIndex = 0; codeUnitIndex < Text.Length; codeUnitIndex++)
        {
            INPUT press = events[codeUnitIndex * 2];
            INPUT release = events[(codeUnitIndex * 2) + 1];
            ushort expectedScanCode = Text[codeUnitIndex];

            Assert.AreEqual(DesktopInputFlags.InputKeyboard, press.Type);
            Assert.AreEqual((ushort)0, press.Data.Keyboard.VirtualKey);
            Assert.AreEqual(expectedScanCode, press.Data.Keyboard.ScanCode);
            Assert.AreEqual(DesktopInputFlags.KeyEventFUnicode, press.Data.Keyboard.Flags);

            Assert.AreEqual(DesktopInputFlags.InputKeyboard, release.Type);
            Assert.AreEqual((ushort)0, release.Data.Keyboard.VirtualKey);
            Assert.AreEqual(expectedScanCode, release.Data.Keyboard.ScanCode);
            Assert.AreEqual(
                DesktopInputFlags.KeyEventFUnicode | DesktopInputFlags.KeyEventFKeyUp,
                release.Data.Keyboard.Flags);
        }

        Assert.AreEqual((ushort)0xD83D, events[2].Data.Keyboard.ScanCode);
        Assert.AreEqual((ushort)0xDE00, events[4].Data.Keyboard.ScanCode);
    }

    /// <summary>The text path rejects null explicitly instead of silently typing nothing.</summary>
    [TestMethod]
    public void TypeTextRejectsNull()
    {
        DesktopSurface surface = new(new RecordingSendInput());

        Assert.ThrowsExactly<ArgumentNullException>(() => surface.TypeText(null!));
    }

    /// <summary>A key combination must hold between one and five keys.</summary>
    [TestMethod]
    public void KeyCombinationRejectsACountOutsideTheContract()
    {
        DesktopSurface surface = new(new RecordingSendInput());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => surface.PressKeys(Array.Empty<SessionKey>()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => surface.PressKeys(
                new SessionKey[]
                {
                    SessionKey.Ctrl,
                    SessionKey.Alt,
                    SessionKey.Shift,
                    SessionKey.Win,
                    SessionKey.Delete,
                    SessionKey.Enter,
                }));
    }

    /// <summary>An accepted count lower than the batch size becomes the fixed retryable agent error.</summary>
    [TestMethod]
    public void RejectedInputBecomesARetryableAgentError()
    {
        RecordingSendInput recorder = new() { AcceptedShortfall = 1 };
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        SessionOperationException error = Assert.ThrowsExactly<SessionOperationException>(
            () => surface.MovePointer(screen.X, screen.Y));

        Assert.AreEqual(SessionErrorCode.AgentError, error.Code);
        Assert.AreEqual("input-rejected", error.Message);
        Assert.IsTrue(error.Retryable, "The contract marks E_AGENT_ERROR as retryable.");
    }

    /// <summary>The native INPUT layout must match the x64 structure the injection adapter validates.</summary>
    [TestMethod]
    public void NativeInputLayoutMatchesTheX64Contract()
    {
        Assert.AreEqual(40, Marshal.SizeOf<INPUT>(), "The x64 INPUT structure is 40 bytes.");
    }

    /// <summary>Captures the real desktop once, in memory, and checks the PNG header and dimensions.</summary>
    [TestMethod]
    public void CapturePngReturnsOnePngFrameWithTheVirtualScreenSize()
    {
        RecordingSendInput recorder = new();
        DesktopSurface surface = new(recorder);
        DesktopScreen screen = surface.GetScreen();

        byte[] png = surface.CapturePng();

        Assert.IsNotEmpty(png, "The PNG frame must not be empty.");
        Assert.IsGreaterThanOrEqualTo(24, png.Length, "A PNG frame needs at least the signature and the IHDR size fields.");

        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        CollectionAssert.AreEqual(signature, png[..8], "A PNG frame must start with the PNG signature.");

        int pngWidth = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int pngHeight = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        Assert.AreEqual(screen.Width, pngWidth, "The frame width must match the virtual screen width.");
        Assert.AreEqual(screen.Height, pngHeight, "The frame height must match the virtual screen height.");
        Assert.IsEmpty(recorder.Batches, "Capturing must not inject input.");
    }

    private static INPUT[] SingleBatch(RecordingSendInput recorder)
    {
        Assert.HasCount(1, recorder.Batches, "One desktop action must produce exactly one input batch.");
        return recorder.Batches[0];
    }

    private static void AssertMouseMove(INPUT input)
    {
        Assert.AreEqual(DesktopInputFlags.InputMouse, input.Type);
        uint expectedFlags = DesktopInputFlags.MouseEventFMove
            | DesktopInputFlags.MouseEventFAbsolute
            | DesktopInputFlags.MouseEventFVirtualDesk;
        Assert.AreEqual(expectedFlags, input.Data.Mouse.Flags);
        Assert.AreEqual(0u, input.Data.Mouse.MouseData);
    }

    private static void AssertMouseButton(INPUT input, uint expectedFlags)
    {
        Assert.AreEqual(DesktopInputFlags.InputMouse, input.Type);
        Assert.AreEqual(expectedFlags, input.Data.Mouse.Flags);
        Assert.AreEqual(0u, input.Data.Mouse.MouseData);
    }

    /// <summary>Records the planned native events without calling user32.</summary>
    private sealed class RecordingSendInput : ISendInput
    {
        internal List<INPUT[]> Batches { get; } = [];

        internal int AcceptedShortfall { get; set; }

        public uint Send(IReadOnlyList<INPUT> inputs)
        {
            INPUT[] copy = new INPUT[inputs.Count];
            for (int index = 0; index < inputs.Count; index++)
            {
                copy[index] = inputs[index];
            }

            Batches.Add(copy);
            return (uint)Math.Max(copy.Length - AcceptedShortfall, 0);
        }
    }
}