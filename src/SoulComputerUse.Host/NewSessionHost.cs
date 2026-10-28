using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
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
    private static readonly (int Width, int Height) Desktop = SessionDesktopSize.PrimaryDisplay();

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
    /// <remarks>
    /// <para>
    /// The split between this thread and the start thread is what makes the window usable while the session
    /// is being brought up. Everything that talks to the control has to run here, because an ActiveX control
    /// belongs to the thread that created it: it is created, attached and driven from this thread only.
    /// </para>
    /// <para>
    /// The three start phases, however, block: the connection waits for the operator's logon and the
    /// after-logon segment waits for the agent's pipe. Running them here would leave this thread inside a
    /// synchronous wait, and this thread is the one that has to dispatch the window messages a drag, a
    /// resize or a minimize needs; the window would look correct and answer <c>HTCAPTION</c> while every
    /// real click did nothing. They therefore run on their own thread, and this thread enters the message
    /// loop immediately after the window exists.
    /// </para>
    /// <para>
    /// The start thread never touches the control directly. The connected-state reader it needs is posted to
    /// this thread, so the control is still only ever read from its own thread.
    /// </para>
    /// </remarks>
    internal int Run()
    {
        try
        {
            Launch();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A start that never reached ready must not leave its machine footprint behind: the child
            // session it created answers a later connection without asking for a logon, the start-up
            // entry it mounted would start an agent on the next logon of this account, and the ownership
            // record or the machine-wide switch may still be held. Clean every one of them before the
            // refusal is shown, so the operator can retry immediately.
            bool cleanupFailed = CleanupFailedStart();

            string description = DescribeFailure(error);
            if (cleanupFailed)
            {
                description += Environment.NewLine
                    + "E_MANUAL_INTERVENTION The failed start could not release every resource; the ownership record or the machine-wide child-session switch may remain.";
            }

            App.ShowStartupError(description);
            Dispose();
            return 1;
        }

        MessageLoop.Run();
        return CloseSession();
    }

    /// <summary>
    /// Removes the start-up entry the before-connect segment mounted, on every exit path. A removal
    /// that fails is reported, because a leftover entry starts the agent on this account's next logon
    /// and the agent's own console gate is the only thing that would stop it.
    /// </summary>
    private static void RemoveAgentBootstrap()
    {
        try
        {
            ChildSessionAgentBootstrap.Remove();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(
                SessionErrorCodeNames.ToWireName(SessionErrorCode.ManualIntervention)
                + " The child-session agent start-up entry could not be removed; the agent will refuse to run on the next console logon. Cause: "
                + error.Message);
        }
    }

    /// <summary>
    /// Releases everything a start that never reached ready may have taken: the child session it
    /// created, the machine-wide child-session switch, the start-up entry, the agent connection, the
    /// ownership record and finally the single-instance lock. Every step is attempted even when an
    /// earlier one fails, so one broken port cannot hide another.
    /// </summary>
    /// <returns>True when at least one release step failed; the caller must report manual intervention.</returns>
    private bool CleanupFailedStart()
    {
        List<Exception> failures = [];

        // A child session left logged on would answer the next connection without a logon, so the
        // session this run created is logged off first. The method reports its own failure.
        ReleaseUnfinishedChildSession();

        // The switch is machine-wide; only this run may restore it, and only when the before-connect
        // segment recorded that this run turned it on.
        BeforeConnectResult? beforeConnect = _beforeConnect?.Result;
        if (beforeConnect is not null && beforeConnect.ChildSessionsOpenedByUs)
        {
            try
            {
                new ChildSessionsSwitchRestore().Restore();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Add(error);
            }
        }

        RemoveAgentBootstrap();

        // The owner pipe stops before the record disappears, then the lock is released. The release
        // helper attempts all three steps and aggregates failures, so a failed delete cannot leave the
        // lock held until process exit.
        if (beforeConnect is not null)
        {
            try
            {
                new OwnerAndLockRelease(_recordStore, _ownerPipe, beforeConnect.LockHandle).Release();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Add(error);
            }
        }
        else if (_ownerPipe is not null)
        {
            try
            {
                _ownerPipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Add(error);
            }
        }

        AgentConnection? agent = _afterLogon?.Segment?.AgentConnection;
        if (agent is not null)
        {
            try
            {
                CloseAgent(agent.Client);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Add(error);
            }
        }

        return failures.Count > 0;
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

    /// <summary>Runs the three start phases on this thread and records their outcome.</summary>
    private void Launch()
    {
        AgentLaunchRequest launchRequest = _plan.CreateAgentLaunchRequest();

        // The control is created on this STA thread before anything switches: an ActiveX control can only
        // be instantiated here, and every later property write must reach this same instance.
        RdpControlSurface surface = new();
        _controlSurface = surface;

        ProductionOwnerPipeHost ownerPipe = new();
        _ownerPipe = ownerPipe;

        CapturingBeforeConnect capturing = new(new BeforeConnectSegment(
            _options,
            new ProductionLockAcquire(),
            _recordStore,
            CreateAuthTierSource(),
            new ProductionLeftoverSweep(),
            new ProductionChildSessionsGate(_plan.ApplicationDirectory, ElevationTimeoutMilliseconds),
            new ChildSessionsSwitchRestore(),
            launchRequest,
            surface,
            new DynamicRdpLaunchSurface(surface.Ocx)));
        _beforeConnect = capturing;

        // The window must exist before the connection: the control needs a parent to be attached to, and
        // the operator must see the logon screen the moment the client shows it.
        ShowProductWindow(surface);

        // Every wait in the start hands this clock over to the message pump, so the window keeps answering
        // while the phases block. A plain sleep anywhere on this thread would freeze the caption, the
        // remote desktop control and every click the operator makes while the session is being brought up.
        PumpingWaitClock waitClock = new(() => _productWindow is null || _productWindow.IsDestroyed);
        _afterLogon = new DeferredAfterLogon(
            capturing,
            result => new AfterLogonSegment(
                _options,
                _recordStore,
                new ProductionAgentConnector(
                    _plan.SessionId,
                    _plan.Nonce,
                    AgentClientTimings.Contract,
                    waitClock),
                ownerPipe,
                new WindowsProcessSessionLookup(),
                result,
                UpdateProductStatus));

        // The session snapshot is taken before the connection: the one session id that appears afterwards
        // is the session this run created, so no leftover or another account's session can answer the logon
        // question. Windows puts a fresh child session's own logon screen inside that session, so the user
        // name Windows publishes after accepting a logon is the proof that the operator did the logon.
        _logonTracker = ChildSessionLogonTracker.Start(
            new WtsSessionSource(),
            WtsSessionProbe.GetActiveConsoleSessionId());

        SessionStartPhaseRunner runner = new(
            capturing,
            new ConnectingOpener(
                new SessionConnectionOpener(
                    surface,
                    new ProductionSessionConnector(),
                    _plan.DesktopWidth,
                    _plan.DesktopHeight),
                new SessionConnectionWaiter(
                    () => ReadConnectedState(surface),
                    LogonPollInterval,
                    LogonTimeout,
                    waitClock,
                    _logonTracker)),
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
            Desktop.Width,
            Desktop.Height,
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
            FittedClientWidth(),
            FittedClientHeight(),
            SessionStatusVisuals.BuildTitle(SessionLabel),
            ConfirmClose,
            status);
        _productWindow = window;
        window.SetSessionSize(_plan.DesktopWidth, _plan.DesktopHeight);
        window.AttachPictureSurface(surface.WindowHandle, surface.Adopt);
        surface.Show();

        if (_diagnostics)
        {
            Console.Error.WriteLine(
                "trace picture-surface=" + surface.WindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture)
                + " adopted=" + window.PictureSurface.ToInt64().ToString(CultureInfo.InvariantCulture)
                + " picture=" + DescribePicture());
        }
    }

    /// <summary>
    /// The fitted client size computed for this session, kept once for the width and height calls.
    /// </summary>
    private (int Width, int Height)? _fittedClientSize;

    private int FittedClientWidth() => FittedClient().Width;

    private int FittedClientHeight() => FittedClient().Height;

    /// <summary>
    /// Queues a runtime status for the product window. The owner pipe calls this from a worker
    /// thread; the window applies it through its own message loop.
    /// </summary>
    private void UpdateProductStatus(SessionState state)
    {
        ProductWindow? window = _productWindow;
        if (window is null || window.IsDestroyed)
        {
            return;
        }

        AuthTier? tier = _beforeConnect?.AuthTier;
        if (tier is null || tier == AuthTier.None)
        {
            return;
        }

        BlockedCause? blockedCause = state == SessionState.Blocked
            ? BlockedCause.ProtectedDesktop
            : null;

        try
        {
            window.PostStatus(SessionStatusVisuals.Describe(
                state,
                tier.Value,
                AuthTierRules.StrengthFor(tier.Value),
                blockedCause));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(
                "E_MANUAL_INTERVENTION The product window status could not be updated ("
                + error.GetType().Name
                + ").");
        }
    }

    /// <summary>
    /// Sizes the window so the letterboxed picture fills the area below the caption with no black bars:
    /// a fixed client size would letterbox every session whose ratio differs from it, which reads as a
    /// broken picture rather than a window.
    /// </summary>
    private (int Width, int Height) FittedClient()
    {
        _fittedClientSize ??= SessionWindowSizing.ClientForSession(_plan.DesktopWidth, _plan.DesktopHeight);
        return _fittedClientSize.Value;
    }

    /// <summary>Runs the contract's shutdown sequence and returns the exit code it produced.</summary>
    private int CloseSession()
    {
        BeforeConnectResult? beforeConnect = _beforeConnect?.Result;
        AgentConnection? agent = _afterLogon?.Segment?.AgentConnection;

        if (!_started || beforeConnect is null || agent is null)
        {
            // Nothing reached ready, so the contract's teardown sequence does not apply: the refused phase
            // already released the lock and the record. What must not survive is a child session this run
            // created, because it answers the next connection without asking the operator to log on.
            ReleaseUnfinishedChildSession();
            RemoveAgentBootstrap();
            Dispose();
            return 0;
        }

        try
        {
            RemoveAgentBootstrap();

            // Use the session id recorded at attach time. Re-reading it from the live agent process
            // fails as soon as the agent exits, which would skip teardown exactly when the process
            // needs it most and leave a ready record for the next start to trip over.
            int childSessionId = _afterLogon?.Segment?.SubagentWtsSessionId ?? 0;
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
    /// Reads the control's connected state and, with diagnostics on, writes the facts the logon gate decides
    /// on whenever one of them changes: the state the client reports, the child session this run created and
    /// whether Windows has published a user for it. Only numbers and yes/no are written, so a trail can be
    /// pasted into a report without a path, an account or a machine name.
    /// </summary>
    private int ReadConnectedState(RdpControlSurface surface)
    {
        int state = surface.ConnectedState;
        if (!_diagnostics)
        {
            return state;
        }

        int createdSessionId = _logonTracker?.CreatedSessionId ?? 0;
        bool loggedOn = _logonTracker?.HasLoggedOnUser() ?? false;
        (int State, int CreatedSessionId, bool LoggedOn) facts = (state, createdSessionId, loggedOn);
        if (facts != _lastTracedFacts)
        {
            _lastTracedFacts = facts;
            Console.Error.WriteLine(
                "trace connected-state=" + state.ToString(CultureInfo.InvariantCulture)
                + " created-child-session=" + createdSessionId.ToString(CultureInfo.InvariantCulture)
                + " logged-on=" + (loggedOn ? "yes" : "no")
                + " picture=" + DescribePicture());
        }

        return state;
    }

    private (int State, int CreatedSessionId, bool LoggedOn)? _lastTracedFacts;

    private readonly bool _diagnostics =
        Environment.GetEnvironmentVariable("SOULCOMPUTERUSE_DIAGNOSTICS") == "1";

    private string DescribePicture()
    {
        ProductWindow? window = _productWindow;
        if (window is null)
        {
            return "none";
        }

        return window.PictureBounds.Width.ToString(CultureInfo.InvariantCulture)
            + "x" + window.PictureBounds.Height.ToString(CultureInfo.InvariantCulture)
            + "@" + window.PictureBounds.Left.ToString(CultureInfo.InvariantCulture)
            + "," + window.PictureBounds.Top.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>How long the abort path waits for the child session's logoff to take effect.</summary>
    private static readonly TimeSpan LogoffConfirmationInterval = TimeSpan.FromMilliseconds(250);

    private const int LogoffConfirmationAttempts = 60;

    /// <summary>
    /// Logs off the child session this run created when the start never reached ready. A session left
    /// logged on answers the next connection without asking the operator to log on, which the contract
    /// makes a hard precondition rather than a warning, so the cleanup runs on the abort path too and
    /// reports itself when the logoff cannot be confirmed.
    /// </summary>
    private void ReleaseUnfinishedChildSession()
    {
        int createdSessionId = _logonTracker?.CreatedSessionId ?? 0;
        if (createdSessionId <= 0)
        {
            return;
        }

        try
        {
            WtsSessionTable table = new();
            table.LogOff(createdSessionId);

            // A Windows logoff is asynchronous: the session stays listed for several seconds after a
            // request that succeeded, so the confirmation has to be repeated instead of read once.
            ThreadLogoffSettle settle = new();
            bool stillListed = true;
            for (int attempt = 0; attempt < LogoffConfirmationAttempts && stillListed; attempt++)
            {
                stillListed = false;
                foreach (WtsSessionEntry entry in table.Read())
                {
                    if (entry.Id == createdSessionId)
                    {
                        stillListed = true;
                        break;
                    }
                }

                if (stillListed)
                {
                    settle.Wait(LogoffConfirmationInterval);
                }
            }

            if (stillListed)
            {
                throw new InvalidOperationException(
                    $"Session {createdSessionId} is still listed after the logoff request.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never silent: the leftover silently disables the logon gate of the next run, so the operator
            // has to be able to see this line without turning diagnostics on.
            Console.Error.WriteLine(
                SessionErrorCodeNames.ToWireName(SessionErrorCode.ManualIntervention)
                + " The child session this run created could not be confirmed as logged off, so the next"
                + " connection may skip the logon gate. Session id "
                + createdSessionId.ToString(CultureInfo.InvariantCulture)
                + "; cause: " + error.Message);
        }
    }

    private ChildSessionLogonTracker? _logonTracker;

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

    /// <summary>Describes a failure at the process boundary.</summary>
    private string DescribeFailure(Exception error)
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
        // cause sets the variable and reads the chain from the error stream of that one run, together with
        // the two facts the logon gate decided on: the session this run created and whether Windows had
        // published a user for it by the time the run ended.
        if (Environment.GetEnvironmentVariable("SOULCOMPUTERUSE_DIAGNOSTICS") == "1")
        {
            Exception? current = error;
            while (current is not null)
            {
                described += Environment.NewLine + "  " + current.GetType().Name + ": " + current.Message;
                if (current.Data.Contains(SessionConnectionWaiter.ObservedStatesDataKey)
                    && current.Data[SessionConnectionWaiter.ObservedStatesDataKey] is IReadOnlyList<int> observed)
                {
                    described += Environment.NewLine + "  observed connected states: " + string.Join(",", observed);
                }

                current = current.InnerException;
            }

            described += Environment.NewLine
                + "  created child session: "
                + (_logonTracker?.CreatedSessionId ?? 0).ToString(CultureInfo.InvariantCulture)
                + ", logged on: " + ((_logonTracker?.HasLoggedOnUser() ?? false) ? "yes" : "no");
        }

        return described;
    }
}