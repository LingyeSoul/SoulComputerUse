using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// The real session table of this machine, backed by <c>wtsapi32.dll</c>.
/// </summary>
/// <remarks>
/// <see cref="LogOff(int)"/> is the only mutating operation. It sends a logoff request with
/// <c>wait: false</c> and does not wait for the session to disappear, so a caller that needs proof has
/// to re-read the table; <see cref="LeftoverSweeper"/> does exactly that before it lets a connection
/// proceed.
/// <para>
/// The system is not called at all for a non-positive id: session zero is the service session and a
/// negative id names no session, so neither can be a child session of this product.
/// </para>
/// </remarks>
public sealed class WtsSessionTable : IWtsSessionTable
{
    private const int SessionInfoVersion = 1;

    /// <inheritdoc />
    public IReadOnlyList<WtsSessionEntry> Read()
    {
        if (!WtsSessionNative.WTSEnumerateSessionsW(
                nint.Zero,
                0,
                SessionInfoVersion,
                out nint buffer,
                out int count))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSEnumerateSessionsW failed with Win32 error {errorCode}, so the session table cannot be read.");
        }

        try
        {
            int stride = Marshal.SizeOf<WtsSessionNative.WtsSessionInfo>();
            List<WtsSessionEntry> sessions = new(count);
            for (int index = 0; index < count; index++)
            {
                WtsSessionNative.WtsSessionInfo record =
                    Marshal.PtrToStructure<WtsSessionNative.WtsSessionInfo>(buffer + (index * stride));
                sessions.Add(new WtsSessionEntry(
                    record.SessionId,
                    record.WinStationName ?? string.Empty,
                    record.State,
                    ReadAccountName(record.SessionId)));
            }

            return sessions.AsReadOnly();
        }
        finally
        {
            WtsSessionNative.WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// Reads the account name of the session this process itself runs in, which is the account the sweep
    /// compares every candidate against: a child session of this product always runs as the account that
    /// started it. The name comes from the same system answer the per-session names come from, so the
    /// comparison never mixes two different name shapes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The system refused to answer for this process's own session.</exception>
    public string? CurrentSessionAccountName()
    {
        string? accountName = ReadAccountName(WtsSessionNative.CurrentSession);
        if (accountName is null)
        {
            throw new InvalidOperationException(
                "The system did not answer the account-name query for this process's own session, so the sweep cannot prove any session to run as this account.");
        }

        return accountName;
    }

    /// <summary>
    /// Reads the account name one session is logged on as. A refused query returns null instead of
    /// throwing: the system does not answer for another account's session when this process holds
    /// ordinary rights, and the leftover policy treats an unreadable name as absent proof, which is the
    /// safe direction for a decision that ends sessions.
    /// </summary>
    private static string? ReadAccountName(int sessionId)
    {
        if (!WtsSessionNative.WTSQuerySessionInformationW(
                nint.Zero,
                sessionId,
                WtsSessionNative.WtsInfoClassAccountName,
                out nint buffer,
                out _))
        {
            return null;
        }

        try
        {
            return buffer == nint.Zero ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                WtsSessionNative.WTSFreeMemory(buffer);
            }
        }
    }

    /// <inheritdoc />
    public void LogOff(int sessionId)
    {
        if (sessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionId),
                sessionId,
                "Session zero is the service session, and a negative id names no session, so neither can be a child session. The logoff request is refused before the system is called.");
        }

        if (!WtsSessionNative.WTSLogoffSession(nint.Zero, sessionId, false))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSLogoffSession(sessionId: {sessionId}) failed with Win32 error {errorCode}, so the session may still be logged on.");
        }
    }

    /// <inheritdoc />
    public int ActiveConsoleSessionId()
    {
        uint sessionId = WtsSessionNative.WTSGetActiveConsoleSessionId();
        if (sessionId == WtsSessionNative.NoConsoleSession)
        {
            throw new InvalidOperationException(
                "WTSGetActiveConsoleSessionId reported that no session is attached to the console, so the operator's session cannot be excluded from a sweep.");
        }

        return (int)sessionId;
    }
}
