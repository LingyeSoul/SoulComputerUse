using System;
using System.Globalization;
using System.Text;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Logging;

/// <summary>
/// The defensive half of the redaction rule: it refuses a value whose shape belongs to the blacklist of
/// section 10, and it renders a record for a console without adding a field.
/// </summary>
/// <remarks>
/// The shape check cannot decide whether a string is a window title or typed text, because those have
/// no distinguishing shape. It does refuse the shapes that are mechanically recognizable: line breaks
/// and control characters (a multi-line value is a body of text, not an identifier), path separators,
/// and an <c>@</c> sign (an account or mail-style identifier). Any future string field outside the
/// eleven whitelisted ones must run through this check before it is emitted anywhere.
/// </remarks>
public static class LogRedactor
{
    /// <summary>
    /// Refuses a value that carries a newline, a tab, any other control character, a path separator or
    /// an <c>@</c> sign. A null value is accepted, because an absent field is not a leak.
    /// </summary>
    /// <param name="value">The candidate value; null is accepted.</param>
    /// <param name="fieldName">The field the value would be written to, echoed in the failure.</param>
    /// <exception cref="ArgumentException">The value has a forbidden shape.</exception>
    public static void EnsureNoForbiddenShape(string? value, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        if (value is null)
        {
            return;
        }

        if (ContainsControlCharacter(value))
        {
            throw new ArgumentException(
                "The value contains a line break, a tab or another control character, so it cannot be an identifier.",
                fieldName);
        }

        if (value.Contains('\\', StringComparison.Ordinal) || value.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value contains a path separator, so it cannot be an identifier.",
                fieldName);
        }

        if (value.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value contains an '@' sign, so it looks like an account or mail-style identifier.",
                fieldName);
        }
    }

    /// <summary>
    /// Renders one record as a single human-readable line for console diagnostics. Only the eleven
    /// whitelisted fields appear, absent fields are left out, and no free text is added.
    /// </summary>
    /// <exception cref="ArgumentNullException">The record is null.</exception>
    public static string Describe(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        StringBuilder builder = new();
        _ = builder.Append(record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        _ = builder.Append(' ').Append(LogLevelNames.ToWireName(record.Level));
        _ = builder.Append(' ').Append(LogEventNames.ToWireName(record.Event));

        if (record.SessionId is not null)
        {
            _ = builder.Append(" sessionId=").Append(record.SessionId);
        }

        if (record.State is not null)
        {
            _ = builder.Append(" state=").Append(SessionStateNames.ToWireName(record.State.Value));
        }

        if (record.Code is not null)
        {
            _ = builder.Append(" code=").Append(SessionErrorCodeNames.ToWireName(record.Code.Value));
        }

        if (record.Method is not null)
        {
            _ = builder.Append(" method=").Append(SessionToolNames.ToWireName(record.Method.Value));
        }

        if (record.DurationMs is not null)
        {
            _ = builder
                .Append(" durationMs=")
                .Append(record.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (record.Pid is not null)
        {
            _ = builder.Append(" pid=").Append(record.Pid.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (record.Width is not null)
        {
            _ = builder.Append(" width=").Append(record.Width.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (record.Height is not null)
        {
            _ = builder.Append(" height=").Append(record.Height.Value.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}
