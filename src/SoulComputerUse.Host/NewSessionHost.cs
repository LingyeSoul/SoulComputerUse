using System;
using System.Diagnostics;
using System.Threading;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.Hosting.Startup.Production;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Ipc.Agent;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Rdp.Launch;
using SoulComputerUse.Windows.Rdp.SessionLaunch;
using SoulComputerUse.Windows.SessionLifecycle;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.Host;

/// <summary>
/// The host's new-session mode: take the machine-wide lock, connect through the single entry point, let the
/// operator log on in the remote window, attach the agent, serve tool calls, then shut the session down in
/// the contract order.
/// </summary>
/// <remarks>
/// <para>
/// The phase order is the reason this type exists at all. The agent is the child session's alternate shell,
/// so no agent process exists until the operator logs on; a start that attached the agent before the
/// connection would wait for a pipe nobody created. The three phases therefore come from
/// <see cref="SessionStartPhaseRunner"/> and this type only composes their production ports.
/// </para>
/// <para>
/// Every gate stays where it already lives: the lock, the record, the tier, the sweep, the child-session
/// switch and the alternate-shell write belong to <see cref="BeforeConnectSegment"/>; the connection belongs
/// to <see cref="ISessionConnector"/>, whose implementation is the only place that names the connect call;
/// the agent, the recorded child session and the owner pipe belong to <see cref="AfterLogonSegment"/>.
/// </para>
/// </remarks>
internal sealed class NewSessionHost : IDisposable
{
    /// <summary>The desktop the connection requests, in the session's native pixels.</summary>
    private const int DesktopWidth = 1280;

    private const int DesktopHeight = 800;

    /// <summary>How long the elevation helper may take before the prompt counts as stuck.</summary>
    private const int ElevationTimeoutMilliseconds = 60000;

    /// <summary>How often the logon gate looks at the client state.</summary>
    private static readonly TimeSpan LogonPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the operator may take to log on before the start is abandoned.</summary>
    private static readonly TimeSpan LogonTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The placeholder title label; a real session id never appears in a window title.</summary>
    private const string SessionLabel = "session";

    private readonly IOwnerRecordStore _recordStore = OwnerRecordStoreAdapter.ForCurrentUser();
    private readonly SessionStartOptions _options;
    private readonly SessionLaunchPlan _plan;

    private RdpControlSurface? _controlSurface;
    private ProductionOwnerPipeHost? _ownerPipe;
    private CapturingBeforeConnect? _beforeConnect;
    private DeferredAfterLogon? _afterLogon;
    private bool _started;
    private bool _disposed;

    /// <summary>Builds the launch values before anything touches the machine.</summary>
    /// <exception cref="ArgumentException">A launch value cannot describe a usable session.</exception>
    internal NewSessionHost()
    {
        _plan = CreatePlan();
        _options = CreateStartOptions(_plan);
    }

    /// <summary>The starting session id, so a failure can name the session it tried to start.</summary>
    internal string SessionId => _plan.SessionId;

    /// <summary>Runs the whole mode on this thread and returns the process exit code.</summary>
    internal int Run()
    {
        try
        {
            Launch();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            App.ShowStartupError(DescribeFailure(error));
            return 1;
        }

        MessageLoop.Run();
        return CloseSession();
    }

    /// <summary>Disposes the window and the control. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _productWindow?.Dispose();
        _productWindow = null;
        _controlSurface?.Dispose();
        _controlSurface = null;
    }

    private ProductWindow? _productWindow;

    /// <summary>Builds the ports, shows the window and runs the three phases.</summary>
    private void Launch()
    {
        AgentLaunchRequest launchRequest = _plan.CreateAgentLaunchRequest();

        // The control is created on this STA thread before anything switches: an ActiveX control can only
        // be instantiated here, and every later property write must reach this same instance.
        RdpControlSurface surface = new();
        _controlSurface = surface;

        ProductionOwnerPipeHost ownerPipe = new();
        _ownerPipe = ownerPipe;

        BeforeConnectSegment beforeConnect = new(
            _options,
            new ProductionLockAcquire(),
            _recordStore,
            CreateAuthTierSource(),
            new ProductionLeftoverSweep(),
            new ProductionChildSessionsGate(_plan.ApplicationDirectory, ElevationTimeoutMilliseconds),
            launchRequest,
            surface,
            new DynamicRdpLaunchSurface(surface.Ocx));
        CapturingBeforeConnect capturing = new(beforeConnect);
        _beforeConnect = capturing;
        _afterLogon = new DeferredAfterLogon(
            capturing,
            result => new AfterLogonSegment(
                _options,
                _recordStore,
                new ProductionAgentConnector(_plan.SessionId, _plan.Nonce, AgentClientTimings.Contract),
                ownerPipe,
                new WindowsProcessSessionLookup(),
                result));

        // The window must exist before the connection: the control needs a parent to be attached to, and
        // the operator must see the logon screen the moment the client shows it.
        ShowProductWindow(surface);

        SessionStartPhaseRunner runner = new(
            capturing,
            new ConnectingOpener(
                new SessionConnectionOpener(
                    surface,
                    new ProductionSessionConnector(),
                    _plan.DesktopWidth,
                    _plan.DesktopHeight),
                new SessionConnectionWaiter(
                    () => surface.ConnectedState,
                    LogonPollInterval,
                    LogonTimeout,
                    new PumpingWaitClock(() => _productWindow is null || _productWindow.IsDestroyed),
                    ChildSessionHasLoggedOnUser)),
            _afterLogon,
            new PhaseStatusObserver(() => _productWindow, () => capturing.AuthTier));

        runner.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (_afterLogon.Segment?.AgentConnection is null)
        {
            throw new InvalidOperationException(
                "The ready phase reported no agent connection, so no tool call could be served.");
        }

        _started = true;
    }

    private static SessionLaunchPlan CreatePlan()
    {
        return new SessionLaunchPlan(
            Guid.NewGuid().ToString("D"),
            AgentNonce.Create(),
            AppContext.BaseDirectory,
            DesktopWidth,
            DesktopHeight,
            connectToChildSession: true,
            SessionLabel);
    }

    private static SessionStartOptions CreateStartOptions(SessionLaunchPlan plan)
    {
        using Process process = Process.GetCurrentProcess();
        DateTimeOffset processStartTime = new(process.StartTime.ToUniversalTime());
        return new SessionStartOptions(
            plan.SessionId,
            Environment.ProcessId,
            processStartTime,
            DateTimeOffset.UtcNow);
    }

    private static ProductionAuthTierSource CreateAuthTierSource()
    {
        // The Windows Hello availability and enrolment query is not wired into this build, so the probe is
        // told "no usable Hello enrolment". It must never default to true: claiming an enrolled Hello
        // without a real system verdict would assert a strong gate the machine may not have.
        return new ProductionAuthTierSource(static () => false);
    }

    /// <summary>Shows the product window with the control already attached as its picture surface.</summary>
    private void ShowProductWindow(RdpControlSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        SessionStatusVisual status = SessionStatusVisuals.Describe(
            SessionState.Starting,
            AuthTier.SystemCredential,
            GateStrength.Login,
            blockedCause: null);
        ProductWindow window = ProductWindow.Create(
            ProductWindowSize.Width,
            ProductWindowSize.Height,
            SessionStatusVisuals.BuildTitle(SessionLabel),
            ConfirmClose,
            status);
        _productWindow = window;
        window.SetSessionSize(_plan.DesktopWidth, _plan.DesktopHeight);
        window.AttachPictureSurface(surface.WindowHandle);
        surface.Show();
    }

    /// <summary>Runs the contract's shutdown sequence and returns the exit code it produced.</summary>
    private int CloseSession()
    {
        BeforeConnectResult? beforeConnect = _beforeConnect?.Result;
        AgentConnection? agent = _afterLogon?.Segment?.AgentConnection;

        if (!_started || beforeConnect is null || agent is null)
        {
            // Nothing reached ready, so the only thing that can still be held is the lock the refused
            // phase already released; the window and the control are all that is left here.
            Dispose();
            return 0;
        }

        try
        {
            int childSessionId = ReadChildSessionId(agent.AgentProcessId);
            bool openedByUs = beforeConnect.ChildSessionsOpenedByUs;

            SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
                ChildSessionsOpenedByUs: openedByUs,
                ChildSessionNeedsLogOff: childSessionId > 0,
                LockIsHeld: true));

            SessionTeardownContext context = new(
                childSessionId > 0 ? childSessionId : null,
                openedByUs,
                LockIsHeld: true,
                ProcessExitRequested: true);

            SessionTeardownExecutor executor = new(
                new ReportedSessionStateTransition(_recordStore, _options, childSessionId, openedByUs),
                new WtsChildSessionLogOff(new WtsSessionTable()),
                new ChildSessionsSwitchRestore(),
                new OwnerAndLockRelease(_recordStore, _ownerPipe, beforeConnect.LockHandle));

            SessionTeardownReport report = executor.ExecuteAsync(plan, context, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            CloseAgent(agent.Client);
            return report.ExitCode;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(DescribeFailure(error));
            Environment.ExitCode = 1;
            return 1;
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// Reports whether any session other than the console one already has a logged-on user. The client asks
    /// the operator for credentials itself, so the child session never paints its own logon screen and the
    /// connected state goes straight from connecting to connected; a non-empty user name in a child session
    /// is then the proof that Windows accepted a logon. The query never surfaces the name itself.
    /// </summary>
    private static bool ChildSessionHasLoggedOnUser()
    {
        try
        {
            return WtsSessionUserQuery.AnyChildSessionHasLoggedOnUser(WtsSessionProbe.GetActiveConsoleSessionId());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A failed query must not be read as "nobody logged on": that would turn a real logon into a
            // bypass refusal. The gate therefore falls back to the logon-screen evidence only.
            return false;
        }
    }

    private static int ReadChildSessionId(int agentProcessId)    {
        if (agentProcessId <= 0)
        {
            return 0;
        }

        return new WindowsProcessSessionLookup().SessionIdFor(agentProcessId);
    }

    private static void CloseAgent(SoulComputerUse.Core.Protocol.ISessionClient client)
    {
        if (client is IAsyncDisposable disposable)
        {
            disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private bool ConfirmClose()
    {
        ProductWindow window = _productWindow
            ?? throw new InvalidOperationException("The close confirmation was asked without a product window.");
        return CloseConfirmationDialog.Confirm(window.Handle, CloseConfirmationConfig.Default);
    }

    private static string DescribeFailure(Exception error)
    {
        // The process boundary: report the contract code and the fixed explanation, never a stack trace,
        // whose absolute paths the log whitelist forbids.
        string described = error switch
        {
            SessionPhaseException phase => SessionErrorCodeNames.ToWireName(phase.Code) + " " + phase.Message,
            SessionStartException start => SessionErrorCodeNames.ToWireName(start.Code) + " " + start.Message,
            SoulComputerUse.Core.Protocol.SessionOperationException operation =>
                SessionErrorCodeNames.ToWireName(operation.Code) + " " + operation.Message,
            _ => "E_MANUAL_INTERVENTION " + error.Message,
        };

        // Diagnostics are off by default because an inner failure may name a path. A developer who needs the
        // cause sets the variable and reads the chain from the error stream of that one run.
        if (Environment.GetEnvironmentVariable("SOULCOMPUTERUSE_DIAGNOSTICS") == "1")
        {
            Exception? current = error;
            while (current is not null)
            {
                described += Environment.NewLine + "  " + current.GetType().Name + ": " + current.Message;
                current = current.InnerException;
            }
        }

        return described;
    }
}