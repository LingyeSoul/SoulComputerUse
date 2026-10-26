using System;

namespace SoulComputerUse.Core.Session;

/// <summary>
/// The credential gate tier probed once at startup, as pinned by section 7.2 of the interface
/// contract. There are exactly three tiers and nothing else may invent one.
/// </summary>
public enum AuthTier
{
    /// <summary>Windows Hello is available and enrolled, so every start needs a Hello check.</summary>
    HelloEnrolled = 0,

    /// <summary>
    /// No usable Hello enrolment. The gate is the child session's own Windows logon screen: the
    /// operator signs in by hand inside the session window, and the application never sees a
    /// credential. A session in this tier must pass through awaiting-credentials before it may
    /// become ready, so the operator's own logon is what admits it.
    /// </summary>
    SystemCredential = 1,

    /// <summary>Not even the system credential path is usable, so startup is refused.</summary>
    None = 2,
}

/// <summary>
/// How strong the gate of a session actually is. It is a machine-readable restatement of
/// <see cref="AuthTier"/> for callers, never an independent decision.
/// </summary>
public enum GateStrength
{
    /// <summary>The operator was verified by the system in this session (Hello).</summary>
    Strong = 0,

    /// <summary>
    /// The operator logged on at the child session's own Windows logon screen. That is a real system
    /// verification performed by Windows, so it is a gate, not a session that merely reused an
    /// existing logon.
    /// </summary>
    Login = 1,
}

/// <summary>The contract wire names of <see cref="AuthTier"/>.</summary>
public static class AuthTierNames
{
    /// <summary>Returns the contract wire name of a tier.</summary>
    public static string ToWireName(AuthTier tier)
    {
        return tier switch
        {
            AuthTier.HelloEnrolled => "hello-enrolled",
            AuthTier.SystemCredential => "system-credential",
            AuthTier.None => "none",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown authentication tier."),
        };
    }

    /// <summary>Parses a contract wire name back into a tier.</summary>
    public static AuthTier FromWireName(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        return wireName switch
        {
            "hello-enrolled" => AuthTier.HelloEnrolled,
            "system-credential" => AuthTier.SystemCredential,
            "none" => AuthTier.None,
            _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, "Unknown authentication tier wire name."),
        };
    }
}

/// <summary>The contract wire names of <see cref="GateStrength"/>.</summary>
public static class GateStrengthNames
{
    /// <summary>Returns the contract wire name of a gate strength.</summary>
    public static string ToWireName(GateStrength strength)
    {
        return strength switch
        {
            GateStrength.Strong => "strong",
            GateStrength.Login => "login",
            _ => throw new ArgumentOutOfRangeException(nameof(strength), strength, "Unknown gate strength."),
        };
    }

    /// <summary>Parses a contract wire name back into a gate strength.</summary>
    public static GateStrength FromWireName(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        return wireName switch
        {
            "strong" => GateStrength.Strong,
            "login" => GateStrength.Login,
            _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, "Unknown gate strength wire name."),
        };
    }
}

/// <summary>
/// The fixed relation between a tier and its gate strength. Callers must not pick a strength of
/// their own: a session that claims a strong gate without Hello would be a false security claim.
/// </summary>
public static class AuthTierRules
{
    /// <summary>Returns the gate strength the contract pins to a tier.</summary>
    public static GateStrength StrengthFor(AuthTier tier)
    {
        return tier switch
        {
            AuthTier.HelloEnrolled => GateStrength.Strong,
            AuthTier.SystemCredential => GateStrength.Login,
            AuthTier.None => throw new InvalidOperationException(
                "The 'none' tier refuses to start, so it never has a gate strength."),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown authentication tier."),
        };
    }

    /// <summary>Throws when a reported strength contradicts its tier.</summary>
    public static void EnsureConsistent(AuthTier tier, GateStrength strength)
    {
        GateStrength expected = StrengthFor(tier);
        if (expected != strength)
        {
            throw new InvalidOperationException(
                $"The '{AuthTierNames.ToWireName(tier)}' tier has gate strength '{GateStrengthNames.ToWireName(expected)}', but '{GateStrengthNames.ToWireName(strength)}' was reported.");
        }
    }
}
