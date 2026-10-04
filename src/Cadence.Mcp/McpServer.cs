using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace Cadence.Mcp;

/// <summary>
/// The MCP server over newline-delimited JSON-RPC 2.0 (PLAN 12.3.4): one reader thread (parses, answers ping and protocol errors, queues
/// tool calls), one worker thread (runs tools strictly in arrival order), one writer lock (<see cref="LineTransport.Write"/>). It knows
/// nothing about Windows, WPF or Task Scheduler: the tools come in through <see cref="IToolHost"/>, so it runs over in-memory streams in tests.
/// </summary>
public sealed class McpServer
{
    static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;
    static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 64 };
    static readonly TimeSpan DefaultDrainLimit = TimeSpan.FromSeconds(10);

    /// <summary>[CP-R7] How long, at most, the server waits for a write that is already running when stdin has closed (the writer-lock timeout of 15 s plus 30 s).</summary>
    public static readonly TimeSpan DefaultWriteDrainLimit = TimeSpan.FromSeconds(45);

    sealed record Work(RpcId Id, string Tool, JsonDocument Doc, JsonElement Arguments, ClientIdentity Client, string Protocol);

    readonly McpServerInfo _info;
    readonly string _logPrefix;
    readonly IToolHost _tools;
    readonly TextWriter _log;
    readonly string? _instructions;
    readonly int _maxQueue;
    readonly Action<SessionInfo>? _onConnected;
    readonly Action<SessionInfo>? _onDisconnected;
    readonly TimeSpan _drainLimit;
    readonly TimeSpan _writeDrainLimit;
    Work? _running;               // the request the worker is executing now (guarded by _queueLock)
    bool _drainExtended;          // [CP-R7] the drain timer already waited once for a running write
    readonly Dictionary<string, JsonDocument> _schemas = new(StringComparer.Ordinal);
    readonly string _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    readonly object _queueLock = new();
    readonly LinkedList<Work> _queue = [];
    bool _closed;

    readonly ManualResetEventSlim _done = new(false);
    int _finished;
    int _exitCode;
    Timer? _drainTimer;
    LineTransport? _transport;

    // Touched by the reader thread only (the worker gets copies inside Work).
    bool _initialized;
    ClientIdentity _client = new("", null, null);
    string _protocol = McpProtocol.Latest;

    /// <param name="onConnected">Called once, on the reader thread, right after <c>initialize</c> is accepted (the audit log's "connected" line). An exception is logged and ignored.</param>
    /// <param name="onDisconnected">Called once when the server stops, if a client had initialized (the "disconnected" line). An exception is logged and ignored.</param>
    public McpServer(McpServerInfo info, IToolHost tools, TextWriter log, string? instructions = null, int maxQueue = 64,
        Action<SessionInfo>? onConnected = null, Action<SessionInfo>? onDisconnected = null, TimeSpan? drainLimit = null, TimeSpan? writeDrainLimit = null, McpServerOptions? options = null)
    {
        _drainLimit = drainLimit ?? DefaultDrainLimit;
        _writeDrainLimit = writeDrainLimit ?? DefaultWriteDrainLimit;
        _onConnected = onConnected;
        _onDisconnected = onDisconnected;
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _logPrefix = (options ?? new McpServerOptions()).LogPrefix;
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _instructions = instructions;
        _maxQueue = maxQueue < 1 ? throw new ArgumentOutOfRangeException(nameof(maxQueue)) : maxQueue;

        // A malformed tool schema must fail at startup (a test catches it), never in front of a client.
        foreach (ToolDefinition t in tools.Tools)
        {
            JsonDocument d;
            try { d = JsonDocument.Parse(t.InputSchemaJson, ParseOptions); }
            catch (JsonException ex) { throw new ArgumentException("The input schema of tool '" + t.Name + "' is not valid JSON: " + ex.Message, nameof(tools), ex); }
            if (d.RootElement.ValueKind != JsonValueKind.Object
                || !d.RootElement.TryGetProperty("type", out JsonElement ty) || ty.ValueKind != JsonValueKind.String || ty.GetString() != "object")
            {
                d.Dispose();
                throw new ArgumentException("The input schema of tool '" + t.Name + "' must be a JSON object with \"type\": \"object\".", nameof(tools));
            }
            _schemas[t.Name] = d;
        }
    }

    /// <summary>Serves until stdin closes (exit 0) or stdout can no longer be written (exit 1). Blocks.</summary>
    public int Run(Stream input, Stream output)
    {
        _transport = new LineTransport(input, output);
        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "mcp-worker" };
        var reader = new Thread(ReaderLoop) { IsBackground = true, Name = "mcp-reader" };
        worker.Start();
        reader.Start();
        _done.Wait();
        if (_initialized) Notify(_onDisconnected);
        return _exitCode;
    }

    /// <summary>The session as it is now (client, protocol), for the connected/disconnected callbacks.</summary>
    SessionInfo Session() => new(_sessionId, _client, _protocol);

    void Notify(Action<SessionInfo>? callback)
    {
        if (callback is null) return;
        try { callback(Session()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log("session callback failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    void Finish(int code)
    {
        if (Interlocked.CompareExchange(ref _finished, 1, 0) != 0) return;
        _exitCode = code;
        _done.Set();
    }

    void Send(byte[] message)
    {
        if (!_transport!.Write(message)) Finish(1);
    }

    void Log(string text) => StderrLog.Line(_log, text, _logPrefix);

    // ---- reader ----

    void ReaderLoop()
    {
        try
        {
            while (Volatile.Read(ref _finished) == 0)
            {
                ReadResult r = _transport!.Read();
                if (r.Kind == ReadKind.Eof) break;
                if (r.Kind == ReadKind.TooLarge)
                {
                    Log("discarded a message larger than 1 MiB");
                    Send(JsonRpc.Error(RpcId.Null, RpcCodes.InvalidRequest, "Message larger than 1 MiB was discarded."));
                    continue;
                }
                Handle(r.Line!);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("reader failed: " + ex);
            Finish(1);
            return;
        }

        // stdin closed: let the queued read requests finish (at most 10 s in all), then exit 0. [CP-R7] A queued request that WRITES and has not started is
        // dropped (no response: nobody is listening, and a change nobody can see should not start), and a write that is already running is waited for.
        lock (_queueLock)
        {
            _closed = true;
            DropQueued(writesOnly: true);
            Monitor.PulseAll(_queueLock);
        }
        _drainTimer = new Timer(_ => DrainExpired(), null, _drainLimit, Timeout.InfiniteTimeSpan);
    }

    bool IsWrite(string tool) => _tools is IWriteAwareToolHost h && h.IsWriteTool(tool);

    /// <summary>Removes queued requests that have not started (only the writes, or all of them). The caller holds <c>_queueLock</c>.</summary>
    void DropQueued(bool writesOnly)
    {
        for (LinkedListNode<Work>? n = _queue.First; n is not null;)
        {
            LinkedListNode<Work>? next = n.Next;
            if (!writesOnly || IsWrite(n.Value.Tool))
            {
                Log("dropped queued request " + Clip(n.Value.Tool) + " (the client closed the input)");
                n.Value.Doc.Dispose();
                _queue.Remove(n);
            }
            n = next;
        }
    }

    /// <summary>The drain time is up: exit now, unless a write is running, which gets <see cref="_writeDrainLimit"/> more (once) so it is never cut in half.</summary>
    void DrainExpired()
    {
        lock (_queueLock)
        {
            DropQueued(writesOnly: false);
            if (!_drainExtended && _running is { } r && IsWrite(r.Tool))
            {
                _drainExtended = true;
                Log("waiting for the running write " + Clip(r.Tool) + " to finish");
                _drainTimer?.Change(_writeDrainLimit, Timeout.InfiniteTimeSpan);
                Monitor.PulseAll(_queueLock);
                return;
            }
        }
        Finish(0);
    }

    void Handle(byte[] line)
    {
        if (!Utf8.IsValid(line))
        {
            Send(JsonRpc.Error(RpcId.Null, RpcCodes.ParseError, "Parse error: the message is not valid UTF-8."));
            return;
        }
        if (line.All(b => b is (byte)' ' or (byte)'\t')) return;   // a blank line carries nothing

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line, ParseOptions); }
        catch (JsonException ex)
        {
            string reason = ex.Message;
            int cut = reason.IndexOf(" LineNumber", StringComparison.Ordinal);
            if (cut > 0) reason = reason[..cut];
            if (reason.Length > 120) reason = reason[..120];
            Send(JsonRpc.Error(RpcId.Null, RpcCodes.ParseError, "Parse error: " + reason.Trim()));
            return;
        }

        bool kept = false;
        try { kept = Dispatch(doc.RootElement, doc); }
        finally { if (!kept) doc.Dispose(); }
    }

    /// <returns>True when the document was handed to the worker (which disposes it).</returns>
    bool Dispatch(JsonElement root, JsonDocument doc)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            Send(JsonRpc.Error(RpcId.Null, RpcCodes.InvalidRequest, "Batches are not supported; send one message per line."));
            return false;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            Send(JsonRpc.Error(RpcId.Null, RpcCodes.InvalidRequest, "Invalid request: expected a JSON object."));
            return false;
        }

        bool hasId = root.TryGetProperty("id", out JsonElement idEl);
        RpcId id = RpcId.Null;
        bool idOk = !hasId || RpcId.TryRead(idEl, out id);
        if (!idOk) id = RpcId.Null;

        if (!root.TryGetProperty("jsonrpc", out JsonElement ver) || ver.ValueKind != JsonValueKind.String || ver.GetString() != "2.0")
        {
            Send(JsonRpc.Error(id, RpcCodes.InvalidRequest, "Invalid request: jsonrpc must be \"2.0\"."));
            return false;
        }
        if (!idOk)
        {
            Send(JsonRpc.Error(RpcId.Null, RpcCodes.InvalidRequest, "Invalid request: id must be a string or an integer."));
            return false;
        }

        if (!root.TryGetProperty("method", out JsonElement methodEl) || methodEl.ValueKind != JsonValueKind.String)
        {
            if (hasId && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _))) return false;   // a response from the client: the server sends no requests
            Send(JsonRpc.Error(id, RpcCodes.InvalidRequest, "Invalid request: method is required."));
            return false;
        }
        string method = methodEl.GetString() ?? "";
        JsonElement prm = root.TryGetProperty("params", out JsonElement p) ? p : default;

        if (!hasId)
        {
            Notification(method, prm);
            return false;
        }

        switch (method)
        {
            case "ping":
                Send(JsonRpc.Result(id, w => { w.WriteStartObject(); w.WriteEndObject(); }));
                return false;
            case "initialize":
                Initialize(id, prm);
                return false;
        }

        if (!_initialized)
        {
            Send(JsonRpc.Error(id, RpcCodes.NotInitialized, "Server not initialized: send initialize first."));
            return false;
        }

        switch (method)
        {
            case "tools/list":
                Send(JsonRpc.Result(id, WriteToolList));
                return false;
            case "tools/call":
                return CallTool(id, prm, doc);
            default:
                Send(JsonRpc.Error(id, RpcCodes.MethodNotFound, "Method not found: " + Clip(method)));
                return false;
        }
    }

    static string Clip(string s) => s.Length <= 100 ? s : s[..100] + "...";

    /// <summary>A client-supplied name for a log line: control characters and runs of white space become one space, cut to 64 characters (never inside a surrogate pair).</summary>
    static string LogName(string raw)
    {
        var sb = new System.Text.StringBuilder();
        bool gap = false;
        foreach (char c in raw)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) { gap = sb.Length > 0; continue; }
            if (gap) { sb.Append(' '); gap = false; }
            sb.Append(c);
        }
        if (sb.Length == 0) return "unknown client";
        if (sb.Length > 64) sb.Length = char.IsHighSurrogate(sb[63]) ? 63 : 64;
        return sb.ToString();
    }

    void Notification(string method, JsonElement prm)
    {
        if (method == "notifications/cancelled"
            && prm.ValueKind == JsonValueKind.Object
            && prm.TryGetProperty("requestId", out JsonElement rid)
            && RpcId.TryRead(rid, out RpcId target))
        {
            Work? removed = null;
            lock (_queueLock)
            {
                for (LinkedListNode<Work>? n = _queue.First; n is not null; n = n.Next)
                {
                    if (n.Value.Id == target) { removed = n.Value; _queue.Remove(n); break; }
                }
            }
            // A running or finished request is not interrupted (best effort); its response may still be sent.
            if (removed is not null)
            {
                removed.Doc.Dispose();
                Log("cancelled queued request " + (target.Kind == RpcIdKind.String ? "\"" + Clip(target.Text ?? "") + "\"" : target.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        // notifications/initialized, anything else: nothing to do (tools are served from the initialize response on).
    }

    void Initialize(RpcId id, JsonElement prm)
    {
        if (_initialized)
        {
            Send(JsonRpc.Error(id, RpcCodes.InvalidRequest, "Already initialized."));
            return;
        }
        string? requested = null;
        string name = "", title = "", version = "";
        if (prm.ValueKind == JsonValueKind.Object)
        {
            if (prm.TryGetProperty("protocolVersion", out JsonElement pv) && pv.ValueKind == JsonValueKind.String) requested = pv.GetString();
            if (prm.TryGetProperty("clientInfo", out JsonElement ci) && ci.ValueKind == JsonValueKind.Object)
            {
                name = Str(ci, "name");
                title = Str(ci, "title");
                version = Str(ci, "version");
            }
        }
        _protocol = McpProtocol.Negotiate(requested);
        _client = new ClientIdentity(name, title.Length == 0 ? null : title, version.Length == 0 ? null : version);
        _initialized = true;
        Log("client " + LogName(_client.PreferredName) + (_client.Version is null ? "" : " " + Clip(_client.Version)) + " protocol " + _protocol + " session " + _sessionId);

        string protocol = _protocol;
        Send(JsonRpc.Result(id, w =>
        {
            w.WriteStartObject();
            w.WriteString("protocolVersion", protocol);
            w.WriteStartObject("capabilities");
            w.WriteStartObject("tools");
            w.WriteBoolean("listChanged", false);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("serverInfo");
            w.WriteString("name", _info.Name);
            w.WriteString("title", _info.Title);
            w.WriteString("version", _info.Version);
            w.WriteEndObject();
            if (_instructions is not null) w.WriteString("instructions", _instructions);
            w.WriteEndObject();
        }));
        Notify(_onConnected);   // after the answer, so a slow or blocked audit file never delays the handshake
    }

    static string Str(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    void WriteToolList(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");
        foreach (ToolDefinition t in _tools.Tools)
        {
            w.WriteStartObject();
            w.WriteString("name", t.Name);
            w.WriteString("title", t.Annotations.Title);
            w.WriteString("description", t.Description);
            w.WritePropertyName("inputSchema");
            _schemas[t.Name].RootElement.WriteTo(w);
            w.WriteStartObject("annotations");
            w.WriteString("title", t.Annotations.Title);
            w.WriteBoolean("readOnlyHint", t.Annotations.ReadOnly);
            w.WriteBoolean("destructiveHint", t.Annotations.Destructive);
            w.WriteBoolean("idempotentHint", t.Annotations.Idempotent);
            w.WriteBoolean("openWorldHint", t.Annotations.OpenWorld);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    bool CallTool(RpcId id, JsonElement prm, JsonDocument doc)
    {
        if (prm.ValueKind != JsonValueKind.Object
            || !prm.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(nameEl.GetString()))
        {
            Send(JsonRpc.Error(id, RpcCodes.InvalidParams, "Invalid params: name is required."));
            return false;
        }
        string name = nameEl.GetString()!;
        if (!_tools.Has(name))
        {
            Send(JsonRpc.Error(id, RpcCodes.InvalidParams, "Unknown tool: " + Clip(name) + ". Call tools/list to see the tools."));
            return false;
        }
        JsonElement args = EmptyObject;
        if (prm.TryGetProperty("arguments", out JsonElement a))
        {
            if (a.ValueKind != JsonValueKind.Object)
            {
                Send(JsonRpc.Error(id, RpcCodes.InvalidParams, "Invalid params: arguments must be an object."));
                return false;
            }
            args = a;
        }

        lock (_queueLock)
        {
            if (_queue.Count < _maxQueue)
            {
                _queue.AddLast(new Work(id, name, doc, args, _client, _protocol));
                Monitor.PulseAll(_queueLock);
                return true;
            }
        }
        Send(JsonRpc.Error(id, RpcCodes.Busy, "Server busy; retry."));
        return false;
    }

    // ---- worker ----

    void WorkerLoop()
    {
        while (Volatile.Read(ref _finished) == 0)
        {
            Work w;
            lock (_queueLock)
            {
                while (_queue.Count == 0 && !_closed) Monitor.Wait(_queueLock);
                if (_queue.Count == 0) break;
                w = _queue.First!.Value;
                _queue.RemoveFirst();
                _running = w;
            }
            try { Execute(w); }
            finally { lock (_queueLock) _running = null; }
        }
        Finish(0);
    }

    void Execute(Work w)
    {
        ToolResult result;
        try
        {
            result = _tools.Call(w.Tool, w.Arguments, new CallContext(w.Client, _sessionId, w.Protocol));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("tool " + w.Tool + " threw: " + ex);
            string message = _info.Title + " hit an unexpected error (" + ex.GetType().Name + "). Nothing was changed unless the job shows a new revision.";
            result = new ToolResult(true, message, new JsonObject { ["error"] = new JsonObject { ["code"] = "internal", ["message"] = message } });
        }

        try { Send(JsonRpc.Result(w.Id, jw => WriteToolResult(jw, result))); }
        finally { w.Doc.Dispose(); }
    }

    static void WriteToolResult(Utf8JsonWriter w, ToolResult r)
    {
        w.WriteStartObject();
        w.WriteStartArray("content");
        w.WriteStartObject();
        w.WriteString("type", "text");
        w.WriteString("text", r.Text);
        w.WriteEndObject();
        w.WriteEndArray();
        if (r.Structured is not null)
        {
            w.WritePropertyName("structuredContent");
            r.Structured.WriteTo(w);
        }
        w.WriteBoolean("isError", r.IsError);
        w.WriteEndObject();
    }
}
