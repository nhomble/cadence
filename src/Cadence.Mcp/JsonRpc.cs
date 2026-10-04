using System.Buffers;
using System.Text.Json;

namespace Cadence.Mcp;

/// <summary>A JSON-RPC request id: a string or an integer (echoed back with the same type and value), or null (an error that cannot name its request).</summary>
internal readonly record struct RpcId(RpcIdKind Kind, string? Text, long Number)
{
    public static readonly RpcId Null = new(RpcIdKind.Null, null, 0);

    public static RpcId FromString(string s) => new(RpcIdKind.String, s, 0);

    public static RpcId FromNumber(long n) => new(RpcIdKind.Number, null, n);

    /// <summary>True and the id for a string or an integer; false for anything else (null, fractions, objects, arrays, booleans).</summary>
    public static bool TryRead(JsonElement e, out RpcId id)
    {
        id = Null;
        switch (e.ValueKind)
        {
            case JsonValueKind.String: id = FromString(e.GetString() ?? ""); return true;
            case JsonValueKind.Number when e.TryGetInt64(out long n): id = FromNumber(n); return true;
            default: return false;
        }
    }

    public void Write(Utf8JsonWriter w)
    {
        switch (Kind)
        {
            case RpcIdKind.String: w.WriteString("id", Text); break;
            case RpcIdKind.Number: w.WriteNumber("id", Number); break;
            default: w.WriteNull("id"); break;
        }
    }
}

internal enum RpcIdKind { Null, String, Number }

/// <summary>JSON-RPC 2.0 error codes used by the server.</summary>
internal static class RpcCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int NotInitialized = -32000;
    public const int Busy = -32001;
}

/// <summary>Builds the response lines (UTF-8, compact, default encoder: control characters, U+2028 and non-ASCII are always escaped, so no raw line break can appear).</summary>
internal static class JsonRpc
{
    public static byte[] Error(RpcId id, int code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            id.Write(w);
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A success response; <paramref name="writeResult"/> writes the value of "result".</summary>
    public static byte[] Result(RpcId id, Action<Utf8JsonWriter> writeResult)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            id.Write(w);
            w.WritePropertyName("result");
            writeResult(w);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
