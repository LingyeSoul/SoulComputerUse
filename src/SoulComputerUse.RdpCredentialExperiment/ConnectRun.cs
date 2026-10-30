using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.RdpCredentialExperiment;

/// <summary>
/// The connect mode of the probe: it hosts the control the way the predecessor product did, connects
/// to a child session with default settings and logs what the connection reports. It never touches a
/// credential property and it never types anything.
/// </summary>
/// <remarks>
/// The log only records step names, state numbers and Win32 error codes. Credentials are not read,
/// not persisted and not named anywhere in this file.
/// </remarks>
internal static class ConnectRun
{
    private const int ObservationMilliseconds = 90000;
    private const int PollMilliseconds = 500;

    /// <summary>Runs the connect experiment against one log file and returns the exit code.</summary>
    internal static int Run(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);

        int consoleBefore = WTSGetActiveConsoleSessionId();
        RdpExperimentLog.Step(
            logPath,
            "baseline",
            true,
            string.Create(CultureInfo.InvariantCulture, $"console session id = {consoleBefore}"));
        // The gate's weak point: a child session that is still logged on answers a new connection
        // without asking the operator to log on, so it is logged off before anything connects.
        ConnectionGate gate = SweepLeftoverChildSessions(logPath);

        using ProbeWindow window = new(1100, 700);
        window.Show();
        Application.DoEvents();

        object? ocx = window.Ocx;
        if (ocx is null)
        {
            RdpExperimentLog.Step(logPath, "attach-control", false, "the ActiveX host did not attach the control");
            return 3;
        }

        RdpExperimentLog.Step(logPath, "attach-control", true, "the ActiveX host attached the control");

        try
        {
            ChildSessionConnection.Connect(ocx, "localhost", 1280, 800, gate);
        }
        catch (COMException error)
        {
            RdpExperimentLog.Step(
                logPath,
                "connect",
                false,
                string.Create(CultureInfo.InvariantCulture, $"HRESULT 0x{error.HResult:X8}: {error.Message}"));
            return 4;
        }
        catch (RdpSurfaceException error)
        {
            RdpExperimentLog.Step(logPath, "connect", false, error.Message);
            return 4;
        }

        bool reachedReady;
        try
        {
            reachedReady = ObserveConnection(logPath, window);
        }
        catch (InvalidOperationException error)
        {
            // The chain reached ready without the logon step. That is exactly the failure the gate
            // exists to catch, so the run reports it instead of printing a green line.
            RdpExperimentLog.Step(logPath, "logon-gate", false, error.Message);
            Console.Error.WriteLine(error.Message);
            return 5;
        }

        int consoleAfter = WTSGetActiveConsoleSessionId();
        RdpExperimentLog.Step(
            logPath,
            "console-session-unchanged",
            consoleAfter == consoleBefore,
            string.Create(CultureInfo.InvariantCulture, $"console session id after the run = {consoleAfter}"));
        return reachedReady ? 0 : 6;
    }

    /// <summary>
    /// Logs off every child session of this product that is still around, so the connection that follows
    /// is a fresh one and has to ask the operator to log on. A session that is neither provably this
    /// product's nor provably somebody else's stops the run before anything is logged off: connecting
    /// anyway would hand over a session that skipped the operator's logon, and logging it off could
    /// destroy a person's disconnected work.
    /// </summary>
    private static ConnectionGate SweepLeftoverChildSessions(string logPath)
    {
        // The probe holds no ownership record, so it carries no recorded child session id; only the
        // measured leftover shape and this account can prove anything here.
        LeftoverEvidence evidence = new(0, SessionTable.CurrentAccountName());
        int consoleId = SessionTable.ActiveConsoleSessionId();
        List<SessionCandidate> candidates = [];
        foreach (SessionEntry entry in SessionTable.Read())
        {
            candidates.Add(new SessionCandidate(entry.Id, entry.StationName, entry.State, entry.AccountName));
        }

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(candidates, consoleId, evidence);
        IReadOnlyList<SessionCandidate> unproven = LeftoverChildSessionPolicy.FindUnproven(candidates, consoleId, evidence);
        if (unproven.Count > 0)
        {
            throw LeftoverChildSessionPolicy.ManualInterventionRequired(string.Create(
                CultureInfo.InvariantCulture,
                $"{unproven.Count} session(s) are logged on that are neither provably this product's leftovers nor provably another person's sessions, so nothing may be logged off and no connection may be opened."));
        }

        if (LeftoverChildSessionPolicy.Decide(leftovers) == LeftoverVerdict.NoLeftover)
        {
            RdpExperimentLog.Step(logPath, "leftover-child-session", true, "none, so the connection must ask the operator to log on");
            return new ConnectionGate(LeftoverVerdict.NoLeftover, leftovers, unproven);
        }

        foreach (SessionCandidate leftover in leftovers)
        {
            // The console session id is re-read before every logoff request, the way the product sweep
            // does: a fast user switch between two requests can move the operator's desktop onto a
            // session this loop judged a moment earlier, and that session must never be logged off.
            if (leftover.Id == SessionTable.ActiveConsoleSessionId())
            {
                throw LeftoverChildSessionPolicy.ManualInterventionRequired(string.Create(
                    CultureInfo.InvariantCulture,
                    $"session id {leftover.Id} became the active console session before its logoff request, so nothing more may be logged off."));
            }

            RdpExperimentLog.Step(
                logPath,
                "leftover-child-session",
                true,
                string.Create(CultureInfo.InvariantCulture, $"id={leftover.Id} state={leftover.State}; logging it off before connecting"));
            try
            {
                SessionTable.LogOff(leftover.Id);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                throw LeftoverChildSessionPolicy.ManualInterventionRequired(error.Message);
            }
        }

        // Logging off is a request, not a result: the session has to actually disappear before a
        // connection can be trusted to present a fresh logon screen.
        VerifyNoLeftoverRemains(logPath, consoleId, evidence);

        return new ConnectionGate(LeftoverVerdict.NoLeftover, leftovers, unproven);
    }

    /// <summary>
    /// Re-reads the session table after the logoff requests and refuses to connect while any child
    /// session of this product is still present. An unconfirmed sweep is a bypassed gate.
    /// </summary>
    private static void VerifyNoLeftoverRemains(string logPath, int consoleId, LeftoverEvidence evidence)
    {
        List<SessionCandidate> remaining = [];
        foreach (SessionEntry entry in SessionTable.Read())
        {
            remaining.Add(new SessionCandidate(entry.Id, entry.StationName, entry.State, entry.AccountName));
        }

        IReadOnlyList<SessionCandidate> stillThere = LeftoverChildSessionPolicy.FindLeftovers(remaining, consoleId, evidence);
        if (LeftoverChildSessionPolicy.Decide(stillThere) == LeftoverVerdict.NoLeftover)
        {
            RdpExperimentLog.Step(logPath, "leftover-child-session-cleared", true, "the session table holds no child session any more");
            return;
        }

        throw LeftoverChildSessionPolicy.ManualInterventionRequired(string.Create(
            CultureInfo.InvariantCulture,
            $"{stillThere.Count} child session(s) survived the logoff request, so the connection would answer without asking the operator to log on."));
    }

    /// <summary>
    /// Polls the control's connected state for the observation window and feeds every state into the
    /// session state chain, so a connection that jumps straight to ready without ever showing the logon
    /// step is refused by <see cref="ChildSessionConnection.Observe"/> instead of being logged as a pass.
    /// State 1 means a live session, 2 and 3 mean still connecting or waiting for the operator, 0 means
    /// not connected.
    /// </summary>
    private static bool ObserveConnection(string logPath, ProbeWindow window)
    {
        List<SessionState> chain = [];
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(ObservationMilliseconds);
        int lastState = -1;
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            int state = ReadConnectedState(window.Ocx);
            if (state != lastState)
            {
                RdpExperimentLog.Step(
                    logPath,
                    "connected-state",
                    state >= 0,
                    string.Create(CultureInfo.InvariantCulture, $"value = {state}; session state = {DescribeChainStep(logPath, chain, state)}"));
                lastState = state;
            }

            if (state == 1)
            {
                return true;
            }

            Thread.Sleep(PollMilliseconds);
        }

        RdpExperimentLog.Step(
            logPath,
            "connected-state",
            false,
            string.Create(CultureInfo.InvariantCulture, $"still not connected after observation; last value = {lastState}"));
        return false;
    }

    /// <summary>
    /// Maps one raw client state into the session state chain. An unknown or unreadable value is a
    /// failure, never a skipped sample: dropping a sample could hide the logon step.
    /// </summary>
    private static string DescribeChainStep(string logPath, List<SessionState> chain, int rawState)
    {
        if (rawState < 0)
        {
            // An unreadable poll carries no state, so it must not enter the chain at all.
            return "unreadable";
        }

        try
        {
            SessionState mapped = ChildSessionConnection.Observe(RdpStateMapping.FromRawValue(rawState), chain);
            return SessionStateNames.ToWireName(mapped);
        }
        catch (InvalidOperationException error)
        {
            // The logon step was skipped; the run has to fail rather than report a live session.
            RdpExperimentLog.Step(logPath, "logon-gate", false, error.Message);
            throw;
        }
    }

    private static int ReadConnectedState(object? ocx)
    {
        if (ocx is null)
        {
            return -1;
        }

        dynamic client = ocx;
        try
        {
            return (int)client.Connected;
        }
        catch (COMException)
        {
            return -1;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();
}
