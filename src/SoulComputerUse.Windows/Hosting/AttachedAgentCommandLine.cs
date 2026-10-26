using System;

namespace SoulComputerUse.Windows.Hosting;

/// <summary>The startup values of a host that attaches to an already-running child-session agent.</summary>
public sealed record AttachedAgentOptions(string SessionId, string Nonce);

/// <summary>
/// Parses the host's attach arguments. It is a separate parser from the agent's because the host
/// must not accept the agent's gate values: it reads those from the agent's status.
/// </summary>
public static class AttachedAgentCommandLine
{
    private const string SessionOption = "--attach-agent";
    private const string NonceOption = "--agent-nonce";

    /// <summary>Parses exactly one session id and one binding nonce.</summary>
    /// <exception cref="ArgumentException">An argument is missing, unknown, duplicated or invalid.</exception>
    public static AttachedAgentOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? sessionText = null;
        string? nonce = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case SessionOption:
                    sessionText = ReadValue(args, ref index, sessionText);
                    break;
                case NonceOption:
                    nonce = ReadValue(args, ref index, nonce);
                    break;
                default:
                    throw Invalid("The host command line contains an argument the attach mode does not define.");
            }
        }

        if (sessionText is null)
        {
            throw Invalid("The host command line is missing the agent session id.");
        }

        if (!Guid.TryParseExact(sessionText, "D", out Guid sessionGuid))
        {
            throw Invalid("The agent session id must be a D-format UUID.");
        }

        if (nonce is null)
        {
            throw Invalid("The host command line is missing the agent binding nonce.");
        }

        if (!IsHexNonce(nonce))
        {
            throw Invalid("The agent binding nonce must be 32 hexadecimal characters.");
        }

        return new AttachedAgentOptions(sessionGuid.ToString("D"), nonce);
    }

    /// <summary>Renders the fixed attach usage line.</summary>
    public static string Usage()
    {
        return "Usage: Host --attach-agent <uuid> --agent-nonce <32-hex>";
    }

    private static string ReadValue(string[] args, ref int index, string? existing)
    {
        if (existing is not null)
        {
            throw Invalid("A host attach option appears more than once.");
        }

        int valueIndex = index + 1;
        if (valueIndex >= args.Length || args[valueIndex].StartsWith("--", StringComparison.Ordinal))
        {
            throw Invalid("A host attach option is missing its value.");
        }

        string value = args[valueIndex];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("A host attach option has an empty value.");
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

    private static ArgumentException Invalid(string message)
    {
        return new ArgumentException(message);
    }
}