using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Logging;

/// <summary>
/// Encodes one <see cref="LogRecord"/> as a single JSON Lines line. Only the eleven whitelisted fields
/// are written, always in the contract order, and a null field is omitted instead of being written as
/// JSON null.
/// </summary>
/// <remarks>
/// The encoder validates every value again before it writes, even though <see cref="LogRecord"/> already
/// rejected illegal values at construction. A record can only exist through that constructor today, but
/// the second check keeps the promise true for any future construction path and makes the rules an
/// encoding property rather than a constructor detail.
/// </remarks>
public static class LogRecordWriter
{
    /// <summary>Encodes one record as one line of JSON without a trailing newline.</summary>
    /// <exception cref="ArgumentNullException">The record is null.</exception>
    /// <exception cref="ArgumentException">The timestamp is missing or not UTC, or the session id is not a UUID in 'D' format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is undefined, or a numeric value is out of range.</exception>
    public static string Write(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureEncodable(record);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            WriteBody(writer, record);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Creates the writer with relaxed escaping. A timestamp contains only digits, punctuation and a
    /// '+', and every other value is an identifier from a fixed enumeration or a bare number, so the
    /// relaxed encoder cannot let an unsafe character through; it only keeps '+' readable instead of
    /// writing it as a Unicode escape.
    /// </summary>
    private static Utf8JsonWriter CreateWriter(ArrayBufferWriter<byte> buffer)
    {
        JsonWriterOptions options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return new Utf8JsonWriter(buffer, options);
    }

    /// <summary>
    /// Applies the field rules to an already built record. Kept separate from the constructor rules so
    /// the encoder cannot drift from the record it writes.
    /// </summary>
    private static void EnsureEncodable(LogRecord record)
    {
        LogRecordValidation.EnsureUtcTimestamp(record.Timestamp, nameof(record));
        LogRecordValidation.EnsureDefinedLevel(record.Level, nameof(record));
        LogRecordValidation.EnsureDefinedEvent(record.Event, nameof(record));
        LogRecordValidation.EnsureSessionId(record.SessionId, nameof(record));
        LogRecordValidation.EnsureDefinedState(record.State, nameof(record));
        LogRecordValidation.EnsureDefinedCode(record.Code, nameof(record));
        LogRecordValidation.EnsureDefinedMethod(record.Method, nameof(record));
        LogRecordValidation.EnsureDurationMs(record.DurationMs, nameof(record));
        LogRecordValidation.EnsureProcessId(record.Pid, nameof(record));
        LogRecordValidation.EnsurePositiveBound(record.Width, nameof(record));
        LogRecordValidation.EnsurePositiveBound(record.Height, nameof(record));
    }

    private static void WriteBody(Utf8JsonWriter writer, LogRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("ts", record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("level", LogLevelNames.ToWireName(record.Level));
        writer.WriteString("event", LogEventNames.ToWireName(record.Event));

        if (record.SessionId is not null)
        {
            writer.WriteString("sessionId", record.SessionId);
        }

        if (record.State is not null)
        {
            writer.WriteString("state", SessionStateNames.ToWireName(record.State.Value));
        }

        if (record.Code is not null)
        {
            writer.WriteString("code", SessionErrorCodeNames.ToWireName(record.Code.Value));
        }

        if (record.Method is not null)
        {
            writer.WriteString("method", SessionToolNames.ToWireName(record.Method.Value));
        }

        if (record.DurationMs is not null)
        {
            writer.WriteNumber("durationMs", record.DurationMs.Value);
        }

        if (record.Pid is not null)
        {
            writer.WriteNumber("pid", record.Pid.Value);
        }

        if (record.Width is not null)
        {
            writer.WriteNumber("width", record.Width.Value);
        }

        if (record.Height is not null)
        {
            writer.WriteNumber("height", record.Height.Value);
        }

        writer.WriteEndObject();
    }
}
