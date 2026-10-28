using System;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>Whether the calling session's desktop accepts capture and input right now.</summary>
public enum DesktopAvailabilityVerdict
{
    /// <summary>The interactive default desktop is reachable, so capture and input are allowed.</summary>
    Usable = 0,

    /// <summary>The input desktop cannot be opened, which is the signature of a locked desktop.</summary>
    BlockedLockedDesktop = 1,

    /// <summary>
    /// The input desktop exists but is a protected one, either the <c>Winlogon</c> desktop of a UAC
    /// prompt or the logon screen, or any other desktop that is not the interactive default.
    /// </summary>
    BlockedSecureDesktop = 2,

    /// <summary>The session carries no interactive console, so no picture can be captured and no input delivered.</summary>
    BlockedNonInteractive = 3,

    /// <summary>
    /// The session still exists but no client is attached to it. Windows keeps the desktop in
    /// memory, so it can still answer a capture and even open its input desktop; nothing that
    /// happens there reaches an operator, and the contract has no automatic reconnect, so this is
    /// the terminal <c>disconnected</c> state rather than a blocked one.
    /// </summary>
    Disconnected = 4,
}

/// <summary>The connection state Windows Terminal Services reports for the calling session.</summary>
public enum DesktopSessionConnectState
{
    /// <summary><c>WTSActive</c>: a signed-in user is attached to an interactive session.</summary>
    Active = 0,

    /// <summary>
    /// <c>WTSDisconnected</c>: the session is still running but its client is gone. Input and
    /// capture stay impossible until a new session is started.
    /// </summary>
    Disconnected = 1,

    /// <summary>Any other connection state, which this build treats as non-interactive.</summary>
    Other = 2,
}

/// <summary>
/// Pure decision behind the blocked-state check. It is kept free of interop so every input
/// combination can be asserted without a locked machine, a UAC prompt or a second session.
/// </summary>
public static class DesktopAvailability
{
    /// <summary>The window station an interactive session runs on.</summary>
    public const string InteractiveWindowStation = "WinSta0";

    /// <summary>The input desktop of an unlocked, interactive session.</summary>
    public const string DefaultDesktopName = "Default";

    /// <summary>The desktop that carries the logon screen and the UAC consent prompt.</summary>
    public const string SecureDesktopName = "Winlogon";

    /// <summary>
    /// Classifies one set of observed desktop facts. The checks run in a fixed order, and each step
    /// depends on the one before it:
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1. Session state first. A session Windows reports as <c>WTSDisconnected</c> is answered as
    /// <see cref="DesktopAvailabilityVerdict.Disconnected"/>: its desktop can still look healthy and
    /// even open, but nobody is attached, and the contract has no reconnect. Any other state that is
    /// not <c>WTSActive</c>, or that runs on a window station other than <c>WinSta0</c>, is not
    /// showing anything to anyone either, so nothing may be captured or typed. This must be the
    /// first gate because the later facts can still look healthy on such a session. Checking names
    /// first would therefore call a dead session usable.
    /// </para>
    /// <para>
    /// 2. Then the input-desktop handle. A refusal to open the input desktop is the observable
    /// signature of a locked desktop, where the input belongs to the <c>Winlogon</c> desktop the
    /// caller is not allowed to open. It has to come after the session gate because a
    /// non-interactive session also fails to open its input desktop, and it has to come before the
    /// name checks because a failed open yields no name to check at all.
    /// </para>
    /// <para>
    /// 3. Then the <c>Winlogon</c> name explicitly, so a UAC or logon desktop is reported as what it
    /// is instead of falling into the catch-all below.
    /// </para>
    /// <para>
    /// 4. Then every other name. Only <c>Default</c> is the interactive desktop; a screen saver
    /// desktop or a name this build does not know is refused rather than guessed, because guessing
    /// "usable" puts input on a desktop the operator cannot see.
    /// </para>
    /// </remarks>
    /// <param name="inputDesktopOpenSucceeded">True when the input desktop handle was obtained.</param>
    /// <param name="sessionConnectState">The connection state Windows reports for this session.</param>
    /// <param name="inputDesktopName">
    /// The name read from the opened input desktop, or null when the handle could not be opened.
    /// </param>
    /// <param name="sessionWindowStation">The window station the calling process runs on.</param>
    /// <exception cref="ArgumentException">The window station name is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The connection state is not defined.</exception>
    public static DesktopAvailabilityVerdict Evaluate(
        bool inputDesktopOpenSucceeded,
        DesktopSessionConnectState sessionConnectState,
        string? inputDesktopName,
        string sessionWindowStation)
    {
        if (string.IsNullOrWhiteSpace(sessionWindowStation))
        {
            throw new ArgumentException(
                "The session window station name must not be empty, because an unreadable station cannot prove that the session is interactive.",
                nameof(sessionWindowStation));
        }

        if (!Enum.IsDefined(sessionConnectState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionConnectState),
                sessionConnectState,
                "The connection state is not a defined value, so it cannot be classified.");
        }

        if (sessionConnectState == DesktopSessionConnectState.Disconnected)
        {
            return DesktopAvailabilityVerdict.Disconnected;
        }

        if (sessionConnectState != DesktopSessionConnectState.Active
            || !string.Equals(sessionWindowStation, InteractiveWindowStation, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopAvailabilityVerdict.BlockedNonInteractive;
        }

        if (!inputDesktopOpenSucceeded)
        {
            return DesktopAvailabilityVerdict.BlockedLockedDesktop;
        }

        if (string.Equals(inputDesktopName, SecureDesktopName, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopAvailabilityVerdict.BlockedSecureDesktop;
        }

        if (!string.Equals(inputDesktopName, DefaultDesktopName, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopAvailabilityVerdict.BlockedSecureDesktop;
        }

        return DesktopAvailabilityVerdict.Usable;
    }
}
