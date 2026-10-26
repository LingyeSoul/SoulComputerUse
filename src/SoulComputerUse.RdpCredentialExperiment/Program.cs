using System;
using System.Runtime.InteropServices;
using System.Threading;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.RdpCredentialExperiment;

/// <summary>
/// The RDP child-session experiment. It confirms the machine is in the state the contract requires,
/// then either probes what the control exposes or connects once to a child session with default
/// settings, depending on the mode argument. It lives in its own executable so that a control that
/// crashes takes down this process only, never the host.
/// </summary>
/// <remarks>
/// The experiment never types, reads or persists a credential: it sets the server, the desktop size,
/// the CredSSP support flag and the child-session flag, and reports what the connection shows.
/// </remarks>
internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int PreconditionRefusedExitCode = 2;
    private const int ControlUnavailableExitCode = 3;
    private const string ConnectSwitch = "--connect";
    private const string SessionsSwitch = "--sessions";

    /// <summary>Marks the entry point's thread as a single-threaded apartment.</summary>
    /// <remarks>
    /// An ActiveX control can only be instantiated on an STA thread, and the process entry point is
    /// not one by default. This attribute turns the main thread into an STA before any code runs, which
    /// is what the ActiveX host needs; without it the control cannot be created at all.
    /// </remarks>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: SoulComputerUse.RdpCredentialExperiment <log-path> [--connect]");
            return PreconditionRefusedExitCode;
        }

        string logPath = args[0];
        string mode = args.Length == 2 ? args[1] : string.Empty;
        bool connect = string.Equals(mode, ConnectSwitch, StringComparison.Ordinal);
        bool sessions = string.Equals(mode, SessionsSwitch, StringComparison.Ordinal);
        if (args.Length == 2 && !connect && !sessions)
        {
            Console.Error.WriteLine($"unknown switch '{mode}'; expected {ConnectSwitch} or {SessionsSwitch}");
            return PreconditionRefusedExitCode;
        }
        try
        {
            RdpExperimentPreconditionsResult preconditions = VerifyPreconditions(logPath);
            RdpExperimentLog.Step(logPath, "preconditions", true, RdpExperimentPreconditions.Describe(preconditions));
            if (sessions)
            {
                ReportSessions(logPath);
                return SuccessExitCode;
            }

            return connect ? ConnectRun.Run(logPath) : InspectControl(logPath);
        }
        catch (Exception error)
        {
            RdpExperimentLog.Step(logPath, "run", false, $"{error.GetType().Name}: {error.Message}");
            Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
            return PreconditionRefusedExitCode;
        }
    }

    /// <summary>
    /// Writes the read-only session table to the log: one line per session plus the console session id,
    /// which is the value every later step is compared against.
    /// </summary>
    private static void ReportSessions(string logPath)
    {
        int consoleId = SessionTable.ActiveConsoleSessionId();
        RdpExperimentLog.Step(
            logPath,
            "console-session-id",
            true,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"value = {consoleId}"));

        foreach (SessionEntry session in SessionTable.Read())
        {
            RdpExperimentLog.Step(logPath, "session", true, SessionTable.Describe(session));
        }
    }
    private static RdpExperimentPreconditionsResult VerifyPreconditions(string logPath)
    {
        bool childSessionsEnabled = ChildSessionsProbe.IsEnabled();
        RdpExperimentLog.Step(logPath, "child-sessions", childSessionsEnabled, "system setting");

        object? denyTs = RdpExperimentPreconditions.ReadPolicyValue(ChildSessionsPolicy.RdpInboundValueName);
        object? denyChild = RdpExperimentPreconditions.ReadPolicyValue(ChildSessionsPolicy.ChildConnectionsValueName);
        return RdpExperimentPreconditions.Check(childSessionsEnabled, denyTs, denyChild);
    }

    private static int InspectControl(string logPath)
    {
        try
        {
            InspectRoute(logPath, "raw-iunknown", RemoteDesktopControl.CreateRaw);
            InspectRoute(logPath, "type-library", RemoteDesktopControl.CreateThroughTypeLibrary);
            return SuccessExitCode;
        }
        catch (Exception error) when (error is InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            RdpExperimentLog.Step(logPath, "create-control", false, $"{error.GetType().Name}: {error.Message}");
            return ControlUnavailableExitCode;
        }
    }

    /// <summary>
    /// Probes one creation route. The two routes answer QueryInterface differently, so measuring only
    /// one of them would say nothing about whether the control can be asked for credentials.
    /// </summary>
    private static void InspectRoute(string logPath, string routeName, Func<RemoteDesktopControl> create)
    {
        using RemoteDesktopControl control = create();
        RdpExperimentLog.Step(logPath, "create-control", true, routeName + ": " + control.DescribeHost());

        bool identity = control.AnswersIdentityQuery();
        RdpExperimentLog.Step(
            logPath,
            "identity-query",
            identity,
            routeName + (identity
                ? ": the reference answers QueryInterface for IUnknown"
                : ": the reference does not answer IUnknown, so every later probe is inconclusive"));

        bool dispatch = control.AnswersDispatchQuery();
        RdpExperimentLog.Step(
            logPath,
            "dispatch-query",
            dispatch,
            routeName + (dispatch
                ? ": the control is scriptable (IDispatch present)"
                : ": the control is not scriptable through this route"));

        foreach ((string interfaceName, Guid interfaceId) in RemoteDesktopControl.Interfaces)
        {
            bool available = control.ProbeInterface(interfaceId);
            RdpExperimentLog.Step(
                logPath,
                "probe-interface",
                available,
                routeName + " · " + interfaceName + (available ? string.Empty : ": E_NOINTERFACE"));
        }
    }
}
