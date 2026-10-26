using System;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Renders the contract's section 2 error object as the text of a failed tool result. The
/// retryability comes from the error catalog, so a client cannot widen it, and the state is omitted
/// rather than invented when it cannot be established.
/// </summary>
internal static class SessionErrorJson
{
    /// <summary>Renders one error object.</summary>
    internal static string Create(SessionOperationException error, SessionStatus? status)
    {
        ArgumentNullException.ThrowIfNull(error);

        SessionErrorRule rule = SessionErrorCatalog.For(error.Code);
        SessionState? state = ResolveState(error.Code, status);
        if (state is not null && !rule.AllowsState(state))
        {
            state = null;
        }

        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("code", SessionErrorCodeNames.ToWireName(error.Code));
            writer.WriteString("message", error.Message);
            writer.WriteBoolean("retryable", rule.Retryable);
            if (state is not null)
            {
                writer.WriteString("state", SessionStateNames.ToWireName(state.Value));
            }

            writer.WriteEndObject();
        });
    }

    private static SessionState? ResolveState(SessionErrorCode code, SessionStatus? status)
    {
        if (status is not null)
        {
            SessionState? reported = TryParse(status.State);
            if (reported is not null)
            {
                return reported;
            }
        }

        // Without a status the code itself can still pin the state when the contract allows just one.
        SessionErrorRule rule = SessionErrorCatalog.For(code);
        return rule.StateScope == SessionErrorStateScope.ListedStates && rule.States.Count == 1
            ? rule.States[0]
            : null;
    }

    private static SessionState? TryParse(string wireName)
    {
        try
        {
            return SessionStateNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
