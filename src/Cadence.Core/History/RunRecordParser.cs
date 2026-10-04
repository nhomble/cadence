using System.Globalization;
using System.Text.Json;

namespace Cadence.Core.History;

/// <summary>
/// Reads the wrapper's JSON-lines run records (PLAN Appendix A). Tolerant by design: a truncated, garbled, blank or foreign line gives null
/// (never an exception), unknown properties and a newer <c>v</c> are ignored, and missing optional fields become null/0.
/// </summary>
public static class RunRecordParser
{
    /// <summary>Longest line considered (a record is about 400 characters; a long exception message is the only way to grow).</summary>
    public const int MaxLineLength = 1 << 20;

    /// <summary>
    /// Parses one record. Requires a JSON object with a <c>runId</c>, a parseable <c>wrapperStart</c> and a known <c>status</c>
    /// (ok, fail, timeout, launchError, skipped); anything else returns null. <paramref name="jobIdFromFolder"/> is the folder the line was
    /// read from (<c>_unknown</c> for records the wrapper could not attribute).
    /// </summary>
    public static RunRecord? ParseLine(string jsonLine, string jobIdFromFolder)
    {
        if (jsonLine is null) return null;
        string line = jsonLine.Trim('\r', '\n', ' ', '\t', '﻿', '\0');
        if (line.Length == 0 || line.Length > MaxLineLength || line[0] != '{') return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? runId = Str(root, "runId");
            if (string.IsNullOrWhiteSpace(runId)) return null;
            DateTimeOffset? wrapperStart = Time(root, "wrapperStart");
            if (wrapperStart is null) return null;
            RunStatus? status = Status(Str(root, "status"));
            if (status is null) return null;

            return new RunRecord(
                jobIdFromFolder,
                Str(root, "task") ?? "",
                runId,
                wrapperStart.Value,
                Time(root, "start"),
                Time(root, "end"),
                Int(root, "exit"),
                status.Value,
                Int(root, "pid"),
                Long(root, "outBytes") ?? 0,
                Long(root, "errBytes") ?? 0,
                Str(root, "msg") is { Length: > 0 } m ? m : null);
        }
        catch (JsonException)
        {
            return null;   // truncated mid-append or garbled
        }
    }

    /// <summary>
    /// Parses a block of text that may mix LF and CRLF line ends (the wrapper appends LF; its rotation rewrites the file with CRLF). Blank
    /// lines, garbled lines and a partial last line are skipped. Records come back in file order.
    /// </summary>
    public static List<RunRecord> ParseText(string text, string jobIdFromFolder)
    {
        var list = new List<RunRecord>();
        int pos = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            if (end > pos)
            {
                RunRecord? r = ParseLine(text.Substring(pos, end - pos), jobIdFromFolder);
                if (r is not null) list.Add(r);
            }
            if (nl < 0) break;
            pos = nl + 1;
        }
        return list;
    }

    /// <summary>The wrapper's status token to <see cref="RunStatus"/>; null for a token this version does not know.</summary>
    public static RunStatus? Status(string? token) => token switch
    {
        null => null,
        _ when token.Equals("ok", StringComparison.OrdinalIgnoreCase) => RunStatus.Ok,
        _ when token.Equals("fail", StringComparison.OrdinalIgnoreCase) => RunStatus.Failed,
        _ when token.Equals("timeout", StringComparison.OrdinalIgnoreCase) => RunStatus.TimedOut,
        _ when token.Equals("launchError", StringComparison.OrdinalIgnoreCase) => RunStatus.LaunchError,
        _ when token.Equals("skipped", StringComparison.OrdinalIgnoreCase) => RunStatus.Skipped,
        _ => null,
    };

    static JsonElement? Prop(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined ? v : null;

    static string? Str(JsonElement o, string name)
    {
        JsonElement? v = Prop(o, name);
        if (v is null) return null;
        return v.Value.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.GetRawText(),
            _ => null,
        };
    }

    static DateTimeOffset? Time(JsonElement o, string name)
    {
        string? s = Str(o, name);
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t) ? t : null;
    }

    static long? Long(JsonElement o, string name)
    {
        JsonElement? v = Prop(o, name);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number)
        {
            if (v.Value.TryGetInt64(out long l)) return l;
            return v.Value.TryGetDouble(out double d) && d >= long.MinValue && d <= long.MaxValue ? (long)d : null;
        }
        if (v.Value.ValueKind == JsonValueKind.String && long.TryParse(v.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long p)) return p;
        return null;
    }

    /// <summary>Exit codes are 32-bit; an NTSTATUS such as 0xC000013A may arrive as an unsigned number and is wrapped to its signed form.</summary>
    static int? Int(JsonElement o, string name)
    {
        long? l = Long(o, name);
        if (l is null) return null;
        if (l is >= int.MinValue and <= int.MaxValue) return (int)l.Value;
        return l is > int.MaxValue and <= uint.MaxValue ? unchecked((int)(uint)l.Value) : null;
    }
}
