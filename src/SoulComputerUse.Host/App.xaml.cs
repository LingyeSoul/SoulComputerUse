using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting;
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
    private AgentAttachedSession? _attachedSession;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Array.IndexOf(e.Args, SnapHitTestSpikeArgument) >= 0)
        {
            ShowSnapComparisonWindows();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Length > 0)
        {
            AttachedAgentOptions options;
            try
            {
                options = AttachedAgentCommandLine.Parse(e.Args);
            }
            catch (ArgumentException error)
            {
                ShowStartupError(error.Message + Environment.NewLine + AttachedAgentCommandLine.Usage());
                Shutdown(2);
                return;
            }

            try
            {
                _attachedSession = AgentAttachedSession.CreateForCurrentProcess(options.SessionId, options.Nonce);
                _attachedSession.Start();
            }
            catch (Exception error) when (IsAttachFailure(error))
            {
                ShowStartupError(error.Message);
                Shutdown(1);
                return;
            }

            SessionStatus status = _attachedSession.Status
                ?? throw new InvalidOperationException("The attached session started without a status.");
            SessionState state = SessionStateNames.FromWireName(status.State);
            SessionStatusVisual visual = SessionStatusVisuals.Describe(state, status.AuthTier, status.GateStrength, null);
            ShowProductWindow(visual, "session");
        }
        else
        {
            SessionStatusVisual status = SessionStatusVisuals.Describe(
                SessionState.Starting,
                AuthTier.HelloEnrolled,
                GateStrength.Strong,
                null);
            ShowProductWindow(status, "example-session");
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_attachedSession is not null)
            {
                _attachedSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception error)
        {
            // The exit path is the process boundary: report the failure without a stack trace, whose
            // absolute paths must never reach a console or a log.
            Console.Error.WriteLine($"The attached session cleanup failed ({error.GetType().Name}).");
            Environment.ExitCode = 1;
        }

        _snapHitTestSpikeWindow?.Dispose();
        _productWindow?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Shows the product window and pumps its messages on this thread. The loop ends when the window
    /// is destroyed, which only happens after the close confirmation was answered.
    /// </summary>
    private void ShowProductWindow(SessionStatusVisual status, string sessionLabel)
    {
        _productWindow = ProductWindow.Create(
            ProductWindowWidth,
            ProductWindowHeight,
            SessionStatusVisuals.BuildTitle(sessionLabel),
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

    private static bool IsAttachFailure(Exception error)
    {
        return error is SessionOwnerException
            or SessionOperationException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or TimeoutException
            or UnauthorizedAccessException;
    }

    private static void ShowStartupError(string message)
    {
        _ = MessageBox.Show(
            message,
            "SoulComputerUse",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
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
