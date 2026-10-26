using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Cli;

/// <summary>
/// A command line the parser refuses. The code is one of the contract error codes used by the CLI's
/// input checks, so the caller prints it without translating it.
/// </summary>
internal sealed class CliParseException : Exception
{
    /// <summary>Creates the exception for one refused command line.</summary>
    internal CliParseException(SessionErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The contract error code of the refusal.</summary>
    internal SessionErrorCode Code { get; }
}