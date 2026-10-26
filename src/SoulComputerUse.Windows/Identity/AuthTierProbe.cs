using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Identity;

/// <summary>
/// Probes the credential gate tier once at startup, as pinned by section 7.2 of the interface
/// contract. The facts are tried in the contract's order and the first one that holds decides:
/// Hello, then this session's own interactive logon, then refusal.
/// </summary>
public static class AuthTierProbe
{
    /// <summary>
    /// The whole decision table, free of interop so every combination can be asserted without a
    /// machine. The first fact that holds wins, so a machine with an enrolled Hello always reports
    /// <see cref="AuthTier.HelloEnrolled"/>, whatever its session logon state is.
    /// </summary>
    /// <param name="helloAvailableAndEnrolled">
    /// True when Windows Hello is usable and enrolled on this machine.
    /// </param>
    /// <param name="hasInteractiveLogonToken">
    /// True when the current session is an active, signed-in interactive session.
    /// </param>
    public static AuthTier Decide(bool helloAvailableAndEnrolled, bool hasInteractiveLogonToken)
    {
        if (helloAvailableAndEnrolled)
        {
            return AuthTier.HelloEnrolled;
        }

        if (hasInteractiveLogonToken)
        {
            return AuthTier.SystemCredential;
        }

        return AuthTier.None;
    }

    /// <summary>
    /// Decides the tier for this machine by combining the caller's Hello verdict with this session's
    /// own interactive logon fact. Hello availability and enrolment must be supplied by the caller
    /// from a system API; this library does not implement that probe, and it never assumes Hello is
    /// present. Returning true without asking the system would claim a strong gate the machine may
    /// not have, so the delegate must report a real system verdict and must never default to true.
    /// The machine fact is only read when the Hello verdict is false, matching the contract's
    /// step-by-step short circuit.
    /// </summary>
    /// <param name="helloAvailableAndEnrolled">Reports the caller's system verdict for Hello.</param>
    public static AuthTier Detect(Func<bool> helloAvailableAndEnrolled)
    {
        ArgumentNullException.ThrowIfNull(helloAvailableAndEnrolled);

        if (helloAvailableAndEnrolled())
        {
            return AuthTier.HelloEnrolled;
        }

        return Decide(false, InteractiveLogonProbe.HasInteractiveLogonToken());
    }

    /// <summary>
    /// The production name of <see cref="Detect"/>: combines the caller's injected Hello verdict with
    /// the real current-session logon probe. It goes through the same decision table, so the two
    /// entry points can never disagree.
    /// </summary>
    /// <param name="helloAvailableAndEnrolled">Reports the caller's system verdict for Hello.</param>
    public static AuthTier DetectMachine(Func<bool> helloAvailableAndEnrolled)
    {
        ArgumentNullException.ThrowIfNull(helloAvailableAndEnrolled);
        return Detect(helloAvailableAndEnrolled);
    }
}
