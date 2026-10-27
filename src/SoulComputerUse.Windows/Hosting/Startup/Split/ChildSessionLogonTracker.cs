using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>The machine's session table, as the logon tracker needs it.</summary>
public interface IWtsSessionSource
{
    /// <summary>Returns every session id on the machine, including the service session.</summary>
    IReadOnlyList<int> ReadSessionIds();

    /// <summary>Reports whether one session reports a logged-on user name.</summary>
    bool HasLoggedOnUser(int sessionId);
}

/// <summary>
/// What the logon gate may ask the machine about the child session this run created. The gate needs both
/// halves of the answer: whether a session of this run exists at all, and whether Windows already accepted
/// a logon for it. A session that exists without a user name is sitting on its own Windows logon screen,
/// which is the operator's turn; a connection that reached the connected state without any session of this
/// run is a bypass.
/// </summary>
public interface ILogonEvidence
{
    /// <summary>The session this run created, or zero while it has not appeared yet.</summary>
    int CreatedSessionId { get; }

    /// <summary>
    /// Reports whether a session this run created has appeared in the machine's session table.
    /// </summary>
    bool HasCreatedSession();

    /// <summary>
    /// Reports whether the session this run created has a logged-on user, which only happens after Windows
    /// accepted a logon.
    /// </summary>
    bool HasLoggedOnUser();
}

/// <summary>
/// Evidence for a run that never looks at the machine's session table. It answers "no session of ours"
/// and "nobody logged on", which is the strict answer: a caller that supplies it keeps the logon-screen
/// chain as the only accepted proof.
/// </summary>
public sealed class NoLogonEvidence : ILogonEvidence
{
    /// <inheritdoc />
    public int CreatedSessionId => 0;

    /// <inheritdoc />
    public bool HasCreatedSession()
    {
        return false;
    }

    /// <inheritdoc />
    public bool HasLoggedOnUser()
    {
        return false;
    }
}

/// <summary>Reads the session table and each session's user name through Terminal Services.</summary>
/// <remarks>
/// A user name is only ever compared against empty and discarded: this type answers a question about a
/// session, never a value, so no account name can reach a log, a message or a record.
/// </remarks>
public sealed class WtsSessionSource : IWtsSessionSource
{
    private const int WtsInfoClassUserName = 5;

    /// <inheritdoc />
    /// <exception cref="Win32Exception">The session table could not be read.</exception>
    public IReadOnlyList<int> ReadSessionIds()
    {
        if (!WTSEnumerateSessionsW(0, 0, 1, out nint buffer, out int count))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSEnumerateSessions failed with Win32 error {errorCode}, so the session table cannot be read.");
        }

        try
        {
            List<int> sessionIds = new(count);
            int stride = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < count; index++)
            {
                WtsSessionInfo record = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (index * stride));
                sessionIds.Add(record.SessionId);
            }

            return sessionIds;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The session id is negative.</exception>
    /// <exception cref="Win32Exception">The session could not be queried.</exception>
    public bool HasLoggedOnUser(int sessionId)
    {
        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId), sessionId, "A Windows session id cannot be negative.");
        }

        if (!WTSQuerySessionInformationW(0, sessionId, WtsInfoClassUserName, out nint buffer, out int byteCount))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSQuerySessionInformation failed with Win32 error {errorCode} for one session, so its logon state cannot be read.");
        }

        try
        {
            if (byteCount <= 0)
            {
                return false;
            }

            string? userName = Marshal.PtrToStringUni(buffer);
            return !string.IsNullOrEmpty(userName);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // The API returns exactly these three fields; a record that declares more shifts the stride and every
    // later entry is read from the wrong offset.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        internal int SessionId;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string? WinStationName;

        internal int State;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(nint server, int reserved, int version, out nint sessions, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out int byteCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}

/// <summary>
/// Finds the session the connection created and answers whether its operator has logged on.
/// </summary>
/// <remarks>
/// <para>
/// A client that asks the operator for credentials itself authenticates before the session paints its own
/// logon screen: the connected state then goes from connecting straight to connected, and the logon screen
/// the gate looks for never appears. The remaining proof is the session's own user name, which Windows only
/// reports after it accepted a credential.
/// </para>
/// <para>
/// The session is identified by comparing the session table against a snapshot taken before the connection:
/// the one id that was not there before is the session this run created, so a leftover session or another
/// account's session can never answer the question. Every read failure answers "not logged on" rather than
/// "logged on", because a failed probe must never admit a session nobody authenticated for.
/// </para>
/// </remarks>
public sealed class ChildSessionLogonTracker : ILogonEvidence
{
    private readonly IWtsSessionSource _source;
    private readonly HashSet<int> _knownSessionIds;
    private readonly int _consoleSessionId;
    private int _trackedSessionId;

    private ChildSessionLogonTracker(
        IWtsSessionSource source,
        HashSet<int> knownSessionIds,
        int consoleSessionId)
    {
        _source = source;
        _knownSessionIds = knownSessionIds;
        _consoleSessionId = consoleSessionId;
    }

    /// <summary>The session this run created, or zero while it has not appeared yet.</summary>
    public int CreatedSessionId
    {
        get
        {
            // A caller outside the wait still has to be able to ask: the shutdown path needs the id of a
            // session that never reached a logon, and the wait may have ended before the next look.
            _ = HasCreatedSession();
            return _trackedSessionId;
        }
    }

    /// <inheritdoc />
    public bool HasCreatedSession()
    {
        return TryFindCreatedSession();
    }

    /// <summary>
    /// Takes a snapshot of the sessions that exist now, so the next connection's session can be told apart
    /// from everything that was already there.
    /// </summary>
    /// <param name="source">The session table to read.</param>
    /// <param name="consoleSessionId">The operator's console session, which is never ours.</param>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The console session id is negative.</exception>
    /// <exception cref="Win32Exception">The snapshot could not be read, so no session can be identified.</exception>
    public static ChildSessionLogonTracker Start(IWtsSessionSource source, int consoleSessionId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (consoleSessionId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consoleSessionId),
                consoleSessionId,
                "A Windows session id cannot be negative.");
        }

        HashSet<int> known = [];
        foreach (int sessionId in source.ReadSessionIds())
        {
            known.Add(sessionId);
        }

        return new ChildSessionLogonTracker(source, known, consoleSessionId);
    }

    /// <summary>
    /// Reports whether the session this run created has a logged-on user. A session that has not appeared
    /// yet, or a table that cannot be read, answers false.
    /// </summary>
    public bool HasLoggedOnUser()
    {
        if (!TryFindCreatedSession())
        {
            return false;
        }

        try
        {
            return _source.HasLoggedOnUser(_trackedSessionId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A probe that cannot answer must not be read as "someone logged on": the gate then relies on
            // the logon screen alone, which is the stricter of the two proofs.
            return false;
        }
    }

    private bool TryFindCreatedSession()
    {
        if (_trackedSessionId != 0)
        {
            return true;
        }

        IReadOnlyList<int> current;
        try
        {
            current = _source.ReadSessionIds();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }

        foreach (int sessionId in current)
        {
            if (sessionId <= 0 || sessionId == _consoleSessionId || _knownSessionIds.Contains(sessionId))
            {
                continue;
            }

            _trackedSessionId = sessionId;
            return true;
        }

        return false;
    }
}