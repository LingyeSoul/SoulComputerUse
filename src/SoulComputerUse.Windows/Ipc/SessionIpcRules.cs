using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Ipc;

/// <summary>
/// The pure decision rules of the owner pipe: which tool a method name means, how long a call may
/// take, and how a wire error becomes the client's exception. Nothing here touches a pipe.
/// </summary>
public static class SessionIpcRules
{
    /// <summary>The contract's request timeout for action tools (mouse, keyboard).</summary>
    public static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The contract's request timeout for <c>screenshot</c>, which is allowed longer.</summary>
    public static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The contract's limit on encoded PNG bytes, counted before base64 inflation.</summary>
    public const int ScreenshotByteLimit = 5_242_880;

    /// <summary>Returns the timeout the contract pins to one tool.</summary>
    public static TimeSpan TimeoutFor(SessionTool tool)
    {
        return tool == SessionTool.Screenshot ? ScreenshotTimeout : ActionTimeout;
    }

    /// <summary>
    /// Resolves a method name to a tool. An unknown name is reported as unresolved instead of being
    /// guessed, so the caller can answer with an explicit error.
    /// </summary>
    public static bool TryResolveMethod(string wireName, out SessionTool tool)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        try
        {
            tool = SessionToolNames.FromWireName(wireName);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            tool = default;
            return false;
        }
    }

    /// <summary>
    /// Converts one wire error into the exception the CLI and MCP adapters share. A code outside the
    /// contract is refused instead of being mapped onto the nearest known code.
    /// </summary>
    /// <exception cref="IpcProtocolException">The code is not one of the contract's error codes.</exception>
    public static SessionOperationException ToOperationException(IpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!TryResolveErrorCode(error.Code, out SessionErrorCode code))
        {
            throw new IpcProtocolException("The IPC error carries code '" + error.Code + "', which is not a contract error code.");
        }

        return new SessionOperationException(code, error.Message, error.Retryable);
    }

    /// <summary>Resolves a contract error code name without guessing an unknown one.</summary>
    public static bool TryResolveErrorCode(string wireName, out SessionErrorCode code)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        try
        {
            code = SessionErrorCodeNames.FromWireName(wireName);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            code = default;
            return false;
        }
    }

    /// <summary>Creates the exception for a request that exceeded its contract timeout.</summary>
    public static SessionOperationException Timeout(SessionTool tool)
    {
        return new SessionOperationException(
            SessionErrorCode.AgentTimeout,
            "The owner did not answer '" + SessionToolNames.ToWireName(tool) + "' within its contract timeout, so the call was abandoned.",
            SessionErrorCatalog.IsRetryable(SessionErrorCode.AgentTimeout));
    }

    /// <summary>Creates the exception for a pipe that is not connected, or was lost.</summary>
    /// <param name="pipeName">The pipe the client tried to use; a pipe name is not machine data.</param>
    public static SessionOperationException Disconnected(string pipeName)
    {
        return new SessionOperationException(
            SessionErrorCode.Disconnected,
            "No session owner answered on the pipe '" + pipeName + "', so the call was not sent.",
            false);
    }

    /// <summary>Creates the exception for a frame the peer sent that the contract does not allow.</summary>
    public static SessionOperationException Protocol(string message)
    {
        return new SessionOperationException(SessionErrorCode.IpcProtocol, message, false);
    }

    /// <summary>The tool names in the contract order, used by the handlers and by the tests.</summary>
    public static IReadOnlyList<SessionTool> Tools => SessionToolCatalog.Tools;
}
