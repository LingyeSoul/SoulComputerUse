using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace SoulComputerUse.Core.Session;

/// <summary>
/// The wire names of <see cref="SessionErrorCode"/> from section 2 of the interface contract. The
/// names are pinned because IPC, MCP and CLI payloads carry them, so they are defined once here.
/// </summary>
public static class SessionErrorCodeNames
{
    private static readonly FrozenDictionary<SessionErrorCode, string> _wireNamesByCode = CreateWireNamesByCode();
    private static readonly FrozenDictionary<string, SessionErrorCode> _codesByWireName = CreateCodesByWireName(_wireNamesByCode);

    /// <summary>Returns the contract wire name of a session error code.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined <see cref="SessionErrorCode"/>.</exception>
    public static string ToWireName(SessionErrorCode code)
    {
        if (!_wireNamesByCode.TryGetValue(code, out string? wireName))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown session error code.");
        }

        return wireName;
    }

    /// <summary>Parses a contract wire name back into a session error code.</summary>
    /// <exception cref="ArgumentNullException">The wire name is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The wire name is not one of the contract names.</exception>
    public static SessionErrorCode FromWireName(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);

        if (!_codesByWireName.TryGetValue(wireName, out SessionErrorCode code))
        {
            throw new ArgumentOutOfRangeException(nameof(wireName), wireName, "Unknown session error code wire name.");
        }

        return code;
    }

    private static FrozenDictionary<SessionErrorCode, string> CreateWireNamesByCode()
    {
        Dictionary<SessionErrorCode, string> wireNames = new()
        {
            [SessionErrorCode.LockHeld] = "E_LOCK_HELD",
            [SessionErrorCode.LockStaleRecord] = "E_LOCK_STALE_RECORD",
            [SessionErrorCode.LockForeignHandle] = "E_LOCK_FOREIGN_HANDLE",
            [SessionErrorCode.AuthRequired] = "E_AUTH_REQUIRED",
            [SessionErrorCode.ManualIntervention] = "E_MANUAL_INTERVENTION",
            [SessionErrorCode.ChildSessionsDisabled] = "E_CHILD_SESSIONS_DISABLED",
            [SessionErrorCode.ChildSessionsDeclined] = "E_CHILD_SESSIONS_DECLINED",
            [SessionErrorCode.ElevationUnavailable] = "E_ELEVATION_UNAVAILABLE",
            [SessionErrorCode.ChildConnectionsDenied] = "E_CHILD_CONNECTIONS_DENIED",
            [SessionErrorCode.RdpInboundDenied] = "E_RDP_INBOUND_DENIED",
            [SessionErrorCode.State] = "E_STATE",
            [SessionErrorCode.NotReady] = "E_NOT_READY",
            [SessionErrorCode.AwaitingCredentials] = "E_AWAITING_CREDENTIALS",
            [SessionErrorCode.Disconnected] = "E_DISCONNECTED",
            [SessionErrorCode.Blocked] = "E_BLOCKED",
            [SessionErrorCode.Closing] = "E_CLOSING",
            [SessionErrorCode.AgentTimeout] = "E_AGENT_TIMEOUT",
            [SessionErrorCode.AgentDead] = "E_AGENT_DEAD",
            [SessionErrorCode.AgentWrongSession] = "E_AGENT_WRONG_SESSION",
            [SessionErrorCode.AgentUnclaimed] = "E_AGENT_UNCLAIMED",
            [SessionErrorCode.IpcProtocol] = "E_IPC_PROTOCOL",
            [SessionErrorCode.IpcPeer] = "E_IPC_PEER",
            [SessionErrorCode.InputInvalid] = "E_INPUT_INVALID",
            [SessionErrorCode.CoordOutOfRange] = "E_COORD_OUT_OF_RANGE",
            [SessionErrorCode.ScreenshotTooLarge] = "E_SCREENSHOT_TOO_LARGE",
            [SessionErrorCode.AgentError] = "E_AGENT_ERROR",
            [SessionErrorCode.HostLaunchFailed] = "E_HOST_LAUNCH_FAILED",
        };

        if (wireNames.Count != Enum.GetValues<SessionErrorCode>().Length)
        {
            throw new InvalidOperationException(
                "The session error wire names must cover every session error code exactly once.");
        }

        return wireNames.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, SessionErrorCode> CreateCodesByWireName(
        FrozenDictionary<SessionErrorCode, string> wireNamesByCode)
    {
        Dictionary<string, SessionErrorCode> codes = new(StringComparer.Ordinal);
        foreach (KeyValuePair<SessionErrorCode, string> pair in wireNamesByCode)
        {
            if (!codes.TryAdd(pair.Value, pair.Key))
            {
                throw new InvalidOperationException(
                    $"The session error wire name '{pair.Value}' is mapped more than once.");
            }
        }

        return codes.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
