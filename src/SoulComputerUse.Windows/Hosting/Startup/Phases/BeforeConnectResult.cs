using System;

namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// What the before-connect phase proved before anything connected: the leftover sweep finished, whether
/// this session enabled the child-session switch, the alternate-shell properties are written, and the
/// lock handle the running session will hold.
/// </summary>
/// <remarks>
/// <para>
/// The value exists only for a segment that finished. A segment that refused or failed throws instead,
/// which is why the constructor refuses a result whose sweep did not finish, whose alternate shell is not
/// configured, or which has no lock handle: such a result would let a caller connect into a session that
/// either skips the operator's logon or starts the usual shell instead of the agent.
/// </para>
/// <para>
/// <see cref="LockHandle"/> is handed over rather than owned here. The before-connect segment takes the
/// machine-wide lock, and a successful start owns it for the whole session, so the handle travels to the
/// caller instead of being closed when the segment returns.
/// </para>
/// </remarks>
public sealed record BeforeConnectResult
{
    /// <summary>Creates one completed before-connect result and refuses an incomplete one.</summary>
    /// <exception cref="ArgumentNullException">The lock handle is null.</exception>
    /// <exception cref="ArgumentException">
    /// The sweep did not complete or the alternate shell is not configured.
    /// </exception>
    public BeforeConnectResult(
        bool sweepCompleted,
        bool childSessionsOpenedByUs,
        bool alternateShellConfigured,
        IDisposable? lockHandle)
    {
        if (!sweepCompleted)
        {
            throw new ArgumentException(
                "A before-connect result must prove the leftover sweep finished; without it a connection answers with a leftover child session and skips the operator's logon.",
                nameof(sweepCompleted));
        }

        if (!alternateShellConfigured)
        {
            throw new ArgumentException(
                "A before-connect result must prove the alternate-shell properties are configured; without them the child session runs the usual shell and no agent can be attached.",
                nameof(alternateShellConfigured));
        }

        ArgumentNullException.ThrowIfNull(lockHandle);

        SweepCompleted = true;
        ChildSessionsOpenedByUs = childSessionsOpenedByUs;
        AlternateShellConfigured = true;
        LockHandle = lockHandle;
    }

    /// <summary>Whether the leftover sweep proved that no child session of ours is still logged on.</summary>
    public bool SweepCompleted { get; }

    /// <summary>
    /// Whether this session enabled the machine-wide child-session switch, which decides whether shutdown
    /// turns it off again. False when it was already on, because an already-on switch is not ours.
    /// </summary>
    public bool ChildSessionsOpenedByUs { get; }

    /// <summary>Whether the alternate-shell properties point at the agent.</summary>
    public bool AlternateShellConfigured { get; }

    /// <summary>The lock handle the running session holds; the caller owns it from here on.</summary>
    public IDisposable LockHandle { get; }
}
