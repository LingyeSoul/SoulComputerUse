namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// The before-connect segment as a port: everything that has to happen before the first connection can
/// be made. The caller composes it from the existing startup ports; this layer only knows that it either
/// finishes or throws.
/// </summary>
/// <remarks>
/// The segment covers the contract order of sections 3, 3.1.1 and 7.2: take the lock, read or take over
/// the ownership record, write <c>starting</c>, probe the credential tier, sweep the leftover child
/// sessions, settle the child-session switch and write the alternate-shell properties.
/// </remarks>
public interface ISessionStartBeforeConnect
{
    /// <summary>Runs the whole before-connect segment.</summary>
    /// <returns>The completed segment, including the lock handle the session now holds.</returns>
    /// <exception cref="System.Exception">A step refused or failed, so nothing may connect.</exception>
    BeforeConnectResult Execute();
}

/// <summary>
/// Opening the connection, as a port. The implementation calls the single connection entry point, which
/// carries the leftover sweep; this layer never connects on its own.
/// </summary>
public interface ISessionConnectionOpener
{
    /// <summary>Opens the connection and returns.</summary>
    /// <exception cref="System.Exception">The connection could not be opened.</exception>
    void Open();
}

/// <summary>
/// The after-logon segment as a port: everything that happens once the operator has logged on and the
/// agent the child session started is reachable.
/// </summary>
/// <remarks>
/// The segment covers the agent attachment, recording the agent's Windows session id, starting the owner
/// pipe and moving the record through <c>connecting</c> to <c>ready</c>. It must not report success unless
/// every one of those is in place.
/// </remarks>
public interface ISessionStartAfterLogon
{
    /// <summary>Runs the whole after-logon segment.</summary>
    /// <returns>The completed segment.</returns>
    /// <exception cref="System.Exception">A step refused or failed, so the session is not ready.</exception>
    AfterLogonResult Execute();
}

/// <summary>
/// Observes each phase boundary as the runner passes it, so the host's caption and a diagnostic report
/// share one source of phase truth.
/// </summary>
public interface ISessionPhaseObserver
{
    /// <summary>Called once per phase, in the contract order.</summary>
    /// <param name="phase">The phase the runner has entered.</param>
    void OnPhaseChanged(SessionStartPhase phase);
}
