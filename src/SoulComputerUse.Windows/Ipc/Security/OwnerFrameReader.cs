using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulComputerUse.Windows.Ipc.Security;

/// <summary>How one line read from an owner pipe ended.</summary>
internal enum OwnerFrameReadStatus
{
    /// <summary>The peer closed the pipe and no further bytes arrived.</summary>
    EndOfStream = 0,

    /// <summary>One complete line was read and decoded.</summary>
    Line = 1,

    /// <summary>The line exceeded <see cref="OwnerPipeLimits.MaximumFrameBytes"/>.</summary>
    FrameTooLong = 2,
}

/// <summary>One line read from an owner pipe, or the reason no line could be produced.</summary>
internal readonly record struct OwnerFrameReadResult(OwnerFrameReadStatus Status, string Line)
{
    /// <summary>Creates the result for a peer that closed the pipe between frames.</summary>
    internal static OwnerFrameReadResult EndOfStream()
    {
        return new OwnerFrameReadResult(OwnerFrameReadStatus.EndOfStream, string.Empty);
    }

    /// <summary>Creates the result for one complete, decoded line.</summary>
    internal static OwnerFrameReadResult ForLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new OwnerFrameReadResult(OwnerFrameReadStatus.Line, line);
    }

    /// <summary>Creates the result for a line that crossed the frame limit.</summary>
    internal static OwnerFrameReadResult FrameTooLong()
    {
        return new OwnerFrameReadResult(OwnerFrameReadStatus.FrameTooLong, string.Empty);
    }
}

/// <summary>
/// Reads <c>\n</c>-terminated UTF-8 lines from a pipe while enforcing
/// <see cref="OwnerPipeLimits.MaximumFrameBytes"/> during the read. An oversized line is refused as
/// soon as its size is known, so the reader never holds more than the limit plus its chunk buffer.
/// </summary>
/// <remarks>
/// A line may be exactly the limit in bytes; the terminator is not counted. Only <c>\n</c> terminates
/// a line, so a lone <c>\r</c> stays in the text; a trailing <c>\r</c> before the terminator is stripped
/// so a peer that sends <c>\r\n</c> sees the same text as <see cref="StreamReader.ReadLine"/>.
/// After an oversized line the reader refuses further reads; the connection must be closed instead.
/// </remarks>
internal sealed class OwnerFrameReader
{
    private const int ReadChunkBytes = 8192;
    private const int InitialLineBytes = 256;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _stream;
    private readonly byte[] _chunk = new byte[ReadChunkBytes];
    private byte[] _line = new byte[InitialLineBytes];
    private int _chunkOffset;
    private int _chunkCount;
    private bool _reachedEnd;
    private bool _refused;

    /// <summary>Creates one reader over a connected pipe stream.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    internal OwnerFrameReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// Reads one line. The returned line is decoded UTF-8 without its terminator.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="IOException">The pipe failed while it was being read.</exception>
    /// <exception cref="InvalidOperationException">A previous read already refused an oversized line.</exception>
    internal async ValueTask<OwnerFrameReadResult> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_refused)
        {
            throw new InvalidOperationException(
                "The frame reader already refused an oversized line, so the connection cannot be read again.");
        }

        int lineLength = 0;
        while (true)
        {
            if (_chunkOffset == _chunkCount)
            {
                if (_reachedEnd)
                {
                    return lineLength == 0
                        ? OwnerFrameReadResult.EndOfStream()
                        : CompleteLine(lineLength);
                }

                _chunkCount = await _stream.ReadAsync(_chunk.AsMemory(0, _chunk.Length), cancellationToken);
                _chunkOffset = 0;
                if (_chunkCount == 0)
                {
                    _reachedEnd = true;
                    return lineLength == 0
                        ? OwnerFrameReadResult.EndOfStream()
                        : CompleteLine(lineLength);
                }
            }

            ReadOnlySpan<byte> available = _chunk.AsSpan(_chunkOffset, _chunkCount - _chunkOffset);
            int newlineIndex = available.IndexOf((byte)'\n');
            int segmentLength = newlineIndex >= 0 ? newlineIndex : available.Length;

            if (segmentLength > OwnerPipeLimits.MaximumFrameBytes - lineLength)
            {
                _refused = true;
                return OwnerFrameReadResult.FrameTooLong();
            }

            Append(available[..segmentLength], lineLength);
            lineLength += segmentLength;

            if (newlineIndex >= 0)
            {
                _chunkOffset += newlineIndex + 1;
                return CompleteLine(lineLength);
            }

            _chunkOffset = _chunkCount;
        }
    }

    private void Append(ReadOnlySpan<byte> bytes, int usedLength)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        int required = usedLength + bytes.Length;
        if (required > _line.Length)
        {
            int capacity = Math.Min(_line.Length * 2, OwnerPipeLimits.MaximumFrameBytes);
            if (capacity < required)
            {
                capacity = required;
            }

            Array.Resize(ref _line, capacity);
        }

        bytes.CopyTo(_line.AsSpan(usedLength));
    }

    private OwnerFrameReadResult CompleteLine(int lineLength)
    {
        int length = lineLength;
        if (length > 0 && _line[length - 1] == (byte)'\r')
        {
            length--;
        }

        return OwnerFrameReadResult.ForLine(Utf8WithoutBom.GetString(_line, 0, length));
    }
}
