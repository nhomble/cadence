using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.History;
using Cadence.Scheduler;

namespace Cadence.App.Cli;

/// <summary>Everything a headless command needs, injected so tests can aim it at \CadenceTest, a temp runs root and a fixed clock.</summary>
public sealed class CliEnv(ITaskSchedulerGateway gateway, RunRecordStore store, Func<DateTimeOffset> clock, TimeZoneInfo zone, Action<string> write)
{
    public ITaskSchedulerGateway Gateway { get; } = gateway;
    public RunRecordStore Store { get; } = store;
    public Func<DateTimeOffset> Clock { get; } = clock;
    public TimeZoneInfo Zone { get; } = zone;

    /// <summary>One line of output (CRLF added).</summary>
    public void Line(string text) => write(text + "\r\n");

    public void Raw(string text) => write(text);

    public string FolderPath => "\\" + Gateway.Options.FolderName;
}

/// <summary>Text and JSON shapes shared by --probe and --history. All status words are camelCase and come from run records, never from Task Scheduler's result code.</summary>
public static class CliFormat
{
    public static string StatusName(RunStatus s) => s switch
    {
        RunStatus.Ok => "ok",
        RunStatus.Failed => "failed",
        RunStatus.TimedOut => "timedOut",
        RunStatus.LaunchError => "launchError",
        RunStatus.Skipped => "skipped",
        RunStatus.Running => "running",
        RunStatus.NotReported => "notReported",
        _ => "unknown",
    };

    public static string OwnershipName(Ownership o) => o switch
    {
        Ownership.Managed => "managed",
        Ownership.Foreign => "foreign",
        Ownership.CorruptMetadata => "corruptMetadata",
        Ownership.NewerVersion => "newerVersion",
        _ => "unknown",
    };

    public static string SyncName(SyncStatus s) => s switch
    {
        SyncStatus.InSync => "inSync",
        SyncStatus.Drifted => "drifted",
        SyncStatus.Outdated => "outdated",
        _ => "notApplicable",
    };

    public static string CalendarName(HolidayCalendar c) => c switch
    {
        HolidayCalendar.UsFederal => "usFederal",
        HolidayCalendar.Nyse => "nyse",
        _ => "none",
    };

    /// <summary>The list badge: "skipped" when the newest row is a holiday skip (neutral, not a failure), else the last counted result, else "none".</summary>
    public static string Badge(JobHistory h)
    {
        if (h.Recent.Count > 0 && h.Recent[0].Status == RunStatus.Skipped) return "skipped";
        return h.Summary.LastResult is { } r ? StatusName(r) : "none";
    }

    public static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string? Iso(DateTime? local) =>
        local is { Year: >= 2000 } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Local)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) : null;

    public static string HResult(int? code) => code is { } c ? "0x" + ((uint)c).ToString("X8", CultureInfo.InvariantCulture) : "";

    public static long Ms(TimeSpan t) => (long)Math.Round(t.TotalMilliseconds);

    /// <summary>"850 ms", "1.2 s", "42 s", "2 min 5 s", "1 h 3 min".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 1) return ((int)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
        if (t.TotalSeconds < 10) return t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        if (t.TotalMinutes < 1) return ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
        if (t.TotalHours < 1) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min " + t.Seconds.ToString(CultureInfo.InvariantCulture) + " s";
        return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + " min";
    }

    /// <summary>Indented JSON, non-ASCII kept readable (UTF-8), no escaping of + ' &lt; &gt; &amp;.</summary>
    public static string Json(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            body(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void WriteSummary(Utf8JsonWriter w, JobSummary s)
    {
        w.WriteStartObject();
        Str(w, "lastResult", s.LastResult is { } r ? StatusName(r) : null);
        Str(w, "lastRunAt", s.LastRunAt is { } at ? Iso(at) : null);
        if (s.AverageRuntime is { } avg) w.WriteNumber("averageRuntimeMs", Ms(avg)); else w.WriteNull("averageRuntimeMs");
        if (s.SuccessRate is { } rate) w.WriteNumber("successRate", Math.Round(rate, 4)); else w.WriteNull("successRate");
        w.WriteNumber("sampleSize", s.SampleSize);
        w.WriteEndObject();
    }

    public static void WriteRun(Utf8JsonWriter w, RunView v)
    {
        w.WriteStartObject();
        w.WriteString("runId", v.RunId);
        w.WriteString("when", Iso(v.When));
        if (v.Duration is { } d) w.WriteNumber("durationMs", Ms(d)); else w.WriteNull("durationMs");
        w.WriteString("status", StatusName(v.Status));
        if (v.ExitCode is { } code) w.WriteNumber("exitCode", code); else w.WriteNull("exitCode");
        Str(w, "message", v.Message);
        w.WriteBoolean("hasOutput", v.HasOutput);
        w.WriteEndObject();
    }

    public static void Str(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    /// <summary>The <c>--history --json</c> document: <c>{summary: JobSummary, recent: RunView[]}</c>.</summary>
    public static string HistoryJson(JobHistory h) => Json(w =>
    {
        w.WriteStartObject();
        w.WritePropertyName("summary");
        WriteSummary(w, h.Summary);
        w.WriteStartArray("recent");
        foreach (RunView v in h.Recent) WriteRun(w, v);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>The human-readable <c>--history</c> table (CRLF lines).</summary>
    public static string HistoryText(string jobName, string jobId, JobHistory h)
    {
        var sb = new StringBuilder();
        JobSummary s = h.Summary;
        sb.Append(jobName).Append("  [").Append(jobId).Append("]\r\n");
        sb.Append("last result: ").Append(Badge(h));
        if (s.SuccessRate is { } rate)
            sb.Append("; success ").Append((rate * 100).ToString("0", CultureInfo.InvariantCulture)).Append("% of ").Append(s.SampleSize.ToString(CultureInfo.InvariantCulture)).Append(" run(s)");
        if (s.AverageRuntime is { } avg) sb.Append("; average ").Append(Duration(avg));
        sb.Append("\r\n");
        if (h.Recent.Count == 0) { sb.Append("no runs recorded yet\r\n"); return sb.ToString(); }

        sb.Append("When                       Status       Exit  Duration   Message\r\n");
        foreach (RunView v in h.Recent)
        {
            string row = v.When.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture).PadRight(26) + " "
                + StatusName(v.Status).PadRight(12) + " "
                + (v.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-").PadRight(5) + " "
                + (v.Duration is { } d ? Duration(d) : "-").PadRight(10) + " "
                + OneLine(v.Message);
            sb.Append(row.TrimEnd()).Append("\r\n");
        }
        return sb.ToString();
    }

    static string OneLine(string? text) =>
        (text ?? "").Replace("\r\n", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    /// <summary>The diff as plain ASCII text ("Time 18:00 -> 19:00").</summary>
    public static string Plain(FieldDiff d) => d.Describe().Replace("→", "->", StringComparison.Ordinal);
}
