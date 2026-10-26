using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The immutable startup configuration of one child-session agent. The nonce is the one-time binding
/// value from the contract and must never be written to a log or echoed in an error.
/// </summary>
public sealed record AgentOptions(
    string SessionId,
    string Nonce,
    AuthTier AuthTier,
    GateStrength GateStrength);

/// <summary>
/// Parses the agent's startup command line. The parser is strict: it accepts exactly the bootstrap
/// marker and the four contract values, rejects duplicates and unknown arguments, and never echoes a
/// rejected nonce.
/// </summary>
public static class AgentCommandLine
{
    private const string BootstrapFlag = "--scu-bootstrap";
    private const string SessionOption = "--session";
    private const string NonceOption = "--nonce";
    private const string AuthTierOption = "--auth-tier";
    private const string GateStrengthOption = "--gate-strength";

    /// <summary>Parses one agent command line.</summary>
    /// <exception cref="ArgumentException">A required value is missing, unknown, duplicated or invalid.</exception>
    public static AgentOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool bootstrap = false;
        string? sessionText = null;
        string? nonce = null;
        string? authTierText = null;
        string? gateStrengthText = null;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case BootstrapFlag:
                    if (bootstrap)
                    {
                        throw Invalid("The bootstrap marker appears more than once.");
                    }

                    bootstrap = true;
                    break;

                case SessionOption:
                    sessionText = ReadValue(args, ref index, SessionOption, sessionText);
                    break;

                case NonceOption:
                    nonce = ReadValue(args, ref index, NonceOption, nonce);
                    break;

                case AuthTierOption:
                    authTierText = ReadValue(args, ref index, AuthTierOption, authTierText);
                    break;

                case GateStrengthOption:
                    gateStrengthText = ReadValue(args, ref index, GateStrengthOption, gateStrengthText);
                    break;

                default:
                    throw Invalid("The agent command line contains an argument the contract does not define.");
            }
        }

        if (!bootstrap)
        {
            throw Invalid("The agent must be launched with the bootstrap marker.");
        }

        if (sessionText is null)
        {
            throw Invalid("The agent command line is missing the session id.");
        }

        if (!Guid.TryParseExact(sessionText, "D", out Guid sessionGuid))
        {
            throw Invalid("The session id must be a D-format UUID.");
        }

        if (nonce is null)
        {
            throw Invalid("The agent command line is missing the binding nonce.");
        }

        if (!IsHexNonce(nonce))
        {
            throw Invalid("The binding nonce must be 32 hexadecimal characters.");
        }

        if (authTierText is null)
        {
            throw Invalid("The agent command line is missing the authentication tier.");
        }

        if (gateStrengthText is null)
        {
            throw Invalid("The agent command line is missing the gate strength.");
        }

        AuthTier authTier = ParseAuthTier(authTierText);
        GateStrength gateStrength = ParseGateStrength(gateStrengthText);
        try
        {
            AuthTierRules.EnsureConsistent(authTier, gateStrength);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw Invalid("The authentication tier and gate strength do not belong together.");
        }

        return new AgentOptions(sessionGuid.ToString("D"), nonce, authTier, gateStrength);
    }

    /// <summary>Renders the fixed usage line. It carries no values from the command line.</summary>
    public static string Usage()
    {
        return "Usage: agent --scu-bootstrap --session <uuid> --nonce <32-hex> --auth-tier <hello-enrolled|system-credential> --gate-strength <strong|login>";
    }

    private static string ReadValue(string[] args, ref int index, string option, string? existing)
    {
        if (existing is not null)
        {
            throw Invalid("An agent option appears more than once.");
        }

        int valueIndex = index + 1;
        if (valueIndex >= args.Length || args[valueIndex].StartsWith("--", StringComparison.Ordinal))
        {
            throw Invalid("An agent option is missing its value.");
        }

        string value = args[valueIndex];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("An agent option has an empty value.");
        }

        index = valueIndex;
        return value;
    }

    private static bool IsHexNonce(string nonce)
    {
        if (nonce.Length != 32)
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

    private static AuthTier ParseAuthTier(string text)
    {
        try
        {
            AuthTier tier = AuthTierNames.FromWireName(text);
            if (tier == AuthTier.None)
            {
                throw Invalid("The authentication tier 'none' must never start an agent.");
            }

            return tier;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Invalid("The authentication tier is not one of the contract values.");
        }
    }

    private static GateStrength ParseGateStrength(string text)
    {
        try
        {
            return GateStrengthNames.FromWireName(text);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Invalid("The gate strength is not one of the contract values.");
        }
    }

    private static ArgumentException Invalid(string message)
    {
        return new ArgumentException(message);
    }
}