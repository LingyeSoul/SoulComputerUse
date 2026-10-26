using System;
using System.Windows;
using System.Windows.Interop;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.Host;

/// <summary>
/// Host process entry point. The only mode implemented today is the M1 product window: a real
/// top-level window whose client area is the whole frame, with the self-drawn caption on top and the
/// letterboxed desktop picture below it.
/// </summary>
public partial class App : Application
{
    private const string SnapHitTestSpikeArgument = "--snap-hit-test-spike";
    private const int ProductWindowWidth = 1200;
    private const int ProductWindowHeight = 800;

    private SnapHitTestSpikeWindow? _snapHitTestSpikeWindow;
    private ProductWindow? _productWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Array.IndexOf(e.Args, SnapHitTestSpikeArgument) >= 0)
        {
            ShowSnapComparisonWindows();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ShowProductWindow();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _snapHitTestSpikeWindow?.Dispose();
        _productWindow?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Shows the product window and pumps its messages on this thread. The loop ends when the window
    /// is destroyed, which only happens after the close confirmation was answered.
    /// </summary>
    private void ShowProductWindow()
    {
        SessionStatusVisual status = SessionStatusVisuals.Describe(
            SessionState.Starting,
            AuthTier.HelloEnrolled,
            GateStrength.Strong,
            null);

        _productWindow = ProductWindow.Create(
            ProductWindowWidth,
            ProductWindowHeight,
            SessionStatusVisuals.BuildTitle("example-session"),
            ConfirmClose,
            status);

        MessageLoop.Run();
    }

    /// <summary>
    /// The close confirmation. The dialog is modal to the product window, cancel is the default and
    /// Escape means cancel, so only an explicit choice ends the session.
    /// </summary>
    private bool ConfirmClose()
    {
        ProductWindow window = _productWindow
            ?? throw new InvalidOperationException("The close confirmation was asked without a product window.");
        return CloseConfirmationDialog.Confirm(window.Handle, CloseConfirmationConfig.Default);
    }

    /// <summary>
    /// Window A keeps the native system caption, so Windows draws and hit-tests its maximize
    /// button. Window B answers HTMAXBUTTON from a client-area hot zone with a self-drawn caption.
    /// Comparing the two tells "our implementation is wrong" apart from "this machine has the
    /// Snap flyout switched off".
    /// </summary>
    private void ShowSnapComparisonWindows()
    {
        Window controlWindow = new()
        {
            Title = "A — 原生系统标题栏（系统自己画最大化按钮）",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 60,
            Top = 60,
            Width = 820,
            Height = 380,
        };
        controlWindow.Show();

        nint controlHandle = new WindowInteropHelper(controlWindow).Handle;
        WindowPlacement.MoveAndResize(controlHandle, 60, 60, 860, 420);

        _snapHitTestSpikeWindow = SnapHitTestSpikeWindow.CreateWithoutSystemCaption(800, 340);
        WindowPlacement.MoveAndResize(_snapHitTestSpikeWindow.Handle, 60, 520, 860, 420);
    }
}
