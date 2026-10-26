using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// Maps a teardown failure onto the contract's error enumeration where the contract names one.
/// </summary>
/// <remarks>
/// Section 2 does not reserve a code for every teardown step: only the missing-child-session case has to
/// be expressible as <c>E_STATE</c>, because the caller's facts contradict its own plan. The other
/// failures keep the exit code plus their enumerated reason and are deliberately not forced into a code
/// the contract does not assign to them.
/// </remarks>
public static class TeardownFailureCodes
{
    /// <summary>Returns the contract code for a failure, or <c>null</c> when the contract names none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The reason is not a defined value.</exception>
    public static SessionErrorCode? ToSessionErrorCode(TeardownFailureReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown teardown failure reason.");
        }

        return reason switch
        {
            TeardownFailureReason.None => null,
            TeardownFailureReason.MissingChildSession => SessionErrorCode.State,
            TeardownFailureReason.LogOffFailed => null,
            TeardownFailureReason.RestoreFailed => null,
            TeardownFailureReason.ReleaseFailed => null,
            TeardownFailureReason.StateTransitionFailed => null,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown teardown failure reason."),
        };
    }
}
