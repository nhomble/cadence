using System.Text;
using System.Text.Json;
using Cadence.Mcp;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>Phase 17: the newline framing over in-memory streams.</summary>
public static class LineTransportTests
{
    sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
    }

    static LineTransport Over(byte[] data, int chunk = 0) =>
        new(chunk > 0 ? new TrickleStream(data, chunk) : new MemoryStream(data), new MemoryStream());

    static LineTransport Over(string text, int chunk = 0) => Over(new UTF8Encoding(false).GetBytes(text), chunk);

    static string Text(ReadResult r) => new UTF8Encoding(false).GetString(r.Line!);

    public static void SplitsLinesAndEndsWithEof()
    {
        LineTransport t = Over("one\ntwo\n");
        T.Eq("first", "one", Text(t.Read()));
        T.Eq("second", "two", Text(t.Read()));
        T.Eq("then EOF", ReadKind.Eof, t.Read().Kind);
        T.Eq("EOF again", ReadKind.Eof, t.Read().Kind);
    }

    public static void EmptyStreamIsEof() => T.Eq("empty", ReadKind.Eof, Over("").Read().Kind);

    public static void CrlfIsAccepted()
    {
        LineTransport t = Over("one\r\ntwo\r\n\r\nthree");
        T.Eq("1", "one", Text(t.Read()));
        T.Eq("2", "two", Text(t.Read()));
        T.Eq("blank line", "", Text(t.Read()));
        T.Eq("last line without a line break is a line", "three", Text(t.Read()));
        T.Eq("then EOF", ReadKind.Eof, t.Read().Kind);
    }

    public static void BomIsStrippedOnceAtTheStart()
    {
        byte[] data = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n', 0xEF, 0xBB, 0xBF, (byte)'b', (byte)'\n'];
        LineTransport t = Over(data);
        T.Eq("first line has no BOM", "a", Text(t.Read()));
        T.Eq("a later BOM is data", "﻿b", Text(t.Read()));
        T.Eq("BOM split over reads", "x", Text(Over([0xEF, 0xBB, 0xBF, (byte)'x'], chunk: 1).Read()));
    }

    public static void OneByteAtATime()
    {
        LineTransport t = Over("hello world\nsecond line\n", chunk: 1);
        T.Eq("1", "hello world", Text(t.Read()));
        T.Eq("2", "second line", Text(t.Read()));
        T.Eq("EOF", ReadKind.Eof, t.Read().Kind);
    }

    public static void LinesLongerThanTheBufferAreAssembled()
    {
        string big = new('x', 200_000);
        LineTransport t = Over(big + "\nnext\n");
        T.Eq("long line length", 200_000, t.Read().Line!.Length);
        T.Eq("next", "next", Text(t.Read()));
    }

    public static void OversizeLineIsDiscardedAndReadingContinues()
    {
        byte[] over = new byte[LineTransport.MaxMessageBytes + 1];
        Array.Fill(over, (byte)'a');
        byte[] exact = new byte[LineTransport.MaxMessageBytes];
        Array.Fill(exact, (byte)'b');
        byte[] data = [.. over, (byte)'\n', .. "ok\n"u8.ToArray(), .. exact, (byte)'\n', .. "end\n"u8.ToArray()];
        LineTransport t = Over(data);
        ReadResult r = t.Read();
        T.Eq("one byte over the limit", ReadKind.TooLarge, r.Kind);
        T.True("no bytes are kept for it", r.Line is null);
        T.Eq("the next line is served", "ok", Text(t.Read()));
        T.Eq("exactly the limit is accepted", LineTransport.MaxMessageBytes, t.Read().Line!.Length);
        T.Eq("and the one after", "end", Text(t.Read()));
    }

    public static void OversizeLineAtEofIsReported()
    {
        byte[] over = new byte[LineTransport.MaxMessageBytes + 10];
        Array.Fill(over, (byte)'a');
        LineTransport t = Over(over);
        T.Eq("too large", ReadKind.TooLarge, t.Read().Kind);
        T.Eq("then EOF", ReadKind.Eof, t.Read().Kind);
    }

    public static void WriteAppendsOneLineBreakAndNoBom()
    {
        var output = new MemoryStream();
        var t = new LineTransport(new MemoryStream(), output);
        T.True("write ok", t.Write("{\"a\":1}"u8));
        T.True("write ok again", t.Write("{\"b\":2}"u8));
        byte[] raw = output.ToArray();
        T.Eq("text", "{\"a\":1}\n{\"b\":2}\n", Encoding.UTF8.GetString(raw));
        T.True("no BOM", raw[0] == (byte)'{');
    }

    sealed class ClosedStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("closed");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("closed");
        public override void WriteByte(byte value) => throw new IOException("closed");
    }

    public static void WriteReturnsFalseWhenThePipeIsClosed()
    {
        var t = new LineTransport(new MemoryStream(), new ClosedStream());
        T.True("false", !t.Write("{}"u8));
        T.True("stays false", !t.Write("{}"u8));
    }

    public static void ConcurrentWritesNeverInterleave()
    {
        var output = new MemoryStream();
        var t = new LineTransport(new MemoryStream(), output);
        string payload = new('p', 2000);
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            t.Write(Encoding.UTF8.GetBytes("{\"i\":" + i + ",\"p\":\"" + payload + "\"}")));
        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        T.Eq("200 lines", 200, lines.Length);
        var seen = new HashSet<int>();
        bool allOk = true;
        foreach (string l in lines)
        {
            try { seen.Add(JsonDocument.Parse(l).RootElement.GetProperty("i").GetInt32()); }
            catch (JsonException) { allOk = false; }
        }
        T.True("every line is one intact JSON object", allOk);
        T.Eq("every id once", 200, seen.Count);
    }
}
