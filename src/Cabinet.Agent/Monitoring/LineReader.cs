using System.Runtime.CompilerServices;
using System.Text;

namespace Cabinet.Agent.Monitoring;

/// <summary>
/// One complete line read from a file, with the byte range it occupies.
/// </summary>
/// <param name="Text">The line, without its line ending.</param>
/// <param name="StartOffset">Byte offset of the start of the line.</param>
/// <param name="EndOffset">Byte offset just past the line ending, where the next line starts.</param>
public readonly record struct TailLine(string Text, long StartOffset, long EndOffset);

/// <summary>
/// Reads complete lines from a growing file, tracking exact byte offsets. An unterminated last
/// line isn't returned until its line ending arrives, so a line being written is never split into
/// two events. Lines longer than the limit are truncated without buffering the excess.
/// </summary>
public sealed class LineReader
{
    private const int BufferSize = 64 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly MemoryStream _line = new();
    private long _readPosition;
    private long _lineStart;

    public LineReader(Stream stream, long offset, int maxLineBytes)
    {
        _stream = stream;
        _maxLineBytes = maxLineBytes;
        _readPosition = offset;
        _lineStart = offset;
    }

    /// <summary>Offset of the first byte not yet returned as part of a complete line.</summary>
    public long Position => _lineStart;

    /// <summary>
    /// Returns the complete lines between the current position and the end of the file.
    /// </summary>
    public async IAsyncEnumerable<TailLine> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            _stream.Seek(_readPosition, SeekOrigin.Begin);
            var count = await _stream.ReadAsync(_buffer, cancellationToken);
            if (count == 0)
            {
                yield break;
            }

            var segmentStart = 0;
            for (var i = 0; i < count; i++)
            {
                if (_buffer[i] != (byte)'\n')
                {
                    continue;
                }

                Append(_buffer.AsSpan(segmentStart, i - segmentStart));
                segmentStart = i + 1;

                var end = _readPosition + segmentStart;
                var line = new TailLine(DecodeLine(), _lineStart, end);
                _line.SetLength(0);
                _lineStart = end;
                yield return line;
            }

            Append(_buffer.AsSpan(segmentStart, count - segmentStart));
            _readPosition += count;
        }
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        var room = _maxLineBytes - (int)_line.Length;
        if (room > 0)
        {
            _line.Write(bytes[..Math.Min(room, bytes.Length)]);
        }
    }

    /// <summary>
    /// The event text a byte range holds, as tailing would produce it: its lines joined by newlines.
    /// </summary>
    public static string ReadRange(Stream stream, long start, long end, int maxLineBytes)
    {
        var bytes = new byte[end - start];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(bytes);

        var lines = new List<string>();
        var position = 0;
        while (position < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', position);
            var stop = newline < 0 ? bytes.Length : newline;
            var length = Math.Min(stop - position, maxLineBytes);
            lines.Add(Decode(bytes.AsSpan(position, length), start + position == 0));
            position = stop + 1;
        }

        return string.Join('\n', lines);
    }

    private string DecodeLine() =>
        Decode(_line.GetBuffer().AsSpan(0, (int)_line.Length), _lineStart == 0);

    private static string Decode(ReadOnlySpan<byte> bytes, bool isFileStart)
    {
        // A byte order mark at the start of the file isn't part of the first line.
        if (isFileStart && bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }

        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        return Utf8.GetString(bytes);
    }
}
