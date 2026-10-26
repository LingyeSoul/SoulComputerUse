using System;

namespace SoulComputerUse.Core.Session;

/// <summary>
/// The sanitized error object of section 2 of the interface contract. It carries exactly the four
/// fields the contract defines: the code, the message, whether the operation is retryable, and the
/// session state the error occurred in.
/// </summary>
/// <remarks>
/// The catalog owns retryability and the states a code may occur in, so this type refuses a caller
/// that contradicts it. Agent self-exit codes are log-only and therefore carry no host state.
/// </remarks>
public sealed record SessionError
{
    /// <summary>Creates one error object and checks it against the catalog.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined <see cref="SessionErrorCode"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The message is empty, the retryable value contradicts the catalog, or the state is not allowed
    /// for the code.
    /// </exception>
    public SessionError(SessionErrorCode code, string message, bool retryable, SessionState? state)
    {
        SessionErrorRule rule = SessionErrorCatalog.For(code);

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("An error message must not be null, empty, or whitespace.", nameof(message));
        }

        if (retryable != rule.Retryable)
        {
            throw new ArgumentException(
                $"The catalog pins '{SessionErrorCodeNames.ToWireName(code)}' to retryable={rule.Retryable}, but retryable={retryable} was supplied.",
                nameof(retryable));
        }

        if (!rule.AllowsState(state))
        {
            string suppliedState = state is null
                ? "no state"
                : $"state '{SessionStateNames.ToWireName(state.Value)}'";
            throw new ArgumentException(
                $"The code '{SessionErrorCodeNames.ToWireName(code)}' allows {rule.DescribeAllowedStates()}, but {suppliedState} was supplied.",
                nameof(state));
        }

        Code = code;
        Message = message;
        Retryable = retryable;
        State = state;
    }

    /// <summary>The contract error code.</summary>
    public SessionErrorCode Code { get; }

    /// <summary>The sanitized message: a fixed template plus numeric or enumerated parameters only.</summary>
    public string Message { get; }

    /// <summary>Whether the failed operation may be retried, as pinned by the catalog.</summary>
    public bool Retryable { get; }

    /// <summary>
    /// The session state the error occurred in. It is null only for agent self-exit codes, which
    /// exist in the agent's own log and never in the tool error channel.
    /// </summary>
    public SessionState? State { get; }

    /// <summary>Creates an error whose retryable value is taken from the catalog.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined <see cref="SessionErrorCode"/>.</exception>
    /// <exception cref="ArgumentException">The message is empty or the state is not allowed for the code.</exception>
    public static SessionError Create(SessionErrorCode code, string message, SessionState? state)
    {
        SessionErrorRule rule = SessionErrorCatalog.For(code);
        return new SessionError(code, message, rule.Retryable, state);
    }
}
