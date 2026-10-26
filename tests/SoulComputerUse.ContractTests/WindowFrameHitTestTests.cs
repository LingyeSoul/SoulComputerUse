using System;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the resize handles of a frameless window: without them a window with no
/// non-client area cannot be resized at all.
/// </summary>
[TestClass]
public sealed class WindowFrameHitTestTests
{
    private const int Width = 800;
    private const int Height = 600;
    private const int Border = 8;

    [TestMethod]
    public void TheMiddleOfTheWindowIsNotAResizeHandle()
    {
        Assert.AreEqual(ResizeEdge.None, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, 400, 300));
        Assert.AreEqual(
            ResizeEdge.None,
            WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, Border, Border),
            "The handle is half open: the first pixel inside the border is already the client area.");
    }

    [TestMethod]
    public void EachEdgeAndCornerResolvesToItsOwnHandle()
    {
        Assert.AreEqual(ResizeEdge.Left, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, 0, 300));
        Assert.AreEqual(ResizeEdge.Left, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, Border - 1, 300));
        Assert.AreEqual(ResizeEdge.Right, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, Width - 1, 300));
        Assert.AreEqual(ResizeEdge.Right, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, Width - Border, 300));
        Assert.AreEqual(ResizeEdge.Top, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, 400, 0));
        Assert.AreEqual(ResizeEdge.Bottom, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, 400, Height - 1));

        Assert.AreEqual(ResizeEdge.TopLeft, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, 0, 0));
        Assert.AreEqual(ResizeEdge.TopRight, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, Width - 1, 0));
        Assert.AreEqual(ResizeEdge.BottomLeft, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, 0, Height - 1));
        Assert.AreEqual(ResizeEdge.BottomRight, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, Width - 1, Height - 1));
    }

    [TestMethod]
    public void PointsOutsideTheWindowAreNotHandles()
    {
        Assert.AreEqual(ResizeEdge.None, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, -1, 300));
        Assert.AreEqual(ResizeEdge.None, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, Width, 300));
        Assert.AreEqual(ResizeEdge.None, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, 400, -1));
        Assert.AreEqual(ResizeEdge.None, WindowFrameHitTest.ResolveResizeEdge(Width, Height, Border, 400, Height));
    }

    [TestMethod]
    public void HandlesAreRefusedForImpossibleGeometry()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WindowFrameHitTest.ResolveResizeEdge(0, Height, Border, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WindowFrameHitTest.ResolveResizeEdge(Width, 0, Border, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WindowFrameHitTest.ResolveResizeEdge(Width, Height, 0, 0, 0));
    }

    [TestMethod]
    public void EachHandleMapsToItsHitTestCode()
    {
        Assert.AreEqual(10, WindowFrameHitTest.ToHitTestCode(ResizeEdge.Left));
        Assert.AreEqual(11, WindowFrameHitTest.ToHitTestCode(ResizeEdge.Right));
        Assert.AreEqual(12, WindowFrameHitTest.ToHitTestCode(ResizeEdge.Top));
        Assert.AreEqual(13, WindowFrameHitTest.ToHitTestCode(ResizeEdge.TopLeft));
        Assert.AreEqual(14, WindowFrameHitTest.ToHitTestCode(ResizeEdge.TopRight));
        Assert.AreEqual(15, WindowFrameHitTest.ToHitTestCode(ResizeEdge.Bottom));
        Assert.AreEqual(16, WindowFrameHitTest.ToHitTestCode(ResizeEdge.BottomLeft));
        Assert.AreEqual(17, WindowFrameHitTest.ToHitTestCode(ResizeEdge.BottomRight));
        Assert.AreEqual(1, WindowFrameHitTest.ToHitTestCode(ResizeEdge.None));
    }
}
