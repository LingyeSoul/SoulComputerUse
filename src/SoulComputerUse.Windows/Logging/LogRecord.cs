using System;
using System.Diagnostics.CodeAnalysis;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Logging;

/// <summary>
/// One structured log line, restricted to the eleven fields of section 10 of the interface contract:
/// the timestamp, the level, the event, the session id, the state, the error code, the tool method,
/// the duration, the process id and the two screen bounds.
/// </summary>
/// <remarks>
/// The type has no free-text member at all, so a window title, typed text, a screenshot, an account
/// name, a path, a SID or an IP address cannot be attached to a record even by accident. The
/// constructor refuses every value that would contradict the field it is put into; the encoder checks
/// the same rules again before writing.
/// </remarks>
public sealed record LogRecord
{
    /// <summary>Builds one record and refuses values that the field cannot legally carry.</summary>
    /// <exception cref="ArgumentException">The timestamp is missing or not UTC, or the session id is not a UUID in 'D' format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is undefined, or a numeric value is out of range.</exception>
    public LogRecord(
        DateTimeOffset timestamp,
        LogLevel level,
        LogEvent logEvent,
        string? sessionId,
        SessionState? state,
        SessionErrorCode? code,
        SessionTool? method,
        int? durationMs,
        int? pid,
        int? width,
        int? height)
    {
        LogRecordValidation.EnsureUtcTimestamp(timestamp, nameof(Timestamp));
        LogRecordValidation.EnsureDefinedLevel(level, nameof(Level));
        LogRecordValidation.EnsureDefinedEvent(logEvent, nameof(Event));
        LogRecordValidation.EnsureSessionId(sessionId, nameof(SessionId));
        LogRecordValidation.EnsureDefinedState(state, nameof(State));
        LogRecordValidation.EnsureDefinedCode(code, nameof(Code));
        LogRecordValidation.EnsureDefinedMethod(method, nameof(Method));
        LogRecordValidation.EnsureDurationMs(durationMs, nameof(DurationMs));
        LogRecordValidation.EnsureProcessId(pid, nameof(Pid));
        LogRecordValidation.EnsurePositiveBound(width, nameof(Width));
        LogRecordValidation.EnsurePositiveBound(height, nameof(Height));

        Timestamp = timestamp;
        Level = level;
        Event = logEvent;
        SessionId = sessionId;
        State = state;
        Code = code;
        Method = method;
        DurationMs = durationMs;
        Pid = pid;
        Width = width;
        Height = height;
    }

    /// <summary>When the event happened, in UTC.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>How severe the event is.</summary>
    public LogLevel Level { get; }

    /// <summary>Which fixed event this record reports.</summary>
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "The contract names this log field 'event', and the record member must carry that name.")]
    public LogEvent Event { get; }

    /// <summary>The random session id, or <c>null</c> when the event does not belong to a session.</summary>
    public string? SessionId { get; }

    /// <summary>The state the session was in, or <c>null</c> when the event does not occur in a state.</summary>
    public SessionState? State { get; }

    /// <summary>The contract error code, or <c>null</c> when the event reports no error.</summary>
    public SessionErrorCode? Code { get; }

    /// <summary>The IPC, MCP or CLI method the event is attributed to, or <c>null</c>.</summary>
    public SessionTool? Method { get; }

    /// <summary>The elapsed time of the reported step in milliseconds, or <c>null</c>.</summary>
    public int? DurationMs { get; }

    /// <summary>The process id the event belongs to, or <c>null</c>.</summary>
    public int? Pid { get; }

    /// <summary>The remote screen width in physical pixels, or <c>null</c>.</summary>
    public int? Width { get; }

    /// <summary>The remote screen height in physical pixels, or <c>null</c>.</summary>
    public int? Height { get; }
}
