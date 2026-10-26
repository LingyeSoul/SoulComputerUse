using System;
using System.Security.Cryptography;
using System.Text;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The one-time binding value the host places on the agent's startup command line and sends in the
/// <c>hello</c> event.
/// </summary>
/// <remarks>
/// The nonce binds one host connection to one agent launch. It is not a secret and it provides no
/// confidentiality: while the startup window is open, another process under the same account can
/// read the command line. The explicit pipe DACL and the peer process SID check are the primary
/// authentication. The nonce is never written to a log or an error message.
/// </remarks>
public static class AgentNonce
{
    /// <summary>The number of random bytes in a nonce, matching the contract's 128 bits.</summary>
    public const int ByteLength = 16;

    /// <summary>Creates one random nonce as upper-case hexadecimal text.</summary>
    public static string Create()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(ByteLength);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Compares two nonces without an early exit. The comparison is constant time for equal-length
    /// values; a different length is already outside the nonce format and returns false.
    /// </summary>
    public static bool Matches(string expected, string actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (actual is null)
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}