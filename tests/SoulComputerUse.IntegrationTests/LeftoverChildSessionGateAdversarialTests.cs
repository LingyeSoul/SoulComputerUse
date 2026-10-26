using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.CSharp.RuntimeBinder;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Adversarial verification of the sweep that has to run before anything connects. A child session that is
/// still logged on answers a new connection without asking the operator to log on, so the credential gate
/// would disappear without anybody noticing; this sweep is the only thing that stops that.
/// </summary>
/// <remarks>
/// The real session table is read by running the shipped listing probe and parsing the log it writes,
/// instead of re-declaring the Win32 record inside the test. That is deliberate: a record that declares
/// one field too many shifts the stride, so every record after the first is read from the wrong offset,
/// which is exactly how this table once turned into garbage ids. Only an end-to-end read of the real code
/// path catches that class of mistake.
/// </remarks>
[TestClass]
public sealed class LeftoverChildSessionGateAdversarialTests
{
    private const string ProbeFileName = "SoulComputerUse.RdpCredentialExperiment.exe";
    private const string SessionsSwitch = "--sessions";
    private const string ConsoleStepName = "console-session-id";
    private const string SessionStepName = "session";
    private const string SucceededMarker = ": ok";
    private const string ValueMarker = "value = ";
    private const string ChildSessionStationName = "RDP-Tcp#0";
    private const int ProbeTimeoutMilliseconds = 60000;

    /// <summary>The largest session id a real machine hands out, used to catch a misread record.</summary>
    private const int HighestPlausibleSessionId = 64;

    /// <summary>The largest state number WTS defines, used to catch a misread record.</summary>
    private const int HighestPlausibleSessionState = 7;

    /// <summary>WTSDisconnected: a session that exists but has nobody logged on in front of it.</summary>
    private const int ServiceSessionState = 4;

    /// <summary>WTSActive: a session with a logged-on user in front of it.</summary>
    private const int ActiveSessionState = 0;

    /// <summary>
    /// The real table must read as real session records, and the listing probe must agree with this process
    /// about which session is the console.
    /// </summary>
    [TestMethod]
    public void TheRealSessionTableReadsAsRealRecordsAndNamesTheSameConsoleSession()
    {
        RealTable table = ReadTheRealSessionTable();

        Assert.AreEqual(
            (int)NativeMethods.WTSGetActiveConsoleSessionId(),
            table.ConsoleSessionId,
            "The listing probe and this process must name the same console session, or one of the two reads is wrong.");

        Assert.IsGreaterThanOrEqualTo(
            2,
            table.Sessions.Count,
            $"A real machine has at least the service session and the console session, but only {table.Sessions.Count.ToString(CultureInfo.InvariantCulture)} were read.");

        foreach (SessionCandidate session in table.Sessions)
        {
            Assert.IsTrue(
                session.Id >= 0 && session.Id <= HighestPlausibleSessionId,
                $"A session id of {session.Id.ToString(CultureInfo.InvariantCulture)} is not a real session id. The record layout is off by a field, so every record after the first is read from the wrong offset.");
            Assert.IsTrue(
                session.State >= 0 && session.State <= HighestPlausibleSessionState,
                $"Session {session.Id.ToString(CultureInfo.InvariantCulture)} reported state {session.State.ToString(CultureInfo.InvariantCulture)}, which WTS does not produce, so the record layout is off.");
            Assert.IsGreaterThan(
                0,
                session.StationName.Length,
                $"Session {session.Id.ToString(CultureInfo.InvariantCulture)} was read with an empty station name, so the sweep cannot tell it apart from the operator's own desktop.");
        }

        for (int index = 0; index < table.Sessions.Count; index++)
        {
            for (int otherIndex = index + 1; otherIndex < table.Sessions.Count; otherIndex++)
            {
                Assert.AreNotEqual(
                    table.Sessions[index].Id,
                    table.Sessions[otherIndex].Id,
                    "The same session id was read twice, so the table is being walked with the wrong stride.");
            }
        }

        SessionCandidate services = FindSession(table.Sessions, LeftoverChildSessionPolicy.ServiceSessionId)
            ?? throw new AssertFailedException("The service session (id 0) must appear in the table.");
        Assert.AreEqual(
            ServiceSessionState,
            services.State,
            "The service session carries no interactive logon, so it is expected to be disconnected.");
        Assert.AreEqual(
            "Services",
            services.StationName,
            "The service session's station name is what keeps the sweep from treating it as ours.");

        SessionCandidate console = FindSession(table.Sessions, table.ConsoleSessionId)
            ?? throw new AssertFailedException("The console session must appear in the table.");
        Assert.AreEqual(
            ActiveSessionState,
            console.State,
            "The operator's console is expected to be the active session, which is the baseline every gate decision is measured against.");
        Assert.AreEqual(
            "Console",
            console.StationName,
            "The console's station name is what keeps the sweep from treating the operator's own desktop as ours.");
    }

    /// <summary>
    /// The machine must hold no logged-on child session of this product's right now. This is the leak alarm
    /// for the gate: a child session left behind by an earlier run answers a new connection without asking
    /// the operator to log on, so the gate would be bypassed and nothing in the product would say so.
    /// </summary>
    [TestMethod]
    public void TheRealMachineHoldsNoLeftoverChildSessionSoTheNextConnectionAsksForLogon()
    {
        RealTable table = ReadTheRealSessionTable();

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(table.Sessions, table.ConsoleSessionId);

        Assert.IsEmpty(
            leftovers,
            "A child session is still logged on (" + Describe(leftovers) + "). A new connection would answer it without asking the operator to log on, so the gate would be bypassed; log it off before connecting.");

        Assert.AreEqual(
            LeftoverVerdict.NoLeftover,
            LeftoverChildSessionPolicy.Decide(leftovers),
            "With nothing left over, the sweep must report that a connection would present a fresh logon.");
    }

    /// <summary>
    /// No session beyond the service session and the console may exist unnoticed. This is the tripwire for
    /// an unproven assumption: the sweep recognizes this product's child session only by a station name
    /// ending in <c>#0</c>, and no child session has ever been measured on this machine, so that suffix has
    /// never been checked against a real one. A third session is exactly the moment to check it.
    /// </summary>
    [TestMethod]
    public void AnySessionBeyondTheServiceAndConsoleIsReportedSoTheChildStationNameCanBeMeasured()
    {
        RealTable table = ReadTheRealSessionTable();

        Assert.HasCount(
            2,
            table.Sessions,
            "The machine is expected to hold only the service session and the console. A third session appeared, and the sweep recognizes this product's child session only by a station name ending in '#0', which has never been measured against a real child session. Record this table before trusting the sweep: "
            + Describe(table.Sessions));
    }

    /// <summary>
    /// The operator's console and the service session must stay out of the sweep even when their station
    /// name has the child-session shape. Getting this wrong logs the operator out of their own desktop.
    /// </summary>
    [TestMethod]
    public void TheConsoleAndServiceSessionsAreNeverSweptEvenWithAChildSessionStationName()
    {
        RealTable table = ReadTheRealSessionTable();

        SessionCandidate service = new(LeftoverChildSessionPolicy.ServiceSessionId, ChildSessionStationName, ActiveSessionState);
        SessionCandidate console = new(table.ConsoleSessionId, ChildSessionStationName, ActiveSessionState);

        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(service, table.ConsoleSessionId),
            "The service session must never be treated as this product's child session, whatever its station name says.");
        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(console, table.ConsoleSessionId),
            "The session the real machine reports as the console must never be treated as this product's child session, so the sweep can never log the operator off their own desktop.");

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers([service, console], table.ConsoleSessionId);
        Assert.IsEmpty(
            leftovers,
            "A table holding only the service session and the real console session must sweep to nothing, but it found " + Describe(leftovers) + ".");
    }

    /// <summary>
    /// A leftover in the real table's shape must stop the run with a manual-intervention refusal that says
    /// what has to happen. The refusal is the contract's requirement, so it is proven on top of the real
    /// table rather than on a wholly synthetic one.
    /// </summary>
    [TestMethod]
    public void ALeftoverInTheRealTableShapeStopsTheRunWithAManualInterventionRefusal()
    {
        RealTable table = ReadTheRealSessionTable();
        int leftoverId = table.ConsoleSessionId + 7;

        List<SessionCandidate> withLeftover = [.. table.Sessions];
        withLeftover.Add(new SessionCandidate(leftoverId, ChildSessionStationName, ActiveSessionState));

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(withLeftover, table.ConsoleSessionId);

        Assert.HasCount(
            1,
            leftovers,
            "Exactly the added child session is left over; the real service session and the real console session must stay out of the sweep.");
        Assert.AreEqual(leftoverId, leftovers[0].Id, "The leftover that was found must be the child session that was added.");

        Assert.AreEqual(
            LeftoverVerdict.LeftoverFound,
            LeftoverChildSessionPolicy.Decide(leftovers),
            "A logged-on child session must stop the run instead of letting a connection skip the operator's logon.");

        InvalidOperationException refusal = LeftoverChildSessionPolicy.ManualInterventionRequired(
            string.Create(CultureInfo.InvariantCulture, $"child session {leftoverId} could not be logged off."));

        Assert.Contains("manual_intervention_required", refusal.Message);
        Assert.Contains("log on", refusal.Message);
    }

    /// <summary>
    /// A refusal must happen before the control is touched at all, which is what makes it a refusal rather
    /// than a connection that is started and then abandoned. The stand-in control records whether the
    /// connection path reached any of its members, so reaching the control shows up as more than zero
    /// touches instead of only as a different exception type.
    /// </summary>
    [TestMethod]
    public void ALeftoverRefusalLeavesTheControlUntouchedSoNoConnectionIsAttempted()
    {
        ConnectionGate refused = new(
            LeftoverVerdict.LeftoverFound,
            [new SessionCandidate(7, ChildSessionStationName, ActiveSessionState)]);
        TouchedControlRecorder control = new();

        InvalidOperationException refusal = Assert.ThrowsExactly<InvalidOperationException>(
            () => ChildSessionConnection.Connect(control, "localhost", 1280, 800, refused));

        Assert.Contains("manual_intervention_required", refusal.Message);
        Assert.AreEqual(
            0,
            control.Touches,
            "The connection path reached the control even though the gate refused. The refusal has to come first, or a connection can start into a session that never asks the operator to log on.");

        // A zero only proves the ordering if this stand-in can record a touch at all. With the gate open the
        // path must step onto the control, and it stops there because the stand-in is not a real control;
        // reaching that stop is the proof that the stand-in sees the connection path.
        TouchedControlRecorder reachable = new();
        ConnectionGate open = new(LeftoverVerdict.NoLeftover, []);

        Assert.ThrowsExactly<RuntimeBinderException>(
            () => ChildSessionConnection.Connect(reachable, "localhost", 1280, 800, open));
        Assert.IsGreaterThanOrEqualTo(
            1,
            reachable.Touches,
            "With the gate open the connection path never reached the control, so the zero touches asserted for the refusal would prove nothing.");
    }

    /// <summary>
    /// A stand-in for the hosted control that counts how many of its members the connection path reached.
    /// The members are public because the connection path binds to the control through <c>dynamic</c>.
    /// </summary>
    public sealed class TouchedControlRecorder
    {
        private int _touches;
        private string _server = string.Empty;

        /// <summary>How many control members the connection path reached.</summary>
        public int Touches => _touches;

        /// <summary>The server the connection path asked for; setting it counts as a touch.</summary>
        public string Server
        {
            get => _server;
            set
            {
                _server = value;
                _touches++;
            }
        }

        /// <summary>The requested desktop width, present so the path can move past the first member.</summary>
        public int DesktopWidth { get; set; }

        /// <summary>The requested desktop height, present so the path can move past the first member.</summary>
        public int DesktopHeight { get; set; }

        /// <summary>Records that a connection was actually asked for.</summary>
        public void Connect() => _touches++;
    }

    private static RealTable ReadTheRealSessionTable()
    {
        string logPath = Path.Combine(
            Path.GetTempPath(),
            "scu-leftover-sweep-" + Guid.NewGuid().ToString("N") + ".log");

        try
        {
            RunTheListingProbe(logPath);
            Assert.IsTrue(File.Exists(logPath), "The listing probe exited without writing a log, so nothing about the machine was proved.");
            return RealTable.Parse(File.ReadAllLines(logPath));
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    private static void RunTheListingProbe(string logPath)
    {
        string probePath = Path.Combine(AppContext.BaseDirectory, ProbeFileName);
        Assert.IsTrue(
            File.Exists(probePath),
            $"The listing probe was not next to the tests ({ProbeFileName}), so the real session table cannot be read.");

        ProcessStartInfo startInfo = new(probePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add(SessionsSwitch);

        using Process probe = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The listing probe process could not be started.");

        if (!probe.WaitForExit(ProbeTimeoutMilliseconds))
        {
            probe.Kill(entireProcessTree: true);
            Assert.Fail("The listing probe did not finish within the timeout, so the machine state stays unknown.");
        }

        Assert.AreEqual(
            0,
            probe.ExitCode,
            "Listing the session table must succeed. A non-zero code here means the machine's child-session policy changed, not that this case found a leftover.");
    }

    private static SessionCandidate? FindSession(IReadOnlyList<SessionCandidate> sessions, int sessionId)
    {
        foreach (SessionCandidate session in sessions)
        {
            if (session.Id == sessionId)
            {
                return session;
            }
        }

        return null;
    }

    private static string Describe(IReadOnlyList<SessionCandidate> sessions)
    {
        List<string> parts = [];
        foreach (SessionCandidate session in sessions)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{session.Id}/{session.StationName}/state {session.State}"));
        }

        return string.Join("; ", parts);
    }

    /// <summary>The machine's real session table as the shipped probe read it.</summary>
    private sealed class RealTable
    {
        private RealTable(int consoleSessionId, IReadOnlyList<SessionCandidate> sessions)
        {
            ConsoleSessionId = consoleSessionId;
            Sessions = sessions;
        }

        internal int ConsoleSessionId { get; }

        internal IReadOnlyList<SessionCandidate> Sessions { get; }

        internal static RealTable Parse(IReadOnlyList<string> lines)
        {
            int consoleSessionId = -1;
            List<SessionCandidate> sessions = [];

            foreach (string line in lines)
            {
                if (line.StartsWith(ConsoleStepName + SucceededMarker, StringComparison.Ordinal))
                {
                    consoleSessionId = ParseValue(line, ConsoleStepName);
                }
                else if (line.StartsWith(SessionStepName + SucceededMarker, StringComparison.Ordinal))
                {
                    sessions.Add(ParseSession(line));
                }
                else if (line.StartsWith(SessionStepName + ": failed", StringComparison.Ordinal))
                {
                    Assert.Fail("The listing probe reported a failed session record, so the table in the log is incomplete: " + line);
                }
            }

            Assert.IsGreaterThanOrEqualTo(
                0,
                consoleSessionId,
                $"The log did not record a readable console session id (read {consoleSessionId.ToString(CultureInfo.InvariantCulture)}).");

            return new RealTable(consoleSessionId, sessions);
        }

        private static int ParseValue(string line, string stepName)
        {
            int marker = line.IndexOf(ValueMarker, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, marker, $"The {stepName} line carried no value: {line}");

            string text = line[(marker + ValueMarker.Length)..].Trim();
            bool parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
            Assert.IsTrue(parsed, $"The {stepName} line carried a value that is not a number: {line}");

            return value;
        }

        private static SessionCandidate ParseSession(string line)
        {
            int? id = null;
            int? state = null;
            string stationName = string.Empty;

            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("id=", StringComparison.Ordinal))
                {
                    id = ParseToken(token, "id=", line);
                }
                else if (token.StartsWith("state=", StringComparison.Ordinal))
                {
                    state = ParseToken(token, "state=", line);
                }
                else if (token.StartsWith("station=", StringComparison.Ordinal))
                {
                    stationName = token["station=".Length..];
                }
            }

            Assert.IsTrue(
                id.HasValue && state.HasValue && stationName.Length > 0,
                $"A session record could not be read as an id, a state and a station: {line}");

            return new SessionCandidate(id.Value, stationName, state.Value);
        }

        private static int ParseToken(string token, string prefix, string line)
        {
            string text = token[prefix.Length..];
            bool parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
            Assert.IsTrue(parsed, $"A session record carried '{prefix}{text}', which is not a number: {line}");

            return value;
        }
    }
}