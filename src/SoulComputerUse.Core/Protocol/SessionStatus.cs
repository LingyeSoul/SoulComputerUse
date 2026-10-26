using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Core.Protocol;

/// <summary>The remote screen bounds reported by <c>get_status</c>.</summary>
public sealed record SessionScreen(int Width, int Height)
{
    /// <summary>Rejects a screen that cannot hold a valid coordinate.</summary>
    public void Validate()
    {
        if (Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), Width, "The screen width must be positive.");
        }

        if (Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Height), Height, "The screen height must be positive.");
        }
    }
}

/// <summary>One immutable answer to <c>get_status</c>.</summary>
public sealed record SessionStatus(
    string State,
    string SessionId,
    AuthTier AuthTier,
    GateStrength GateStrength,
    SessionScreen Screen,
    string? HelloHint)
{
    /// <summary>Rejects a status that could not have come from a real session.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(State);
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        ArgumentNullException.ThrowIfNull(Screen);
        Screen.Validate();

        if (AuthTier == AuthTier.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AuthTier),
                AuthTier,
                "The 'none' tier refuses to start, so it cannot be reported by a running session.");
        }

        if (HelloHint is not null && !string.Equals(HelloHint, "helloNotEnrolled", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(HelloHint),
                HelloHint,
                "The hello hint is an enumeration; 'helloNotEnrolled' is its only defined value.");
        }
    }
}
