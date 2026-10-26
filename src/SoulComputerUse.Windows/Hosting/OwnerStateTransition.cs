using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;

namespace SoulComputerUse.Windows.Hosting;

/// <summary>
/// Connects the teardown layer's state port to the owner, so <c>closing</c> is written to the
/// ownership record before any destructive step runs.
/// </summary>
/// <remarks>
/// The owner refuses an illegal transition with <c>E_STATE</c>, and the teardown executor records that
/// refusal as a failed step instead of continuing as if the state had changed.
/// </remarks>
public sealed class OwnerStateTransition : ISessionStateTransition
{
    private readonly SessionOwnerService _owner;

    /// <summary>Creates the port over one running owner.</summary>
    /// <exception cref="ArgumentNullException">The owner is null.</exception>
    public OwnerStateTransition(SessionOwnerService owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <inheritdoc />
    public void EnterClosing()
    {
        _owner.Transition(SessionState.Closing);
    }
}