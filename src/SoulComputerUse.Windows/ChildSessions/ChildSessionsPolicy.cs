using System;

namespace SoulComputerUse.Windows.ChildSessions;

/// <summary>What the machine says about one policy value.</summary>
public enum PolicyState
{
    /// <summary>The value exists and allows the operation.</summary>
    Allowed = 0,

    /// <summary>The value exists and forbids the operation.</summary>
    Denied = 1,

    /// <summary>The value does not exist. Treated as denied: guessing "allowed" can log the operator out.</summary>
    Missing = 2,
}

/// <summary>The two policy values that gate a child session.</summary>
public enum PolicyValue
{
    /// <summary>Whether remote desktop inbound is allowed on this machine.</summary>
    RdpInbound = 0,

    /// <summary>Whether child connections are allowed on this machine.</summary>
    ChildConnections = 1,
}

/// <summary>Why a policy value refused the connection.</summary>
public enum PolicyProblem
{
    /// <summary>The value exists and is non-zero.</summary>
    Denied = 0,

    /// <summary>The value does not exist at all.</summary>
    Missing = 1,
}

/// <summary>What the session may do given the machine's child-session state.</summary>
public enum ChildSessionsDecisionKind
{
    /// <summary>Child sessions are already allowed, so the session may connect.</summary>
    Proceed = 0,

    /// <summary>Child sessions are off but the machine allows them, so administrator consent is needed.</summary>
    RequestElevation = 1,

    /// <summary>The machine does not consent, so the session must not connect.</summary>
    Refuse = 2,
}

/// <summary>
/// One decision plus, for a refusal, the policy value that caused it and the reason. The payload is
/// what lets the caller tell the operator which value to fix instead of a generic "cannot connect".
/// </summary>
public sealed record ChildSessionsDecision(
    ChildSessionsDecisionKind Kind,
    PolicyValue? Value,
    PolicyProblem? Problem,
    string? Message)
{
    /// <summary>True when the decision does not forbid connecting.</summary>
    public bool AllowsConnection => Kind != ChildSessionsDecisionKind.Refuse;
}

/// <summary>
/// Pure decision table for the child-session switch lifecycle. Both hard preconditions are checked
/// first, because failing either must refuse the session even when the switch is already on:
/// connecting under a denied policy can log the operator out of their own desktop.
/// A missing policy value refuses the session instead of being assumed allowed.
/// </summary>
public static class ChildSessionsPolicy
{
    /// <summary>The policy value that gates RDP inbound.</summary>
    public const string RdpInboundValueName = "fDenyTSConnections";

    /// <summary>The policy value that gates child connections.</summary>
    public const string ChildConnectionsValueName = "fDenyChildConnections";

    /// <summary>Decides what to do without touching the machine.</summary>
    public static ChildSessionsDecision Evaluate(bool childSessionsEnabled, PolicyState rdpInbound, PolicyState childConnections)
    {
        ChildSessionsDecision? rdpRefusal = Refusal(PolicyValue.RdpInbound, RdpInboundValueName, rdpInbound);
        if (rdpRefusal is not null)
        {
            return rdpRefusal;
        }

        ChildSessionsDecision? childRefusal = Refusal(PolicyValue.ChildConnections, ChildConnectionsValueName, childConnections);
        if (childRefusal is not null)
        {
            return childRefusal;
        }

        return childSessionsEnabled
            ? new ChildSessionsDecision(ChildSessionsDecisionKind.Proceed, null, null, null)
            : new ChildSessionsDecision(
                ChildSessionsDecisionKind.RequestElevation,
                null,
                null,
                "Child sessions are disabled on this machine but not forbidden; administrator consent is required to enable them.");
    }

    /// <summary>
    /// The operator-facing explanation for one refused policy value. It names the policy value and
    /// says what makes it allow the session; policy value names are configuration keys, not real
    /// paths, so naming them is safe.
    /// </summary>
    public static string DescribeProblem(string valueName, PolicyProblem problem)
    {
        if (string.IsNullOrEmpty(valueName))
        {
            throw new ArgumentException("The policy value name must not be empty.", nameof(valueName));
        }

        return problem switch
        {
            PolicyProblem.Denied =>
                $"The policy value '{valueName}' is non-zero, which forbids the connection; a local connection would take over the operator's session. Set it to a DWORD 0 to allow it.",
            PolicyProblem.Missing =>
                $"The policy value '{valueName}' is missing, so this machine has not consented to a connection. Create it as a DWORD 0 to allow it.",
            _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Unknown policy problem."),
        };
    }

    private static ChildSessionsDecision? Refusal(PolicyValue value, string valueName, PolicyState state)
    {
        return state switch
        {
            PolicyState.Allowed => null,
            PolicyState.Denied => new ChildSessionsDecision(
                ChildSessionsDecisionKind.Refuse,
                value,
                PolicyProblem.Denied,
                DescribeProblem(valueName, PolicyProblem.Denied)),
            PolicyState.Missing => new ChildSessionsDecision(
                ChildSessionsDecisionKind.Refuse,
                value,
                PolicyProblem.Missing,
                DescribeProblem(valueName, PolicyProblem.Missing)),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown policy state."),
        };
    }
}