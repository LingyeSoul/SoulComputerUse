using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// Writes one structured agent log line to standard error, and keeps a copy in a per-user file.
/// </summary>
/// <remarks>
/// In production the agent is the child session's alternate shell, so its standard error has no reader: a
/// failure there is invisible, and the host can only report the contract code it observed. The file copy is
/// therefore not an optional debugging aid but the agent's only durable record. It carries the same
/// whitelisted fields as the standard-error line, so it can be attached to a report without a redaction
/// pass.
/// </remarks>
public static class AgentLog
{
    /// <summary>The file the durable copy is appended to.</summary>
    public const string DiagnosticLogPath = @"%LOCALAPPDATA%\SoulComputerUse\agent-diagnostics.log";

    /// <summary>Writes one event with a severity level.</summary>
    public static void Write(string level, string eventName)
    {
        AgentLogLine line = new(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            level,
            eventName,
            Environment.ProcessId);
        string rendered = JsonSerializer.Serialize(line);
        Console.Error.WriteLine(rendered);

        try
        {
            string path = Environment.ExpandEnvironmentVariables(DiagnosticLogPath);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, rendered + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A record that cannot be written must not change the agent's behaviour: the standard-error
            // line above is still emitted, and a failed write is not a reason to stop serving a session.
        }
    }
}

/// <summary>One structured log line.</summary>
public sealed record AgentLogLine(
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("event")] string EventName,
    [property: JsonPropertyName("pid")] int ProcessId);