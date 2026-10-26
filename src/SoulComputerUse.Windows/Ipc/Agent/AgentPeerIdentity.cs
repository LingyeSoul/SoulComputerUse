using System;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The peer account check of section 4 of the interface contract. The server reads the client process
/// id from its pipe handle and the client reads the server process id from its own handle; each side
/// then opens that process token and compares its user SID with the current user SID.
/// </summary>
/// <remarks>
/// The check fails closed. A process that cannot be opened, a token that cannot be read, or a SID
/// that differs is refused with <c>E_IPC_PEER</c>. The SID and the process id are deliberately never
/// placed in an error message or a log.
/// </remarks>
public static class AgentPeerIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserInformationClass = 1;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Returns the user SID of the current process token.</summary>
    /// <exception cref="InvalidOperationException">The token has no user SID.</exception>
    public static SecurityIdentifier CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new InvalidOperationException(
                "The current process token has no user SID, so the agent pipe cannot be secured.");
    }

    /// <summary>
    /// Refuses a peer whose token user SID is not the expected SID.
    /// </summary>
    /// <exception cref="Core.Protocol.SessionOperationException">The SIDs differ.</exception>
    public static void EnsureSameUser(SecurityIdentifier expected, SecurityIdentifier actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (!expected.Equals(actual))
        {
            throw AgentErrors.Peer(
                "The named pipe peer runs under a different Windows account, so the connection was refused.");
        }
    }

    /// <summary>Reads the process id of the peer that connected to a server pipe.</summary>
    /// <exception cref="Core.Protocol.SessionOperationException">The id cannot be read.</exception>
    internal static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId))
        {
            throw AgentErrors.Peer(
                "Reading the named pipe client process id failed with Win32 error "
                + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)
                + ".");
        }

        return ToProcessId(processId);
    }

    /// <summary>Reads the process id of the server that a client pipe is connected to.</summary>
    /// <exception cref="Core.Protocol.SessionOperationException">The id cannot be read.</exception>
    internal static int GetServerProcessId(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
        {
            throw AgentErrors.Peer(
                "Reading the named pipe server process id failed with Win32 error "
                + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)
                + ".");
        }

        return ToProcessId(processId);
    }

    /// <summary>Reads the user SID of a process token.</summary>
    /// <exception cref="Core.Protocol.SessionOperationException">The process or its token cannot be read.</exception>
    internal static SecurityIdentifier GetProcessUserSid(int processId)
    {
        using SafeProcessHandle process = new(
            OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId),
            ownsHandle: true);
        if (process.IsInvalid)
        {
            throw AgentErrors.Peer(
                "Opening the named pipe peer process failed with Win32 error "
                + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)
                + ".");
        }

        if (!OpenProcessToken(process, TokenQuery, out nint token))
        {
            throw AgentErrors.Peer(
                "Opening the named pipe peer process token failed with Win32 error "
                + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)
                + ".");
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
            throw AgentErrors.Peer(
                "Measuring the named pipe peer token user failed with Win32 error "
                + measurementError.ToString(CultureInfo.InvariantCulture)
                + ".");
        }

        if (required <= 0)
        {
            throw AgentErrors.Peer("The named pipe peer token reported no user information.");
        }

        nint buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!GetTokenInformation(token, TokenUserInformationClass, buffer, required, out int written)
                || written < nint.Size)
            {
                throw AgentErrors.Peer(
                    "Reading the named pipe peer token user failed with Win32 error "
                    + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)
                    + ".");
            }

            nint sidPointer = Marshal.ReadIntPtr(buffer, 0);
            if (sidPointer == 0)
            {
                throw AgentErrors.Peer("The named pipe peer token has no user SID.");
            }

            return new SecurityIdentifier(sidPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ToProcessId(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
        {
            throw AgentErrors.Peer("The named pipe peer reported an unusable process id.");
        }

        return (int)processId;
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