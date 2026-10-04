namespace Cadence.Mcp;

public enum ReadKind { Line, TooLarge, Eof }

/// <summary>One read: a complete line (without the line break) or one of the two special outcomes.</summary>
public readonly record struct ReadResult(ReadKind Kind, byte[]? Line);

/// <summary>
/// Newline-delimited UTF-8 framing over raw streams (PLAN 12.3.4). Reads bytes (never <c>Console.In</c>), bounded: a line longer than
/// <see cref="MaxMessageBytes"/> is skipped without being buffered and reported as <see cref="ReadKind.TooLarge"/>. Writes one message per
/// line with one flush, serialized by a lock, never with a BOM. Read is for ONE thread; Write is thread-safe.
/// </summary>
public sealed class LineTransport
{
    public const int MaxMessageBytes = 1 << 20;

    readonly Stream _input;
    readonly Stream _output;
    readonly byte[] _buf = new byte[64 * 1024];
    int _pos;
    int _len;
    bool _eof;
    bool _first = true;
    readonly object _writeLock = new();
    bool _writeClosed;

    public LineTransport(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>The next line. A trailing CR is removed and a UTF-8 BOM at the very start of the stream is removed once. An unterminated last line is returned as a line (then Eof).</summary>
    public ReadResult Read()
    {
        var line = new MemoryStream();
        bool discarding = false;
        long count = 0;
        while (true)
        {
            if (_pos >= _len)
            {
                if (_eof) return Finish(line, discarding, count, sawAny: count > 0);
                _pos = 0;
                try { _len = _input.Read(_buf, 0, _buf.Length); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { _len = 0; }
                if (_len <= 0) { _len = 0; _eof = true; continue; }
            }

            int nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
            int take = (nl < 0 ? _len : nl) - _pos;
            count += take;
            if (!discarding)
            {
                if (count > MaxMessageBytes) { discarding = true; line.SetLength(0); }
                else line.Write(_buf, _pos, take);
            }
            _pos += take;
            if (nl >= 0)
            {
                _pos++;   // the \n
                return Finish(line, discarding, count, sawAny: true);
            }
        }
    }

    ReadResult Finish(MemoryStream line, bool discarding, long count, bool sawAny)
    {
        if (discarding) return new ReadResult(ReadKind.TooLarge, null);
        if (!sawAny && count == 0 && _eof) return new ReadResult(ReadKind.Eof, null);
        byte[] bytes = line.ToArray();
        int start = 0;
        if (_first)
        {
            _first = false;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
        }
        int end = bytes.Length;
        if (end > start && bytes[end - 1] == (byte)'\r') end--;
        return new ReadResult(ReadKind.Line, bytes[start..end]);
    }

    /// <summary>Writes the message and a line break, then flushes. False when the pipe is closed (nobody is listening).</summary>
    public bool Write(ReadOnlySpan<byte> utf8Json)
    {
        lock (_writeLock)
        {
            if (_writeClosed) return false;
            try
            {
                _output.Write(utf8Json);
                _output.WriteByte((byte)'\n');
                _output.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or UnauthorizedAccessException)
            {
                _writeClosed = true;
                return false;
            }
        }
    }
}
