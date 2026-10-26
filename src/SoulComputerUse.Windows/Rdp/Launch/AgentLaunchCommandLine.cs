using System;
using System.IO;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc.Agent;

namespace SoulComputerUse.Windows.Rdp.Launch;

/// <summary>
/// Renders the alternate-shell command line that starts the child-session agent, and the bounded
/// description that may leave this layer for a log.
/// </summary>
/// <remarks>
/// <para>
/// The layout is pinned to what the agent's own command line parser accepts: the bootstrap marker
/// followed by the four contract values in a fixed order. A space belongs to the layout, so the string
/// is built from the contract tokens rather than from whatever a caller happens to pass.
/// </para>
/// <para>
/// The binding nonce is part of the command line and may exist in no other rendering. Every rejection
/// therefore names the problem without echoing a value, and <see cref="DescribeForLog"/> cannot receive
/// the nonce at all.
/// </para>
/// </remarks>
public static class AgentLaunchCommandLine
{
    /// <summary>The marker the agent requires before it accepts any other argument.</summary>
    public const string BootstrapMarker = "--scu-bootstrap";

    private const string SessionOption = "--session";
    private const string NonceOption = "--nonce";
    private const string AuthTierOption = "--auth-tier";
    private const string GateStrengthOption = "--gate-strength";

    /// <summary>The nonce text length: 128 bits rendered as hexadecimal.</summary>
    private const int NonceHexLength = AgentNonce.ByteLength * 2;

    /// <summary>
    /// Builds the complete alternate-shell command line for one launch.
    /// </summary>
    /// <param name="executablePath">The agent executable as a complete absolute path.</param>
    /// <param name="sessionId">The session UUID in <c>D</c> format.</param>
    /// <param name="nonce">The 32-character hexadecimal binding nonce.</param>
    /// <param name="authTier">The gate tier the agent must report; <see cref="AuthTier.None"/> is refused.</param>
    /// <param name="gateStrength">The gate strength the contract pins to the tier.</param>
    /// <exception cref="ArgumentException">A value cannot appear on the command line as required.</exception>
    public static string Build(
        string executablePath,
        string sessionId,
        string nonce,
        AuthTier authTier,
        GateStrength gateStrength)
    {
        string commandExecutable = FormatExecutablePath(executablePath);
        string normalizedSessionId = NormalizeSessionId(sessionId);
        EnsureNonce(nonce);
        string authTierName = AuthTierName(authTier);
        string gateStrengthName = GateStrengthName(gateStrength);
        EnsureConsistent(authTier, gateStrength);

        return $"{commandExecutable} {BootstrapMarker} {SessionOption} {normalizedSessionId} {NonceOption} {nonce} {AuthTierOption} {authTierName} {GateStrengthOption} {gateStrengthName}";
    }

    /// <summary>
    /// Renders the fixed description of a launch for a log entry: the session id, the tier and the gate
    /// strength, and nothing else. The nonce and every path are outside the value's reach.
    /// </summary>
    /// <exception cref="ArgumentException">The session id is not a <c>D</c>-format UUID.</exception>
    public static string DescribeForLog(string sessionId, AuthTier authTier, GateStrength gateStrength)
    {
        string normalizedSessionId = NormalizeSessionId(sessionId);
        return $"session={normalizedSessionId} tier={AuthTierNames.ToWireName(authTier)} strength={GateStrengthNames.ToWireName(gateStrength)}";
    }

    /// <summary>
    /// Formats the executable for the command line. The path is quoted as a whole when it contains a
    /// separator character, and the flags and values that follow are never quoted because each one has
    /// a fixed shape without separators.
    /// </summary>
    private static string FormatExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("The agent executable path must not be empty.", nameof(executablePath));
        }

        if (executablePath.Contains('"'))
        {
            throw new ArgumentException(
                "The agent executable path must not contain a double quote, because the alternate shell command line cannot carry one.",
                nameof(executablePath));
        }

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The agent executable path must be an absolute path, because the alternate shell is started without the working directory this program happens to run in.",
                nameof(executablePath));
        }

        bool needsQuotes = executablePath.IndexOfAny([' ', '\t']) >= 0;
        return needsQuotes ? $"\"{executablePath}\"" : executablePath;
    }

    /// <summary>Renders the session id in the one casing the agent reports back.</summary>
    private static string NormalizeSessionId(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (!Guid.TryParseExact(sessionId, "D", out Guid sessionGuid))
        {
            throw new ArgumentException("The session id must be a D-format UUID.", nameof(sessionId));
        }

        return sessionGuid.ToString("D");
    }

    private static void EnsureNonce(string nonce)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        if (!IsHexNonce(nonce))
        {
            // The message carries the required shape only: the nonce itself never reaches a message.
            throw new ArgumentException(
                $"The binding nonce must be {NonceHexLength} hexadecimal characters.",
                nameof(nonce));
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

    /// <summary>Renders the tier, refusing the one tier that must never start an agent.</summary>
    private static string AuthTierName(AuthTier authTier)
    {
        if (authTier == AuthTier.None)
        {
            throw new ArgumentException(
                "The 'none' tier refuses to start, so no agent command line is built for it.",
                nameof(authTier));
        }

        return AuthTierNames.ToWireName(authTier);
    }

    private static string GateStrengthName(GateStrength gateStrength)
    {
        return GateStrengthNames.ToWireName(gateStrength);
    }

    /// <summary>Refuses a strength the contract does not pin to the tier.</summary>
    private static void EnsureConsistent(AuthTier authTier, GateStrength gateStrength)
    {
        try
        {
            AuthTierRules.EnsureConsistent(authTier, gateStrength);
        }
        catch (InvalidOperationException error)
        {
            throw new ArgumentException(
                "The authentication tier and the gate strength do not belong together.",
                nameof(gateStrength),
                error);
        }
    }
}
