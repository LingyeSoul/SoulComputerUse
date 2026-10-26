using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Writes the adapter's diagnostic lines to stderr. Only the contract's log whitelist is used:
/// the method name, the duration and the error code, never typed text, image data, accounts or paths.
/// </summary>
internal static class McpLog
{
    /// <summary>Writes one tool call outcome.</summary>
    internal static void ToolCall(
        TextWriter diagnostics,
        string method,
        long durationMs,
        SessionErrorCode? code,
        SessionState? state)
    {
        Write(diagnostics, "information", "mcp.tool_call", method, durationMs, code, state);
    }

    /// <summary>Writes one request the server refused before or outside a tool call.</summary>
    internal static void RequestFailed(TextWriter diagnostics, string? method, long durationMs, string level)
    {
        Write(diagnostics, level, "mcp.request_failed", method, durationMs, null, null);
    }

    private static void Write(
        TextWriter diagnostics,
        string level,
        string eventName,
        string? method,
        long durationMs,
        SessionErrorCode? code,
        SessionState? state)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(eventName);

        string line = JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("ts", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("level", level);
            writer.WriteString("event", eventName);
            if (method is not null)
            {
                writer.WriteString("method", method);
            }

            writer.WriteNumber("durationMs", durationMs);
            if (code is not null)
            {
                writer.WriteString("code", SessionErrorCodeNames.ToWireName(code.Value));
            }

            if (state is not null)
            {
                writer.WriteString("state", SessionStateNames.ToWireName(state.Value));
            }

            writer.WriteEndObject();
        });

        diagnostics.WriteLine(line);
        diagnostics.Flush();
    }
}
