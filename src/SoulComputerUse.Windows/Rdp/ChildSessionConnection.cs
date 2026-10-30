using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>A session that may or may not be one of this product's child sessions.</summary>
/// <param name="AccountName">
/// The account name the session is logged on as, or null when the system did not answer for this
/// session. An empty string is a readable answer that names no account. The policy compares this name
/// and never reports it.
/// </param>
public sealed record SessionCandidate(int Id, string StationName, int State, string? AccountName = null);

/// <summary>
/// The proof a leftover sweep judges with: the child session id this product's own ownership record
/// named when the current start took the record over, and the account name the sweeping process runs
/// as.
/// </summary>
/// <remarks>
/// A child session always runs as the account that started it, so a session logged on as any other
/// account is not this product's leftover, whatever its station name looks like. That invariant is
/// what the operator account name is here for.
/// </remarks>
public sealed record LeftoverEvidence
{
    /// <summary>Creates the evidence of one sweep.</summary>
    /// <param name="RecordedChildSessionId">
    /// The <c>subagentWtsSessionId</c> the taken-over ownership record named, or zero when no record
    /// named a session.
    /// </param>
    /// <param name="OperatorAccountName">
    /// The account name of the process that runs the sweep, or null when it could not be determined.
    /// With no account name no session can be proven to run as this account, so nothing may be logged
    /// off on shape alone.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The recorded child session id is negative.</exception>
    public LeftoverEvidence(int RecordedChildSessionId, string? OperatorAccountName)
    {
        if (RecordedChildSessionId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RecordedChildSessionId),
                RecordedChildSessionId,
                "The recorded child session id must not be negative.");
        }

        this.RecordedChildSessionId = RecordedChildSessionId;
        this.OperatorAccountName = OperatorAccountName;
    }

    /// <summary>The child session id the taken-over ownership record named; zero names none.</summary>
    public int RecordedChildSessionId { get; }

    /// <summary>The account the sweep runs as; null when the name could not be determined.</summary>
    public string? OperatorAccountName { get; }
}

/// <summary>What the policy proved about one session.</summary>
public enum LeftoverProof
{
    /// <summary>Proven not to be a leftover of this product, so the run may go past it.</summary>
    NotOurs = 0,

    /// <summary>Proven to be a leftover of this product, so the sweep logs it off before connecting.</summary>
    Ours = 1,

    /// <summary>
    /// Neither side is proven. The sweep may not log the session off and the run may not go past it,
    /// because either act would guess, so the run stops for a human to decide.
    /// </summary>
    Unproven = 2,
}

/// <summary>What the pre-connection decision found.</summary>
public enum LeftoverVerdict
{
    /// <summary>Nothing of ours is left over, so a connection presents a fresh logon screen.</summary>
    NoLeftover = 0,

    /// <summary>A leftover child session was found and must be logged off before connecting.</summary>
    LeftoverFound = 1,

    /// <summary>
    /// A session is logged on that is neither provably this product's leftover nor provably somebody
    /// else's, so nothing may be logged off and nothing may connect; a human has to decide.
    /// </summary>
    UnprovenPresent = 2,
}

/// <summary>What the pre-connection decision found.</summary>
public sealed record ConnectionGate(
    LeftoverVerdict LeftoverVerdict,
    IReadOnlyList<SessionCandidate> Leftovers,
    IReadOnlyList<SessionCandidate> Unproven);

/// <summary>
/// Decides, before anything connects, whether a child session of this product is already logged on.
/// </summary>
/// <remarks>
/// This is the gate's weak point, and the reason the sweep exists: a child session that is still logged
/// on answers a new connection without asking for a password, so the operator would silently receive a
/// session nobody authenticated for. A leftover therefore has to be logged off first, and if it cannot
/// be, the run must stop instead of connecting into an unguarded session.
/// <para>
/// A leftover is defined as a session whose client is gone, so its state is
/// <see cref="DisconnectedState"/>. A session that is active has somebody in front of it, and this
/// product's own client cannot be that somebody while the single-instance lock is held, so an active
/// session is never this product's leftover, whatever its station name says.
/// </para>
/// </remarks>
public static class LeftoverChildSessionPolicy
{
    /// <summary>The service session, which must never be treated as one of ours.</summary>
    public const int ServiceSessionId = 0;

    /// <summary>
    /// The prefix of an inbound remote-desktop station name: <c>RDP-Tcp#&lt;n&gt;</c>, where the whole
    /// rest of the name is one number. Any other name that merely ends in <c>#0</c> is not an inbound
    /// remote-desktop station at all.
    /// </summary>
    private const string InboundRdpStationPrefix = "RDP-Tcp#";

    /// <summary>
    /// <c>WTS_CONNECTSTATE_CLASS.WTSDisconnected</c>: the session still holds a logon but has no client
    /// attached, which is the shape a child session has after its window closed.
    /// </summary>
    public const int DisconnectedState = 4;

    /// <summary>
    /// Finds the sessions that are proven to be child sessions of this product: not the service
    /// session, not the operator's console session, and proven by evidence. Everything this method
    /// returns will be logged off, so a session it must not decide about is absent here and present in
    /// <see cref="FindUnproven"/> instead.
    /// </summary>
    public static IReadOnlyList<SessionCandidate> FindLeftovers(
        IReadOnlyList<SessionCandidate> sessions,
        int activeConsoleSessionId,
        LeftoverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(evidence);

        List<SessionCandidate> leftovers = [];
        foreach (SessionCandidate session in sessions)
        {
            if (Classify(session, activeConsoleSessionId, evidence) == LeftoverProof.Ours)
            {
                leftovers.Add(session);
            }
        }

        return leftovers;
    }

    /// <summary>
    /// Finds the sessions the evidence cannot decide about. The sweep logs nothing off while even one
    /// is present, and the run refuses instead of guessing in either direction.
    /// </summary>
    public static IReadOnlyList<SessionCandidate> FindUnproven(
        IReadOnlyList<SessionCandidate> sessions,
        int activeConsoleSessionId,
        LeftoverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(evidence);

        List<SessionCandidate> unproven = [];
        foreach (SessionCandidate session in sessions)
        {
            if (Classify(session, activeConsoleSessionId, evidence) == LeftoverProof.Unproven)
            {
                unproven.Add(session);
            }
        }

        return unproven;
    }

    /// <summary>Returns the verdict for the proven leftovers of a sweep.</summary>
    public static LeftoverVerdict Decide(IReadOnlyList<SessionCandidate> leftovers)
    {
        ArgumentNullException.ThrowIfNull(leftovers);
        return leftovers.Count == 0 ? LeftoverVerdict.NoLeftover : LeftoverVerdict.LeftoverFound;
    }

    /// <summary>
    /// Proves what one session is. Only positive proof makes a session ours, because a leftover that
    /// is missed costs a fresh logon while a stranger's session that is logged off costs somebody's
    /// unsaved work, and the second cannot be undone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The proofs, from strongest to weakest:
    /// </para>
    /// <list type="number">
    /// <item>
    /// The taken-over ownership record names this exact session id, the session is disconnected, and its
    /// account is the account this process runs as. The record is this product's own write and the
    /// same-account invariant corroborates it, so together they survive even a reboot that reuses the
    /// id for another session.
    /// </item>
    /// <item>
    /// The session is disconnected, its station name is empty and its account is this process's own
    /// account. Measured on the development machine on 2026-10-26, a child session whose client is gone
    /// reports an empty station name and a readable account name while another local user's fast-user
    /// -switch session does not run as this account.
    /// </item>
    /// </list>
    /// <para>
    /// Everything else is not ours, with one exception that is <see cref="LeftoverProof.Unproven"/>:
    /// an inbound <c>RDP-Tcp#&lt;n&gt;</c> station that is disconnected while its account name is
    /// readable. That shape belongs to a person who closed the remote-desktop window while staying
    /// logged on, and the account name does not separate that person from this product's own
    /// unmeasured leftover shape, so neither a logoff nor a connection may guess.
    /// </para>
    /// <para>
    /// An account name the system did not answer for is treated as absent proof, never as a match: a
    /// session of this account answers its own queries, so an unreadable name means the session is not
    /// this product's, while a record that names the very session id keeps the case unproven rather
    /// than dismissed. An active session is never proven to be ours at all, because a leftover's
    /// client is gone by definition.
    /// </para>
    /// <para>
    /// The account names are compared and never reported; the refusals that carry these decisions name
    /// counts and session ids only.
    /// </para>
    /// </remarks>
    public static LeftoverProof Classify(SessionCandidate session, int activeConsoleSessionId, LeftoverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(evidence);

        if (session.Id == ServiceSessionId || session.Id == activeConsoleSessionId)
        {
            return LeftoverProof.NotOurs;
        }

        // The precise evidence first: the record this product wrote names this exact session, and the
        // same-account invariant says a session of ours always runs as this process's account. An active
        // session is never a leftover, so even the record does not make it one.
        if (evidence.RecordedChildSessionId > 0
            && session.Id == evidence.RecordedChildSessionId
            && session.State == DisconnectedState)
        {
            if (RunsAsOperatorAccount(session, evidence))
            {
                return LeftoverProof.Ours;
            }

            return session.AccountName is null ? LeftoverProof.Unproven : LeftoverProof.NotOurs;
        }

        if (session.State != DisconnectedState)
        {
            return LeftoverProof.NotOurs;
        }

        // The measured leftover shape: the client is gone, so the station name is gone too, and the
        // account of a child session is the account that started it.
        if (session.StationName.Length == 0)
        {
            return RunsAsOperatorAccount(session, evidence) ? LeftoverProof.Ours : LeftoverProof.NotOurs;
        }

        // A disconnected inbound remote-desktop station with a readable account name is the one shape
        // the evidence cannot decide, so it is left for a human: neither the person's unsaved work nor
        // the gate may be gambled on a guess.
        if (IsInboundRdpStationName(session.StationName) && !string.IsNullOrEmpty(session.AccountName))
        {
            return LeftoverProof.Unproven;
        }

        return LeftoverProof.NotOurs;
    }

    /// <summary>
    /// Tells whether a session is proven to be a child session of this product and may therefore be
    /// logged off by the sweep; see <see cref="Classify"/> for the proofs and their order.
    /// </summary>
    public static bool IsOneOfOurs(SessionCandidate session, int activeConsoleSessionId, LeftoverEvidence evidence)
    {
        return Classify(session, activeConsoleSessionId, evidence) == LeftoverProof.Ours;
    }

    /// <summary>
    /// Proves what one session is as the target of a teardown logoff: the entry the session table
    /// returned for the id the teardown is about to use, judged at the last moment before the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This proof differs from <see cref="Classify"/> in one deliberate way: the entry's state is not
    /// part of it. The sweep's rule that an active session is never this product's leftover holds because
    /// the sweeping process holds the single-instance lock, so no second client of this product can
    /// exist. A teardown runs while the very same lock is held and the tearing-down process may itself
    /// still be the target's client, so the target may legitimately be active at the moment of the
    /// request. The proof here is therefore the recorded id plus the same-account invariant alone: the
    /// evidence names this exact session, and the session is logged on as the account this process runs
    /// as, which a stranger's session that reused the freed id is not.
    /// </para>
    /// <para>
    /// The same fail-closed standard as the sweep applies: the service session, the active console
    /// session and any session the evidence does not name are never ours; an account name the system did
    /// not answer for is absent proof, so the case stays <see cref="LeftoverProof.Unproven"/> rather than
    /// dismissed; an account that was read but is not this process's own account — including an empty
    /// name, which belongs to a session nobody logged on to — is not ours. A teardown target that cannot
    /// be proven must not be logged off, because the request cannot be undone.
    /// </para>
    /// </remarks>
    public static LeftoverProof ClassifyOwnedTarget(
        SessionCandidate session,
        int activeConsoleSessionId,
        LeftoverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(evidence);

        if (session.Id == ServiceSessionId || session.Id == activeConsoleSessionId || session.Id <= 0)
        {
            return LeftoverProof.NotOurs;
        }

        // Only the session the evidence names may be logged off at teardown: every other session, however
        // much it looks like ours, needs a sweep with its own proof.
        if (session.Id != evidence.RecordedChildSessionId)
        {
            return LeftoverProof.NotOurs;
        }

        if (session.AccountName is null)
        {
            return LeftoverProof.Unproven;
        }

        return RunsAsOperatorAccount(session, evidence) ? LeftoverProof.Ours : LeftoverProof.NotOurs;
    }

    /// <summary>
    /// Tells whether one station name is a whole inbound remote-desktop name, <c>RDP-Tcp#&lt;n&gt;</c>:
    /// the prefix followed by nothing but digits. A name that merely ends in <c>#0</c> is not matched,
    /// because a suffix cannot tell the listener's numbering from a coincidence.
    /// </summary>
    private static bool IsInboundRdpStationName(string stationName)
    {
        if (!stationName.StartsWith(InboundRdpStationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> number = stationName.AsSpan(InboundRdpStationPrefix.Length);
        if (number.IsEmpty)
        {
            return false;
        }

        foreach (char digit in number)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tells whether the session's account was read and is this process's own account. A name that was
    /// not answered for, an empty name and a missing operator name are all absent proof, never a match.
    /// </summary>
    private static bool RunsAsOperatorAccount(SessionCandidate session, LeftoverEvidence evidence)
    {
        return evidence.OperatorAccountName is { Length: > 0 }
            && session.AccountName is { Length: > 0 }
            && string.Equals(session.AccountName, evidence.OperatorAccountName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The refusal raised when a leftover cannot be removed. Connecting anyway would hand over a session
    /// that skips the operator's logon, so this is a manual-intervention case, never a warning.
    /// </summary>
    public static InvalidOperationException ManualInterventionRequired(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new InvalidOperationException(
            $"manual_intervention_required: {reason} A leftover child session answers a new connection without asking the operator to log on, so the gate would be bypassed.");
    }
}

/// <summary>
/// The one entry point that connects the product to a child session.
/// </summary>
/// <remarks>
/// It exists so that no other code can open a connection: the sweep for a leftover child session, the
/// machine-state check and the state mapping all live on this path, and a second path could skip any of
/// them. The gate is only real if every connection goes through here.
/// </remarks>
public static class ChildSessionConnection
{
    /// <summary>Decides whether a connection may be started at all, before anything is created.</summary>
    public static ConnectionGate DecideGate(
        IReadOnlyList<SessionCandidate> sessions,
        int activeConsoleSessionId,
        bool childSessionsEnabled,
        object? denyTsConnections,
        object? denyChildConnections,
        LeftoverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(evidence);

        // The machine state first: without it a connection could take over the operator's console.
        RdpExperimentPreconditions.Check(childSessionsEnabled, denyTsConnections, denyChildConnections);

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(sessions, activeConsoleSessionId, evidence);
        IReadOnlyList<SessionCandidate> unproven = LeftoverChildSessionPolicy.FindUnproven(sessions, activeConsoleSessionId, evidence);
        LeftoverVerdict verdict = unproven.Count > 0
            ? LeftoverVerdict.UnprovenPresent
            : LeftoverChildSessionPolicy.Decide(leftovers);
        return new ConnectionGate(verdict, leftovers, unproven);
    }

    /// <summary>
    /// Refuses to go on when a leftover child session is still around, and when a session is around that
    /// is neither provably a leftover nor provably somebody else's. A leftover answers the connection
    /// with its own logon and an undecidable session may be a person's disconnected desktop, so one of
    /// them must never be connected past and the other must never be signed off by a guess.
    /// </summary>
    public static void EnsureNoLeftover(ConnectionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        if (gate.LeftoverVerdict == LeftoverVerdict.NoLeftover)
        {
            return;
        }

        if (gate.LeftoverVerdict == LeftoverVerdict.LeftoverFound)
        {
            throw LeftoverChildSessionPolicy.ManualInterventionRequired(
                $"{gate.Leftovers.Count} child session(s) of this product are still logged on and could not be removed before connecting.");
        }

        throw LeftoverChildSessionPolicy.ManualInterventionRequired(
            $"{gate.Unproven.Count} session(s) are logged on that are neither provably this product's leftovers nor provably another person's sessions, so nobody may be signed out and no connection may be opened until a human decides.");
    }

    /// <summary>
    /// Turns one observed client state into the session state the caption shows, and checks the chain so
    /// far. That check is the guarantee the operator was actually asked to log on.
    /// </summary>
    public static SessionState Observe(RdpConnectedState connectedState, IList<SessionState> chainSoFar)
    {
        ArgumentNullException.ThrowIfNull(chainSoFar);

        SessionState state = RdpStateMapping.ToSessionState(connectedState);
        chainSoFar.Add(state);
        RdpStateMapping.EnsureLogonWasObserved(chainSoFar.AsReadOnly());
        return state;
    }

    /// <summary>
    /// Configures the hosted control and starts the connection. This is the only place in the product
    /// that asks the control to connect; every other path has to call it, so the sweep above cannot be
    /// skipped by a second caller.
    /// </summary>
    /// <param name="ocx">The raw control object the ActiveX host attached.</param>
    /// <param name="server">The server to connect to.</param>
    /// <param name="desktopWidth">The remote desktop width to request.</param>
    /// <param name="desktopHeight">The remote desktop height to request.</param>
    /// <param name="gate">What the pre-connection sweep decided; a leftover refuses the connection.</param>
    public static void Connect(
        object ocx,
        string server,
        int desktopWidth,
        int desktopHeight,
        ConnectionGate gate)
    {
        ArgumentNullException.ThrowIfNull(ocx);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        if (desktopWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desktopWidth), desktopWidth, "The desktop width must be positive.");
        }

        if (desktopHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desktopHeight), desktopHeight, "The desktop height must be positive.");
        }

        ArgumentNullException.ThrowIfNull(gate);

        // The gate comes first: connecting into a session that is already logged on would skip the
        // operator's logon, and nothing above this line may have connected on its own.
        EnsureNoLeftover(gate);

        dynamic client = ocx;
        client.Server = server;
        client.DesktopWidth = desktopWidth;
        client.DesktopHeight = desktopHeight;
        client.AdvancedSettings2.EnableCredSspSupport = true;

        // Without smart sizing the control draws the remote desktop at its native size and clips whatever
        // does not fit, so a window that is resized any way but exactly the session size would show a
        // cropped corner of the desktop instead of the whole picture. Smart sizing scales the picture to
        // the control's own size, keeping the aspect ratio, which is what the picture rectangle expects.
        client.AdvancedSettings2.SmartSizing = true;

        // A child-session connection is the only kind this product makes; a flag that did not stick
        // would take over the operator's own session instead.
        RdpExtendedSettings.SetBooleanAndVerify(ocx, RdpExtendedSettings.ConnectToChildSessionProperty, true);

        client.Connect();
    }
}
