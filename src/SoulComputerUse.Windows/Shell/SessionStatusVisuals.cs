using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Shell;

/// <summary>The three reasons a session can be in the blocked state.</summary>
public enum BlockedCause
{
    /// <summary>A UAC prompt or a secure desktop takes the input away from the agent.</summary>
    ProtectedDesktop = 0,

    /// <summary>The session is not interactive, so no picture can be captured.</summary>
    NonInteractiveSession = 1,

    /// <summary>Local policy forbids child-session connections, or the policy value is unreadable.</summary>
    ChildSessionPolicy = 2,
}

/// <summary>
/// What the caption shows for one session state: the status dot colour, the status text taken from
/// the state machine's enumerations and the gate tier short label.
/// </summary>
/// <remarks>
/// Every string here comes from the enumeration it belongs to; the caption never assembles a status
/// sentence of its own, so the wording cannot drift away from the contract.
/// The ready state deliberately has no extra note: gate strength is a machine-readable field on
/// <c>get_status</c>, not something the title bar annotates.
/// </remarks>
public sealed record SessionStatusVisual(
    CaptionColor DotColor,
    string CaptionText,
    string GateTierLabel);

/// <summary>Maps a session state, its gate tier and its gate strength to what the caption renders.</summary>
public static class SessionStatusVisuals
{
    /// <summary>
    /// Builds the caption status for a state. <paramref name="blockedCause"/> must be given for the
    /// blocked state and must be omitted for every other state, so no state can silently borrow the
    /// wrong sentence.
    /// </summary>
    public static SessionStatusVisual Describe(
        SessionState state,
        AuthTier tier,
        GateStrength strength,
        BlockedCause? blockedCause)
    {
        AuthTierRules.EnsureConsistent(tier, strength);
        if (tier == AuthTier.None)
        {
            throw new InvalidOperationException(
                "The 'none' tier refuses to start, so it has no caption state to render.");
        }

        if (state == SessionState.Blocked)
        {
            if (blockedCause is null)
            {
                throw new ArgumentNullException(
                    nameof(blockedCause),
                    "The blocked state needs its cause, because the caption text comes from the cause.");
            }

            return new SessionStatusVisual(
                CaptionColor.FromRgb(0xD2A8FF),
                BlockedText(blockedCause.Value),
                string.Empty);
        }

        if (blockedCause is not null)
        {
            throw new ArgumentException(
                $"A blocked cause ({blockedCause.Value}) only belongs to the blocked state, but the state is {SessionStateNames.ToWireName(state)}.",
                nameof(blockedCause));
        }

        return state switch
        {
            SessionState.Starting => new SessionStatusVisual(
                CaptionColor.FromRgb(0x9CA0A6),
                "正在启动…",
                string.Empty),
            SessionState.AwaitingCredentials => new SessionStatusVisual(
                CaptionColor.FromRgb(0xF9A825),
                "等待验证 — 请在桌面窗口手动完成",
                tier == AuthTier.HelloEnrolled ? "请完成系统验证" : string.Empty),
            SessionState.Connecting => new SessionStatusVisual(
                CaptionColor.FromRgb(0x4EA6F0),
                "正在连接…",
                string.Empty),
            SessionState.Ready => new SessionStatusVisual(
                CaptionColor.FromRgb(0x3FB950),
                string.Empty,
                string.Empty),
            SessionState.Disconnected => new SessionStatusVisual(
                CaptionColor.FromRgb(0xF85149),
                "连接已断开",
                string.Empty),
            SessionState.Closing => new SessionStatusVisual(
                CaptionColor.FromRgb(0x9CA0A6),
                "正在结束会话…",
                string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown session state."),
        };
    }

    /// <summary>Builds the window title from the product name and a session label.</summary>
    /// <remarks>
    /// The label is a placeholder chosen by the caller, never a real session identifier, user name or
    /// machine name: the title bar is visible on screen and ends up in screenshots.
    /// </remarks>
    public static string BuildTitle(string sessionLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionLabel);
        return $"SoulComputerUse — {sessionLabel.Trim()}";
    }

    private static string BlockedText(BlockedCause cause)
    {
        return cause switch
        {
            BlockedCause.ProtectedDesktop => "不支持：UAC / 安全桌面，请人工处理",
            BlockedCause.NonInteractiveSession => "不支持：非交互会话",
            BlockedCause.ChildSessionPolicy => "不支持：本机策略禁止子会话连接",
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unknown blocked cause."),
        };
    }
}
