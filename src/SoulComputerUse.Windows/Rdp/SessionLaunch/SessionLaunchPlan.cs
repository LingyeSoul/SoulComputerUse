using System;
using SoulComputerUse.Windows.Rdp.Launch;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The immutable inputs of one hosted session launch, validated before anything is written to the
/// control or to the machine, together with the alternate-shell request derived from them.
/// </summary>
/// <remarks>
/// <para>
/// The plan is a pure decision. Its constructor validates the values and refuses a value that could
/// not produce a usable connection; <see cref="CreateAgentLaunchRequest"/> derives the request from the
/// application directory alone. Nothing here touches the control, the lock or the machine.
/// </para>
/// <para>
/// The request declares no credential tier and no gate strength. The agent detects the tier inside the
/// child session for itself, and a declaration that the machine contradicts would stop the agent at
/// startup.
/// </para>
/// <para>
/// A rejection names the field and the required shape only. The binding nonce and the application
/// directory never appear in a message, because a message can reach a console, a screenshot or an
/// issue.
/// </para>
/// </remarks>
public sealed record SessionLaunchPlan
{
    /// <summary>The largest desktop side a connection may request.</summary>
    public const int MaximumDesktopSide = 8192;

    /// <summary>The number of hexadecimal characters in the binding nonce.</summary>
    private const int NonceHexLength = 32;

    /// <summary>Validates one launch and refuses values that cannot describe a usable session.</summary>
    /// <exception cref="ArgumentException">
    /// The session id is not a <c>D</c>-format UUID, the nonce is not 32 hexadecimal characters, the
    /// application directory or the session label is empty, or the launch is not a child-session launch.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A desktop side is not positive or exceeds <see cref="MaximumDesktopSide"/>.
    /// </exception>
    public SessionLaunchPlan(
        string sessionId,
        string nonce,
        string applicationDirectory,
        int desktopWidth,
        int desktopHeight,
        bool connectToChildSession,
        string sessionLabel)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !Guid.TryParseExact(sessionId, "D", out Guid sessionGuid))
        {
            throw new ArgumentException("The session id must be a D-format UUID.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(nonce) || !IsHexNonce(nonce))
        {
            // The required shape is the whole message: the nonce itself must never be echoed.
            throw new ArgumentException("The binding nonce must be 32 hexadecimal characters.", nameof(nonce));
        }

        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            throw new ArgumentException("The application directory must not be empty.", nameof(applicationDirectory));
        }

        RequireDesktopSide(desktopWidth, nameof(desktopWidth), "width");
        RequireDesktopSide(desktopHeight, nameof(desktopHeight), "height");

        if (!connectToChildSession)
        {
            throw new ArgumentException(
                "Only a child-session connection may be launched, because any other connection would take over the operator's own session.",
                nameof(connectToChildSession));
        }

        if (string.IsNullOrWhiteSpace(sessionLabel))
        {
            throw new ArgumentException("The session label must not be empty.", nameof(sessionLabel));
        }

        SessionId = sessionGuid.ToString("D");
        Nonce = nonce;
        ApplicationDirectory = applicationDirectory;
        DesktopWidth = desktopWidth;
        DesktopHeight = desktopHeight;
        ConnectToChildSession = connectToChildSession;
        SessionLabel = sessionLabel.Trim();
    }

    /// <summary>The UUIDv4 session id shared by the ownership record, the agent pipe and the owner pipe.</summary>
    public string SessionId { get; }

    /// <summary>The one-time binding nonce carried by the alternate-shell command line.</summary>
    public string Nonce { get; }

    /// <summary>The directory the agent executable is derived from.</summary>
    public string ApplicationDirectory { get; }

    /// <summary>The remote desktop width to request, in the session's native pixels.</summary>
    public int DesktopWidth { get; }

    /// <summary>The remote desktop height to request, in the session's native pixels.</summary>
    public int DesktopHeight { get; }

    /// <summary>Whether the connection lands in a child session. The product launches no other kind.</summary>
    public bool ConnectToChildSession { get; }

    /// <summary>The placeholder label the product window title uses, never a real session identifier.</summary>
    public string SessionLabel { get; }

    /// <summary>
    /// Derives the alternate-shell request from the application directory: the agent installed beside
    /// this program, started in that same directory, with no declared tier.
    /// </summary>
    /// <exception cref="FileNotFoundException">The agent executable is missing from that directory.</exception>
    public AgentLaunchRequest CreateAgentLaunchRequest()
    {
        return AgentLaunchRequest.ForApplicationDirectory(
            ApplicationDirectory,
            SessionId,
            Nonce,
            authTier: null,
            gateStrength: null);
    }

    /// <summary>
    /// Renders the plan without the binding nonce and without the application directory, so an
    /// accidental interpolation into a log line or a message cannot leak either.
    /// </summary>
    public override string ToString()
    {
        return $"session={SessionId} size={DesktopWidth}x{DesktopHeight} childSession={ConnectToChildSession}";
    }

    private static void RequireDesktopSide(int value, string parameterName, string side)
    {
        if (value <= 0 || value > MaximumDesktopSide)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The desktop {side} must be positive and at most {MaximumDesktopSide}.");
        }
    }

    private static bool IsHexNonce(string nonce)
    {
        if (nonce.Length != NonceHexLength)
        {
            return false;
        }

        foreach (char character in nonce)
        {
            bool isHex = character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
