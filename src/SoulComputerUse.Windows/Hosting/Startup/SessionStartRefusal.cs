using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// One refused gate of the startup pipeline: the contract code and the fixed explanation that belongs
/// to it. It travels as a value so the runner can refuse at the exact stage that found the problem
/// without restating any rule or message.
/// </summary>
public sealed record SessionStartRefusal
{
    /// <summary>Creates one refusal and refuses an empty explanation.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined value.</exception>
    /// <exception cref="ArgumentException">The explanation is empty or whitespace.</exception>
    public SessionStartRefusal(SessionErrorCode code, string reason)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown session error code.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Code = code;
        Reason = reason;
    }

    /// <summary>The contract error code of this refusal.</summary>
    public SessionErrorCode Code { get; }

    /// <summary>The fixed explanation, built from templates plus enumerations and counts only.</summary>
    public string Reason { get; }
}
