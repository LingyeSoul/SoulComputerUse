using System;

namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// The four phases of a session start, in the order the contract allows them. The numeric values are
/// that order, so comparing two phases tells which one comes first.
/// </summary>
/// <remarks>
/// The phases exist because the agent is an alternate shell of the child session: it is started only
/// after the operator has logged on in the remote desktop window, so it cannot be attached before the
/// connection exists. A start that attached the agent before opening the connection would find no agent
/// on a production machine and would report a failure that the real order never produces.
/// <para>
/// <see cref="AwaitingCredentials"/> is the phase the window spends showing the Windows logon screen:
/// the operator logs on personally, and no part of this product enters a credential. The phase is
/// therefore an observation of the remote window, not a prompt this product renders.
/// </para>
/// </remarks>
public enum SessionStartPhase
{
    /// <summary>
    /// Before anything connects: lock, ownership record, credential tier, leftover sweep, the
    /// child-session switch and the alternate-shell properties.
    /// </summary>
    BeforeConnect = 0,

    /// <summary>The connection is open and the window waits for the operator to log on personally.</summary>
    AwaitingCredentials = 1,

    /// <summary>The operator has logged on, so the agent can be attached and the owner pipe started.</summary>
    AfterLogon = 2,

    /// <summary>The agent is attached, the child session is recorded and the owner pipe serves calls.</summary>
    Ready = 3,
}

/// <summary>
/// The wire names of <see cref="SessionStartPhase"/> and the parser that refuses an unknown name. The
/// names are pinned because a report, a log field and the host's caption all carry them.
/// </summary>
public static class SessionStartPhaseNames
{
    /// <summary>The wire name of <see cref="SessionStartPhase.BeforeConnect"/>.</summary>
    public const string BeforeConnect = "before-connect";

    /// <summary>The wire name of <see cref="SessionStartPhase.AwaitingCredentials"/>.</summary>
    public const string AwaitingCredentials = "awaiting-credentials";

    /// <summary>The wire name of <see cref="SessionStartPhase.AfterLogon"/>.</summary>
    public const string AfterLogon = "after-logon";

    /// <summary>The wire name of <see cref="SessionStartPhase.Ready"/>.</summary>
    public const string Ready = "ready";

    /// <summary>Returns the wire name of one phase.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The phase is not a defined value.</exception>
    public static string ToWireName(SessionStartPhase phase)
    {
        return phase switch
        {
            SessionStartPhase.BeforeConnect => BeforeConnect,
            SessionStartPhase.AwaitingCredentials => AwaitingCredentials,
            SessionStartPhase.AfterLogon => AfterLogon,
            SessionStartPhase.Ready => Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown session start phase."),
        };
    }

    /// <summary>
    /// Parses one wire name. An unknown name is refused instead of being guessed at, because a phase a
    /// caller cannot name is a phase this product does not have.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The name is not one of the four phase names.</exception>
    public static SessionStartPhase FromWireName(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);

        return wireName switch
        {
            BeforeConnect => SessionStartPhase.BeforeConnect,
            AwaitingCredentials => SessionStartPhase.AwaitingCredentials,
            AfterLogon => SessionStartPhase.AfterLogon,
            Ready => SessionStartPhase.Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, "Unknown session start phase wire name."),
        };
    }

    /// <summary>
    /// Parses one wire name and reports whether it is a phase name, without throwing for an unknown
    /// value. A null name is not a phase and is reported the same way as an unknown one.
    /// </summary>
    public static bool TryFromWireName(string? wireName, out SessionStartPhase phase)
    {
        switch (wireName)
        {
            case BeforeConnect:
                phase = SessionStartPhase.BeforeConnect;
                return true;
            case AwaitingCredentials:
                phase = SessionStartPhase.AwaitingCredentials;
                return true;
            case AfterLogon:
                phase = SessionStartPhase.AfterLogon;
                return true;
            case Ready:
                phase = SessionStartPhase.Ready;
                return true;
            default:
                phase = default;
                return false;
        }
    }
}
