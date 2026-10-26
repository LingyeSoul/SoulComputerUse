using System;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Logging;

/// <summary>
/// The value checks shared by <see cref="LogRecord"/> and <see cref="LogRecordWriter"/>. Both run them
/// on purpose: the record refuses an invalid value at construction, and the encoder refuses it again
/// before a byte is written, so no future caller can bypass the rules by building a record another way.
/// </summary>
/// <remarks>
/// No message repeats the offending value. A rejected value is exactly the kind of machine data that
/// must not reach a log, an exception message, or a console.
/// </remarks>
internal static class LogRecordValidation
{
    /// <summary>Rejects a missing or non-UTC timestamp.</summary>
    internal static void EnsureUtcTimestamp(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp == default)
        {
            throw new ArgumentException("A log record needs a real timestamp, not the default value.", parameterName);
        }

        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A log record timestamp must be UTC, so its offset must be zero.", parameterName);
        }
    }

    /// <summary>Rejects an undefined level.</summary>
    internal static void EnsureDefinedLevel(LogLevel level, string parameterName)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(parameterName, level, "Unknown log level.");
        }
    }

    /// <summary>Rejects an undefined event.</summary>
    internal static void EnsureDefinedEvent(LogEvent logEvent, string parameterName)
    {
        if (!Enum.IsDefined(logEvent))
        {
            throw new ArgumentOutOfRangeException(parameterName, logEvent, "Unknown log event.");
        }
    }

    /// <summary>Rejects a session id that is not a UUID in the contract's 'D' format.</summary>
    internal static void EnsureSessionId(string? sessionId, string parameterName)
    {
        if (sessionId is null)
        {
            return;
        }

        // The shape check runs first: a value that looks like a path or an account must be refused as
        // that kind of value, and a UUID never has that shape anyway.
        LogRedactor.EnsureNoForbiddenShape(sessionId, parameterName);

        if (!Guid.TryParseExact(sessionId, "D", out _))
        {
            throw new ArgumentException(
                "A log record session id must be a UUID in 'D' format; the rejected value is not printed.",
                parameterName);
        }
    }

    /// <summary>Rejects an undefined state.</summary>
    internal static void EnsureDefinedState(SessionState? state, string parameterName)
    {
        if (state is not null && !Enum.IsDefined(state.Value))
        {
            throw new ArgumentOutOfRangeException(parameterName, state, "Unknown session state.");
        }
    }

    /// <summary>Rejects an undefined error code.</summary>
    internal static void EnsureDefinedCode(SessionErrorCode? code, string parameterName)
    {
        if (code is not null && !Enum.IsDefined(code.Value))
        {
            throw new ArgumentOutOfRangeException(parameterName, code, "Unknown session error code.");
        }
    }

    /// <summary>Rejects an undefined tool.</summary>
    internal static void EnsureDefinedMethod(SessionTool? method, string parameterName)
    {
        if (method is not null && !Enum.IsDefined(method.Value))
        {
            throw new ArgumentOutOfRangeException(parameterName, method, "Unknown session tool.");
        }
    }

    /// <summary>Rejects a negative duration.</summary>
    internal static void EnsureDurationMs(int? durationMs, string parameterName)
    {
        if (durationMs is not null && durationMs.Value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, durationMs, "A log record duration must not be negative.");
        }
    }

    /// <summary>Rejects a process id that is not positive.</summary>
    internal static void EnsureProcessId(int? pid, string parameterName)
    {
        if (pid is not null && pid.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, pid, "A log record process id must be positive.");
        }
    }

    /// <summary>Rejects a screen bound that is not positive.</summary>
    internal static void EnsurePositiveBound(int? bound, string parameterName)
    {
        if (bound is not null && bound.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, bound, "A log record screen bound must be positive.");
        }
    }
}
