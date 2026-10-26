using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace SoulComputerUse.Windows.Logging;

/// <summary>
/// The severity of a structured log line. The values are ordered from the most to the least detailed
/// level, matching the usual diagnostic order.
/// </summary>
public enum LogLevel
{
    /// <summary>Diagnostic detail that is only useful while investigating a problem.</summary>
    Debug = 0,

    /// <summary>A normal lifecycle step.</summary>
    Information = 1,

    /// <summary>Something was refused or recovered, but the session continues.</summary>
    Warning = 2,

    /// <summary>An operation failed.</summary>
    Error = 3,

    /// <summary>The process cannot continue.</summary>
    Critical = 4,
}

/// <summary>
/// The wire names of <see cref="LogLevel"/>. They are lowercase words, matching the names the existing
/// product logs already use, and an unknown level is refused instead of being guessed.
/// </summary>
public static class LogLevelNames
{
    private static readonly FrozenDictionary<LogLevel, string> _wireNamesByLevel = CreateWireNamesByLevel();
    private static readonly FrozenDictionary<string, LogLevel> _levelsByWireName = CreateLevelsByWireName(_wireNamesByLevel);

    /// <summary>Returns the lowercase wire name of a level.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="LogLevel"/>.</exception>
    public static string ToWireName(LogLevel level)
    {
        if (!_wireNamesByLevel.TryGetValue(level, out string? wireName))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown log level.");
        }

        return wireName;
    }

    /// <summary>Tries to parse a level wire name; a null or unknown name returns false.</summary>
    public static bool TryFromWireName(string? wireName, out LogLevel level)
    {
        if (wireName is null)
        {
            level = default;
            return false;
        }

        return _levelsByWireName.TryGetValue(wireName, out level);
    }

    private static FrozenDictionary<LogLevel, string> CreateWireNamesByLevel()
    {
        Dictionary<LogLevel, string> wireNames = new()
        {
            [LogLevel.Debug] = "debug",
            [LogLevel.Information] = "information",
            [LogLevel.Warning] = "warning",
            [LogLevel.Error] = "error",
            [LogLevel.Critical] = "critical",
        };

        if (wireNames.Count != Enum.GetValues<LogLevel>().Length)
        {
            throw new InvalidOperationException(
                "The log level wire names must cover every log level exactly once.");
        }

        return wireNames.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, LogLevel> CreateLevelsByWireName(
        FrozenDictionary<LogLevel, string> wireNamesByLevel)
    {
        Dictionary<string, LogLevel> levels = new(StringComparer.Ordinal);
        foreach (KeyValuePair<LogLevel, string> pair in wireNamesByLevel)
        {
            if (!levels.TryAdd(pair.Value, pair.Key))
            {
                throw new InvalidOperationException(
                    $"The log level wire name '{pair.Value}' is mapped more than once.");
            }
        }

        return levels.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
