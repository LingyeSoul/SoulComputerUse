using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The credential gate tier the child-session agent detected for itself, together with the gate
/// strength the contract pins to that tier and the optional operator hint of section 5.1.
/// </summary>
/// <remarks>
/// The refusal tier carries no gate strength, so <see cref="Strength"/> is null exactly when
/// <see cref="Tier"/> is <see cref="AuthTier.None"/>: a session that never starts has no gate to
/// describe, and there is no strength value that could stand in for one. The hint is an enumeration
/// of section 5.1 and never display text. The two optionals are deliberately separate: a caller that
/// wants a strength must not be able to invent one, because a session that claimed a strong gate
/// without Hello would be a false security claim.
/// </remarks>
public sealed record AgentAuthTier(AuthTier Tier, GateStrength? Strength, string? HelloHint)
{
    /// <summary>The only defined value of <see cref="HelloHint"/>, pinned by section 5.1.</summary>
    public const string HelloNotEnrolledHint = "helloNotEnrolled";

    /// <summary>True when this tier refuses to start.</summary>
    public bool RefusesStartup => Tier == AuthTier.None;

    /// <summary>
    /// Returns the gate strength, refusing the tier that never starts. Asking the refusal tier for a
    /// strength is a caller error rather than a value to invent.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tier is the refusal tier.</exception>
    public GateStrength RequireStrength()
    {
        return Strength ?? throw new InvalidOperationException(
            "The refusal tier never starts a session, so it has no gate strength to return.");
    }

    /// <summary>Rejects a tier, strength and hint combination the contract cannot describe.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tier or the hint value is not a contract value.</exception>
    /// <exception cref="InvalidOperationException">The strength or the hint does not belong to the tier.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(Tier))
        {
            throw new ArgumentOutOfRangeException(nameof(Tier), Tier, "Unknown authentication tier.");
        }

        if (Tier == AuthTier.None)
        {
            if (Strength is not null)
            {
                throw new InvalidOperationException(
                    "The refusal tier never starts a session, so it must not carry a gate strength.");
            }

            if (HelloHint is not null)
            {
                throw new InvalidOperationException(
                    "The refusal tier never starts a session, so it must not carry an operator hint.");
            }

            return;
        }

        GateStrength expected = AuthTierRules.StrengthFor(Tier);
        if (Strength != expected)
        {
            throw new InvalidOperationException(
                $"The '{AuthTierNames.ToWireName(Tier)}' tier has gate strength '{GateStrengthNames.ToWireName(expected)}', so no other strength may be reported for it.");
        }

        if (HelloHint is null)
        {
            return;
        }

        if (Tier != AuthTier.SystemCredential)
        {
            throw new InvalidOperationException(
                $"The operator hint belongs to the '{AuthTierNames.ToWireName(AuthTier.SystemCredential)}' tier only.");
        }

        if (!string.Equals(HelloHint, HelloNotEnrolledHint, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(HelloHint),
                HelloHint,
                $"The operator hint is an enumeration, and '{HelloNotEnrolledHint}' is its only defined value.");
        }
    }
}