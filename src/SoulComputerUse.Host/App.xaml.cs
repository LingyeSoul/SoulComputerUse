using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting;
using SoulComputerUse.Windows.Integrity;
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
        // A host started directly by a double-click or Start-Process bypasses the launcher, so it verifies
        // its own file before it reads any argument or causes any machine effect.
        if (!SelfIntegrity.IsAccepted(SelfIntegrity.Check()))
        {
            ShowStartupError(SelfIntegrity.FailureMessage);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);

        if (Array.IndexOf(e.Args, SnapHitTestSpikeArgument) >= 0)
        {
            ShowSnapComparisonWindows();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (NewSessionArguments.IsRequested(e.Args))
        {
            if (NewSessionArguments.HasExtraArguments(e.Args))
            {
                ShowStartupError(NewSessionArguments.Usage());
                Shutdown(2);
                return;
            }

            // The new-session mode creates an ActiveX control and pumps its window messages, so it runs on
            // its own STA thread; the dispatcher only waits for the exit code.
            int exitCode = NewSessionThread.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

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
            }            catch (Exception error) when (IsAttachFailure(error))
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

            // The window is destroyed only after the operator confirmed the close, so the contract's
            // teardown sequence runs here rather than in OnExit: entering closing, logging the child
            // session off and releasing the lock must happen while the process still owns them.
            RunAttachedTeardown();
        }
        else
        {
            ShowStartupError(
                "Usage: SoulComputerUse.Host.exe --new-session"
                + Environment.NewLine
                + "       SoulComputerUse.Host.exe --attach-agent <uuid> --agent-nonce <32-hex>");
            Shutdown(2);
            return;
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
    /// Runs the attached session's shutdown sequence and records the contract exit code. A failed step
    /// is reported through the exit code; the cleanup in <see cref="OnExit"/> still runs afterwards and
    /// is idempotent.
    /// </summary>
    private void RunAttachedTeardown()
    {
        if (_attachedSession is null)
        {
            return;
        }

        try
        {
            int exitCode = Task.Run(() => _attachedSession.CloseAsync(CancellationToken.None)).GetAwaiter().GetResult();
            if (exitCode != 0)
            {
                Console.Error.WriteLine("E_TEARDOWN The session could not be shut down cleanly.");
                Environment.ExitCode = exitCode;
            }
        }
        catch (Exception error) when (IsAttachFailure(error))
        {
            Console.Error.WriteLine($"E_TEARDOWN The session shutdown failed ({error.GetType().Name}).");
            Environment.ExitCode = 1;
        }
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

        if (_attachedSession is not null)
        {
            _attachedSession.StateChanged += state =>
            {
                if (_productWindow is null
                    || _attachedSession.Status is not SessionStatus current)
                {
                    return;
                }

                BlockedCause? blockedCause = state == SessionState.Blocked
                    ? BlockedCause.ProtectedDesktop
                    : null;
                try
                {
                    _productWindow.PostStatus(SessionStatusVisuals.Describe(
                        state,
                        current.AuthTier,
                        current.GateStrength,
                        blockedCause));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Console.Error.WriteLine(
                        "E_MANUAL_INTERVENTION The product window status could not be updated ("
                        + error.GetType().Name
                        + ").");
                }
            };
        }

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

    internal static void ShowStartupError(string message)    {
        // The message is only useful where somebody can read it. A double-clicked WinExe has neither a
        // console nor a redirected handle, so it gets a dialog; any console, pipe or file reader gets the
        // text on the error stream and the process exits with the failure code instead of blocking.
        if (HostConsole.HasUsableErrorStream())
        {
            Console.Error.WriteLine(message);
            return;
        }

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
