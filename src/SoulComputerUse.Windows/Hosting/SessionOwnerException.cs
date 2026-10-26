using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting;

/// <summary>
/// A session owner operation that the contract refuses: the machine-wide lock is already held, or
/// the state machine does not allow the requested transition.
/// </summary>
/// <remarks>
/// The code uses the contract's section 2 enumeration so a caller can map the failure without
/// parsing the message. Messages are fixed templates and never carry a path, an account identifier
/// or any other machine-specific value.
/// </remarks>
public sealed class SessionOwnerException : Exception
{
    /// <summary>Creates the exception around one contract error code.</summary>
    public SessionOwnerException(SessionErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Creates the exception around one contract error code with the failure that caused it.</summary>
    public SessionOwnerException(SessionErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>The contract error code that describes the refusal.</summary>
    public SessionErrorCode Code { get; }
}