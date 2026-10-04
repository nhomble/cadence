using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Mcp;

namespace Cadence.Tests.Mcp;

/// <summary>A fake tool host: a read tool "echo", a tool that throws, a tool that blocks until released, and a tool with newlines in its text.</summary>
internal sealed class FakeToolHost : IToolHost
{
    public readonly ManualResetEventSlim SlowStarted = new(false);
    public readonly ManualResetEventSlim SlowGate = new(false);
    public readonly ConcurrentQueue<string> Calls = new();
    public CallContext? LastContext;

    public IReadOnlyList<ToolDefinition> Tools { get; } =
    [
        new("echo", "Echoes its text.", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"additionalProperties\":false}", new ToolAnnotations("Echo", true, false, true)),
        new("boom", "Always throws.", "{\"type\":\"object\"}", new ToolAnnotations("Boom", true, false, true)),
        new("slow", "Blocks until released.", "{\"type\":\"object\"}", new ToolAnnotations("Slow", true, false, true)),
        new("lines", "Text with line breaks.", "{\"type\":\"object\"}", new ToolAnnotations("Lines", true, false, true)),
    ];

    public bool Has(string name) => Tools.Any(t => t.Name == name);

    public ToolResult Call(string name, JsonElement arguments, CallContext context)
    {
        LastContext = context;
        Calls.Enqueue(name);
        switch (name)
        {
            case "echo":
                string text = arguments.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? "" : "";
                return new ToolResult(false, text, new JsonObject { ["text"] = text });
            case "boom":
                throw new InvalidOperationException("kaboom");
            case "slow":
                SlowStarted.Set();
                SlowGate.Wait(TimeSpan.FromSeconds(20));
                return new ToolResult(false, "slow done", new JsonObject { ["done"] = true });
            case "lines":
                return new ToolResult(false, "a\nb\r\nc" + (char)0x2028 + "d\te \"q\" " + (char)0xE9 + (char)0x65E5, null);
            default:
                throw new InvalidOperationException("unknown " + name);
        }
    }
}

/// <summary>An input stream the test feeds line by line; Read blocks until data or Complete().</summary>
internal sealed class FeedStream : Stream
{
    readonly BlockingCollection<byte[]> _chunks = [];
    byte[] _current = [];
    int _pos;

    public void Feed(string text) => _chunks.Add(new UTF8Encoding(false).GetBytes(text));

    public void FeedBytes(byte[] bytes) => _chunks.Add(bytes);

    public void Line(string json) => Feed(json + "\n");

    public void Complete() => _chunks.CompleteAdding();

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_pos >= _current.Length)
        {
            if (!_chunks.TryTake(out byte[]? next, Timeout.Infinite)) return 0;   // completed and drained
            _current = next;
            _pos = 0;
        }
        int n = Math.Min(count, _current.Length - _pos);
        Array.Copy(_current, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>An output stream that keeps the raw bytes and can wait for a number of complete lines.</summary>
internal sealed class CaptureStream : Stream
{
    readonly object _gate = new();
    readonly List<byte> _bytes = [];
    public bool FailWrites { get; set; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (FailWrites) throw new IOException("pipe closed");
            for (int i = 0; i < count; i++) _bytes.Add(buffer[offset + i]);
            Monitor.PulseAll(_gate);
        }
    }

    public byte[] Raw()
    {
        lock (_gate) return [.. _bytes];
    }

    public List<string> Lines()
    {
        string all = new UTF8Encoding(false).GetString(Raw());
        return [.. all.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    public bool WaitForLines(int n, int ms = 10000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        lock (_gate)
        {
            while (_bytes.Count(b => b == (byte)'\n') < n)
            {
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return false;
                Monitor.Wait(_gate, left);
            }
        }
        return true;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A running server over a <see cref="FeedStream"/> and a <see cref="CaptureStream"/> on its own thread.</summary>
internal sealed class Session
{
    public readonly FakeToolHost Host = new();
    public readonly FeedStream Input = new();
    public readonly CaptureStream Output = new();
    public readonly StringWriter Log = new();
    readonly Task<int> _run;

    public Session(int maxQueue = 64, IToolHost? host = null, Action<SessionInfo>? onConnected = null, Action<SessionInfo>? onDisconnected = null, TimeSpan? drainLimit = null, TimeSpan? writeDrainLimit = null)
    {
        var server = new McpServer(Cadence.Agent.McpHost.ServerInfo("9.9.9"), host ?? Host, TextWriter.Synchronized(Log), "the instructions", maxQueue, onConnected, onDisconnected, drainLimit, writeDrainLimit, Cadence.Agent.McpHost.ServerOptions);
        _run = Task.Run(() => server.Run(Input, Output));
    }

    public const string Init = "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"fake\",\"title\":\"Fake Agent\",\"version\":\"3\"}}}";

    public void Initialize()
    {
        Input.Line(Init);
        Output.WaitForLines(1);
    }

    /// <summary>Closes stdin and waits for Run's exit code (-999 when it did not finish in time).</summary>
    public int Finish(int ms = 15000)
    {
        Input.Complete();
        return _run.Wait(ms) ? _run.Result : -999;
    }

    /// <summary>Polls until the condition holds (true) or the time runs out (false).</summary>
    public bool WaitUntil(Func<bool> condition, int ms = 5000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow >= end) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    public int WaitExit(int ms = 15000) => _run.Wait(ms) ? _run.Result : -999;

    public List<JsonElement> Replies()
    {
        var list = new List<JsonElement>();
        foreach (string l in Output.Lines()) list.Add(JsonDocument.Parse(l).RootElement.Clone());
        return list;
    }

    /// <summary>One shot: every line in, EOF, all replies out.</summary>
    public static (int rc, List<JsonElement> replies, byte[] raw) Batch(params string[] lines)
    {
        var s = new Session();
        foreach (string l in lines) s.Input.Line(l);
        int rc = s.Finish();
        return (rc, s.Replies(), s.Output.Raw());
    }

    public static JsonElement ById(List<JsonElement> replies, long id) =>
        replies.First(r => r.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.Number && i.GetInt64() == id);
}
