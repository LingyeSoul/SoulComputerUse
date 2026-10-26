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
    /// 1. Session state first. A session that is not <c>WTSActive</c>, or that runs on a window
    /// station other than <c>WinSta0</c>, is not showing anything to anyone, so nothing may be
    /// captured or typed. This must be the first gate because the later facts can still look
    /// healthy on such a session: a disconnected session keeps a desktop named <c>Default</c>, and
    /// opening it can even succeed. Checking names first would therefore call a dead session
    /// usable.
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
    /// <param name="sessionIsActive">True when Windows Terminal Services reports <c>WTSActive</c>.</param>
    /// <param name="inputDesktopName">
    /// The name read from the opened input desktop, or null when the handle could not be opened.
    /// </param>
    /// <param name="sessionWindowStation">The window station the calling process runs on.</param>
    /// <exception cref="ArgumentException">The window station name is null, empty or whitespace.</exception>
    public static DesktopAvailabilityVerdict Evaluate(
        bool inputDesktopOpenSucceeded,
        bool sessionIsActive,
        string? inputDesktopName,
        string sessionWindowStation)
    {
        if (string.IsNullOrWhiteSpace(sessionWindowStation))
        {
            throw new ArgumentException(
                "The session window station name must not be empty, because an unreadable station cannot prove that the session is interactive.",
                nameof(sessionWindowStation));
        }

        if (!sessionIsActive
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
