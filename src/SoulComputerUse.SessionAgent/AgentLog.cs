using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// Writes one structured agent log line to standard error. The field set is the contract's log
/// whitelist: no nonce, typed text, screenshot bytes, account names or paths ever go in here.
/// </summary>
public static class AgentLog
{
    /// <summary>Writes one event with a severity level.</summary>
    public static void Write(string level, string eventName)
    {
        AgentLogLine line = new(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            level,
            eventName,
            Environment.ProcessId);
        Console.Error.WriteLine(JsonSerializer.Serialize(line));
    }
}

/// <summary>One structured log line.</summary>
public sealed record AgentLogLine(
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("event")] string EventName,
    [property: JsonPropertyName("pid")] int ProcessId);