using System;
using System.IO;

namespace SoulComputerUse.Windows.Logging;

/// <summary>
/// Writes encoded records to one <see cref="TextWriter"/> as JSON Lines: one record per line, flushed
/// immediately so a crash still leaves the last line on disk.
/// </summary>
/// <remarks>
/// A write failure propagates. Swallowing it would let the process believe it logged something it did
/// not, which is worse than a visible failure during teardown. When no log destination is configured,
/// use <see cref="Null"/>; the writer never falls back to the console on its own.
/// <para>
/// Each line is written under one lock, so concurrent writers sharing an instance produce whole lines
/// instead of interleaved fragments.
/// </para>
/// </remarks>
public sealed class StructuredLogWriter
{
    private static readonly StructuredLogWriter _null = new();

    private readonly TextWriter? _output;
    private readonly object _lineGate = new();

    private StructuredLogWriter()
    {
        _output = null;
    }

    /// <summary>Creates a writer that appends lines to one destination.</summary>
    /// <exception cref="ArgumentNullException">The destination is null.</exception>
    public StructuredLogWriter(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>
    /// A writer that discards every record. It exists for the unconfigured case so no caller has to
    /// pass a console writer and silently change where diagnostics go.
    /// </summary>
    public static StructuredLogWriter Null => _null;

    /// <summary>True when this writer discards its records.</summary>
    public bool IsNull => _output is null;

    /// <summary>Encodes one record, writes it as one line and flushes the destination.</summary>
    /// <exception cref="ArgumentNullException">The record is null.</exception>
    /// <exception cref="IOException">The destination rejected the write.</exception>
    /// <exception cref="ObjectDisposedException">The destination was already disposed.</exception>
    public void Write(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_output is null)
        {
            return;
        }

        string line = LogRecordWriter.Write(record);
        lock (_lineGate)
        {
            _output.WriteLine(line);
            _output.Flush();
        }
    }
}
