using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Ipc.Security;

/// <summary>
/// The peer account check of the owner pipe. The server reads the client process id from its pipe
/// handle and the client reads the server process id from its own handle; each side then opens that
/// process token and compares its user SID with the current user SID.
/// </summary>
/// <remarks>
/// The check proves only that both ends run under the same Windows account. It does not prove that the
/// peer is this product's owner process: another process of the same account can still open the pipe
/// and answer, so the owner SID model has that known boundary and this type must not claim more.
/// The check fails closed. A process that cannot be opened, a token that cannot be read, or a SID that
/// differs is refused with <c>E_IPC_PEER</c>. A SID, an account name, a process id and a path are
/// deliberately never placed in an error message.
/// </remarks>
public static class OwnerPipePeer
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserInformationClass = 1;
    private const int ErrorInsufficientBuffer = 122;

    private const string DifferentAccountMessage =
        "The named pipe peer runs under a different Windows account, so the connection was refused.";

    /// <summary>Returns the user SID of the current process token.</summary>
    /// <exception cref="InvalidOperationException">The token has no user SID.</exception>
    public static SecurityIdentifier CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new InvalidOperationException(
                "The current process token has no user SID, so the owner pipe cannot be secured.");
    }

    /// <summary>Refuses a peer whose token user SID is not the expected SID.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="SessionOperationException">The SIDs differ; the code is <c>E_IPC_PEER</c>.</exception>
    public static void EnsureSameUser(SecurityIdentifier expected, SecurityIdentifier actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (!expected.Equals(actual))
        {
            throw Peer(DifferentAccountMessage);
        }
    }

    /// <summary>
    /// Verifies that the client connected to a server pipe runs as the current user.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pipe"/> is null.</exception>
    /// <exception cref="SessionOperationException">The peer cannot be read or runs under another account.</exception>
    internal static void EnsureClientIsSameUser(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        int processId = GetClientProcessId(pipe);
        EnsureSameUser(ExpectedUserSid(), GetProcessUserSid(processId));
    }

    /// <summary>
    /// Verifies that the server a client pipe is connected to runs as the current user.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pipe"/> is null.</exception>
    /// <exception cref="SessionOperationException">The peer cannot be read or runs under another account.</exception>
    internal static void EnsureServerIsSameUser(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        int processId = GetServerProcessId(pipe);
        EnsureSameUser(ExpectedUserSid(), GetProcessUserSid(processId));
    }

    /// <summary>Reads the process id of the client that connected to a server pipe.</summary>
    /// <exception cref="SessionOperationException">The id cannot be read; the code is <c>E_IPC_PEER</c>.</exception>
    internal static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId))
        {
            throw Peer("The named pipe client process id could not be read, so the connection was refused.");
        }

        return ToProcessId(processId);
    }

    /// <summary>Reads the process id of the server that a client pipe is connected to.</summary>
    /// <exception cref="SessionOperationException">The id cannot be read; the code is <c>E_IPC_PEER</c>.</exception>
    internal static int GetServerProcessId(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
        {
            throw Peer("The named pipe server process id could not be read, so the connection was refused.");
        }

        return ToProcessId(processId);
    }

    /// <summary>Reads the user SID of a process token.</summary>
    /// <exception cref="SessionOperationException">The process or its token cannot be read; the code is <c>E_IPC_PEER</c>.</exception>
    internal static SecurityIdentifier GetProcessUserSid(int processId)
    {
        using SafeProcessHandle process = new(
            OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId),
            ownsHandle: true);
        if (process.IsInvalid)
        {
            throw Peer("The named pipe peer process could not be opened to read its Windows account, so the connection was refused.");
        }

        if (!OpenProcessToken(process, TokenQuery, out nint token))
        {
            throw Peer("The named pipe peer process token could not be opened to read its Windows account, so the connection was refused.");
        }

        using SafeAccessTokenHandle tokenHandle = new(token);
        return ReadTokenUser(tokenHandle);
    }

    private static SecurityIdentifier ReadTokenUser(SafeAccessTokenHandle token)
    {
        bool measured = GetTokenInformation(token, TokenUserInformationClass, 0, 0, out int required);
        int measurementError = Marshal.GetLastWin32Error();
        if (!measured && measurementError != ErrorInsufficientBuffer)
        {
            throw Peer("The named pipe peer token user could not be read, so the connection was refused.");
        }

        if (required <= 0)
        {
            throw Peer("The named pipe peer token reported no user information, so the connection was refused.");
        }

        nint buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!GetTokenInformation(token, TokenUserInformationClass, buffer, required, out int written)
                || written < nint.Size)
            {
                throw Peer("The named pipe peer token user could not be read, so the connection was refused.");
            }

            nint sidPointer = Marshal.ReadIntPtr(buffer, 0);
            if (sidPointer == 0)
            {
                throw Peer("The named pipe peer token has no user SID, so the connection was refused.");
            }

            return new SecurityIdentifier(sidPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SecurityIdentifier ExpectedUserSid()
    {
        try
        {
            return CurrentUserSid();
        }
        catch (InvalidOperationException)
        {
            throw Peer("The current process token user could not be read, so the named pipe peer could not be verified.");
        }
    }

    private static int ToProcessId(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
        {
            throw Peer("The named pipe peer reported an unusable process id, so the connection was refused.");
        }

        return (int)processId;
    }

    private static SessionOperationException Peer(string message)
    {
        return new SessionOperationException(SessionErrorCode.IpcPeer, message, false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
