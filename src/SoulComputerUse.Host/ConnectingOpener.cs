using System;
using System.Threading;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Rdp.SessionLaunch;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.Host;

/// <summary>
/// The connection step of the new-session mode: it opens the child session through the single entry point
/// and then waits, while pumping messages, until the operator has logged on.
/// </summary>
/// <remarks>
/// <para>
/// The wait belongs to the same phase boundary as the connection, because the contract has one thing
/// between "before connect" and "after logon": the window that shows the Windows logon screen. Composing
/// the two here keeps <see cref="SessionStartPhaseRunner"/> unchanged and makes the phase order impossible
/// to get wrong.
/// </para>
/// <para>
/// The wait pumps window messages instead of sleeping, so the remote desktop control keeps painting the
/// logon screen the operator is being asked to use. A plain sleep would freeze that window.
/// </para>
/// </remarks>
internal sealed class ConnectingOpener : ISessionConnectionOpener
{
    private readonly ISessionConnectionOpener _opener;
    private readonly SessionConnectionWaiter _waiter;

    internal ConnectingOpener(ISessionConnectionOpener opener, SessionConnectionWaiter waiter)
    {
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(waiter);
        _opener = opener;
        _waiter = waiter;
    }

    /// <inheritdoc />
    public void Open()
    {
        _opener.Open();
        _waiter.Await();
    }
}

/// <summary>
/// Reports each phase boundary on the product window, so the caption says what the session is doing while
/// the remote window shows the logon screen or the desktop.
/// </summary>
/// <remarks>
/// The tier is only known once the before-connect segment has probed it, and the contract's caption needs
/// it for the <c>awaiting-credentials</c> text. A missing tier therefore raises instead of borrowing one:
/// the phase runner records an observer failure and the session still runs, which is better than painting
/// a gate claim the machine never made.
/// </remarks>
internal sealed class PhaseStatusObserver : ISessionPhaseObserver
{
    private readonly Func<ProductWindow?> _window;
    private readonly Func<AuthTier?> _authTier;
    private readonly bool _trace;

    internal PhaseStatusObserver(Func<ProductWindow?> window, Func<AuthTier?> authTier)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(authTier);
        _window = window;
        _authTier = authTier;
        _trace = Environment.GetEnvironmentVariable("SOULCOMPUTERUSE_DIAGNOSTICS") == "1";
    }

    /// <inheritdoc />
    public void OnPhaseChanged(SessionStartPhase phase)
    {
        ProductWindow? window = _window();
        if (window is null)
        {
            return;
        }

        if (_trace)
        {
            // The caption is the only signal the operator has while the child session is being brought
            // up, so a run that appears stuck needs to say which boundary it reached and when.
            Console.Error.WriteLine(
                "trace phase=" + SessionStartPhaseNames.ToWireName(phase)
                + " at=" + DateTime.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
        }

        window.SetStatus(Describe(phase));
        window.RepaintNow();
    }

    private SessionStatusVisual Describe(SessionStartPhase phase)
    {
        SessionState state = phase switch
        {
            SessionStartPhase.BeforeConnect => SessionState.Starting,
            SessionStartPhase.AwaitingCredentials => SessionState.AwaitingCredentials,
            SessionStartPhase.AfterLogon => SessionState.Connecting,
            SessionStartPhase.Ready => SessionState.Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown session start phase."),
        };

        AuthTier tier = _authTier()
            ?? throw new InvalidOperationException(
                "The caption was asked about a phase before the credential tier was probed, so it cannot name the gate the session uses.");

        return SessionStatusVisuals.Describe(state, tier, AuthTierRules.StrengthFor(tier), blockedCause: null);
    }
}