using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Identity;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// Decides the credential gate tier inside the child session, as pinned by section 7.2 of the
/// interface contract. The facts are tried in the contract's order and the first one that holds
/// decides: Hello enrolment, then this session's own interactive logon, then refusal. The agent
/// detects the tier for itself, so a value on the command line can never become the gate the agent
/// reports.
/// </summary>
/// <remarks>
/// This type does not implement the Windows Hello availability query. Hello availability and
/// enrolment are a machine fact that the caller must supply from a system API, exactly as
/// <c>AuthTierProbe</c> declares, and the caller must never default that fact to true: claiming an
/// enrolled Hello without a system verdict would assert a strong gate that may not exist.
/// </remarks>
public static class AgentAuthTierDetector
{
    /// <summary>
    /// The whole decision table, free of interop so every combination can be asserted. The hardware
    /// hint is only read on the branch that reports it: an enrolled Hello decides before the hint is
    /// considered, and the refusal tier never reports one.
    /// </summary>
    /// <param name="helloAvailableAndEnrolled">True when Windows Hello is usable and enrolled on this machine.</param>
    /// <param name="hasInteractiveLogonToken">True when the current session is an active, signed-in interactive session.</param>
    /// <param name="helloHardwarePresentButNotEnrolled">True when Hello hardware exists on this machine but no Hello credential is enrolled.</param>
    /// <exception cref="ArgumentOutOfRangeException">A fact combination is not one the contract defines.</exception>
    public static AgentAuthTier Decide(
        bool helloAvailableAndEnrolled,
        bool hasInteractiveLogonToken,
        bool helloHardwarePresentButNotEnrolled)
    {
        AgentAuthTier detected;
        if (helloAvailableAndEnrolled)
        {
            detected = new AgentAuthTier(AuthTier.HelloEnrolled, GateStrength.Strong, null);
        }
        else if (hasInteractiveLogonToken)
        {
            string? hint = helloHardwarePresentButNotEnrolled ? AgentAuthTier.HelloNotEnrolledHint : null;
            detected = new AgentAuthTier(AuthTier.SystemCredential, GateStrength.Login, hint);
        }
        else
        {
            detected = new AgentAuthTier(AuthTier.None, null, null);
        }

        detected.Validate();
        return detected;
    }

    /// <summary>
    /// Detects the tier from live probes, reading each fact only when the contract's order reaches it.
    /// The interactive logon fact comes from the real current-session probe.
    /// </summary>
    /// <param name="helloAvailableAndEnrolled">Reports the caller's system verdict for Hello availability and enrolment.</param>
    /// <param name="helloHardwarePresent">Reports whether Hello hardware exists although no Hello credential is enrolled.</param>
    /// <exception cref="ArgumentNullException">A probe is null.</exception>
    public static AgentAuthTier Detect(Func<bool> helloAvailableAndEnrolled, Func<bool> helloHardwarePresent)
    {
        return Detect(helloAvailableAndEnrolled, helloHardwarePresent, InteractiveLogonProbe.HasInteractiveLogonToken);
    }

    /// <summary>
    /// Detects the tier from three injectable facts, so the contract's short circuit can be asserted
    /// without a machine. An enrolled Hello decides before the logon fact and the hardware hint are
    /// read, and a session with no interactive logon refuses to start before the hint is read.
    /// </summary>
    /// <param name="helloAvailableAndEnrolled">Reports the caller's system verdict for Hello availability and enrolment.</param>
    /// <param name="helloHardwarePresent">Reports whether Hello hardware exists although no Hello credential is enrolled.</param>
    /// <param name="hasInteractiveLogonToken">Reports whether the current session carries an interactive logon.</param>
    /// <exception cref="ArgumentNullException">A probe is null.</exception>
    public static AgentAuthTier Detect(
        Func<bool> helloAvailableAndEnrolled,
        Func<bool> helloHardwarePresent,
        Func<bool> hasInteractiveLogonToken)
    {
        ArgumentNullException.ThrowIfNull(helloAvailableAndEnrolled);
        ArgumentNullException.ThrowIfNull(helloHardwarePresent);
        ArgumentNullException.ThrowIfNull(hasInteractiveLogonToken);

        if (helloAvailableAndEnrolled())
        {
            // The Hello verdict decides, so neither the session fact nor the hardware hint is read.
            return Decide(true, false, false);
        }

        if (!hasInteractiveLogonToken())
        {
            // Without Hello and without an interactive logon the session refuses to start, so no hint
            // is reported and the hardware fact is not read.
            return Decide(false, false, false);
        }

        return Decide(false, true, helloHardwarePresent());
    }

    /// <summary>
    /// Detects the tier and turns a failed probe into an explicit refusal. A probe that throws must
    /// never be read as a weaker tier: the caller starts the agent with the returned value, so a
    /// silent fallback would run a session under a gate nobody verified. The original failure is
    /// kept as the inner exception, and no tier is returned.
    /// </summary>
    /// <param name="helloAvailableAndEnrolled">Reports the caller's system verdict for Hello availability and enrolment.</param>
    /// <param name="helloHardwarePresent">Reports whether Hello hardware exists although no Hello credential is enrolled.</param>
    /// <exception cref="ArgumentNullException">A probe is null.</exception>
    /// <exception cref="InvalidOperationException">A probe failed, so no tier could be detected.</exception>
    public static AgentAuthTier DetectFailClosed(
        Func<bool> helloAvailableAndEnrolled,
        Func<bool> helloHardwarePresent)
    {
        return DetectFailClosed(
            helloAvailableAndEnrolled,
            helloHardwarePresent,
            InteractiveLogonProbe.HasInteractiveLogonToken);
    }

    /// <summary>
    /// Detects the tier from three injectable facts and turns a failed probe into an explicit refusal,
    /// so the fail-closed path itself can be asserted without a machine.
    /// </summary>
    /// <param name="helloAvailableAndEnrolled">Reports the caller's system verdict for Hello availability and enrolment.</param>
    /// <param name="helloHardwarePresent">Reports whether Hello hardware exists although no Hello credential is enrolled.</param>
    /// <param name="hasInteractiveLogonToken">Reports whether the current session carries an interactive logon.</param>
    /// <exception cref="ArgumentNullException">A probe is null.</exception>
    /// <exception cref="InvalidOperationException">A probe failed, so no tier could be detected.</exception>
    public static AgentAuthTier DetectFailClosed(
        Func<bool> helloAvailableAndEnrolled,
        Func<bool> helloHardwarePresent,
        Func<bool> hasInteractiveLogonToken)
    {
        ArgumentNullException.ThrowIfNull(helloAvailableAndEnrolled);
        ArgumentNullException.ThrowIfNull(helloHardwarePresent);
        ArgumentNullException.ThrowIfNull(hasInteractiveLogonToken);

        try
        {
            return Detect(helloAvailableAndEnrolled, helloHardwarePresent, hasInteractiveLogonToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Every probe failure, including one that surfaces as an argument exception, becomes the
            // same explicit refusal: no tier is returned, so the caller cannot start on a guess.
            throw new InvalidOperationException(
                "The credential gate tier of this child session could not be detected, so the agent refuses to start under an unverified gate.",
                error);
        }
    }
}