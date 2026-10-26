using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// The exception one failing phase raises: the contract code, the phase that failed, and a fixed
/// explanation.
/// </summary>
/// <remarks>
/// The message is a fixed template from <see cref="SessionStartPhaseMessages"/> plus the phase's wire
/// name, so it never carries a path, an account, a SID or a nonce, and never the inner failure's message:
/// a pipe error or a record error can name a path, and the phase is what the caller needs anyway. The
/// inner exception stays available for a developer through <see cref="Exception.InnerException"/>.
/// </remarks>
public sealed class SessionPhaseException : Exception
{
    /// <summary>Creates the exception for one failed phase.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined value, or the phase is not.</exception>
    /// <exception cref="ArgumentException">The explanation is empty or whitespace.</exception>
    public SessionPhaseException(SessionErrorCode code, SessionStartPhase phase, string message, Exception? innerException)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown session error code.");
        }

        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown session start phase.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Phase = phase;
    }

    /// <summary>The contract error code of the failure.</summary>
    public SessionErrorCode Code { get; }

    /// <summary>The phase this attempt had entered when it failed.</summary>
    public SessionStartPhase Phase { get; }
}
