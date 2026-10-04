using System.Text.Json;
using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class MetadataCodecTests
{
    static CadenceMetadata Meta(JobDefinition job, SkipList? skip = null) =>
        new(1, 1, 1, job, skip, "3f9c0a1b2c3d4e5f", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 10, 2, 10, 30, 15, TimeSpan.FromHours(-4)), ValueList.Of("Old name", "Older (2)"));

    static CadenceMetadata StockMeta()
    {
        var job = ComposeSamples.StockIngest();
        var skip = new SkipList(HolidayCalendar.Nyse, new DateOnly(2036, 12, 31), HolidayRules.Range(HolidayCalendar.Nyse, 2026, 2036));
        return Meta(job, skip);
    }

    static string JsonOf(string encoded) => encoded[(encoded.IndexOf('\n', StringComparison.Ordinal) + 1)..];

    public static void RoundTripStock()
    {
        var m = StockMeta();
        string text = MetadataCodec.Encode(m);
        T.True("starts with the marker line", text.StartsWith("cadence/1\n{\"schema\":1,\"gen\":1,\"wv\":1,\"id\":\"d4dst0ck\",\"name\":\"Stock ingest (Dad)\"", StringComparison.Ordinal));
        T.True("single JSON line", !JsonOf(text).Contains('\n', StringComparison.Ordinal) && !JsonOf(text).Contains('\r', StringComparison.Ordinal));
        T.Eq("decode problem", MetadataProblem.None, MetadataCodec.TryDecode(text, out var back));
        T.True("decoded equals original", m.Equals(back));
        T.Eq("re-encode is byte for byte identical", text, MetadataCodec.Encode(back!));
        T.True("under the cap", text.Length < MetadataCodec.MaxEncodedLength);
    }

    // Strings that stress JSON, XML, and the wrapper's PowerShell handling.
    static readonly string[] Awkward =
    [
        "plain",
        "",
        "with \"double quotes\" and 'single' ones",
        "100% done %TEMP% %%",
        "a & b < c > d && e | f ^ g",
        "back\\slash \\\\ trailing\\",
        "unicode: h\u00e9llo \u65e5\u672c\u8a9e \u2713 \U0001F600 \u00df",
        "line1\r\nline2\nline3\rline4",
        "tab\tseparated\u0001ctrl\u001f",
        "json-ish {\"a\":[1,2],\"b\":\"c\"} and \\u0041 \\n",
        "xml-ish <![CDATA[x]]> &amp; &#10; ]]> -->",
        "$(whoami) `backtick` $env:PATH ; & calc",
        "  leading and trailing spaces  ",
        "\u2028 \u2029 line separators",
    ];

    public static void AwkwardStringsRoundTripByteForByte()
    {
        foreach (string s in Awkward)
        {
            string label = "[" + s.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "] " + s.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            var job = ComposeSamples.StockIngest() with { Name = s.Length == 0 ? "x" : s };
            job = job with
            {
                Run = job.Run with
                {
                    Exe = "C:\\p " + s + ".exe",
                    Args = s,
                    WorkingDirectory = "C:\\w " + s,
                    SourcePath = s,
                    Env = ValueList.Of(new KeyValuePair<string, string>("K_" + s.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), s)),
                },
            };
            var m = Meta(job);
            string text = MetadataCodec.Encode(m);
            T.True(label + ": one line", !JsonOf(text).Contains('\n', StringComparison.Ordinal) && !JsonOf(text).Contains('\r', StringComparison.Ordinal));
            T.Eq(label + ": decode", MetadataProblem.None, MetadataCodec.TryDecode(text, out var back));
            T.True(label + ": model equal", m.Equals(back));
            T.Eq(label + ": args exact", s, back?.Job.Run.Args);
            T.Eq(label + ": re-encode identical", text, MetadataCodec.Encode(back!));

            // Through the Task XML writer and reader (entitized CR/LF, attributes, escaping), as a task would store it.
            var model = new TaskDefinitionModel(new RegistrationInfoModel("Cadence", "d", text, "Cadence", null, null), ValueList<TaskTrigger>.Empty,
                new PrincipalModel(null, "InteractiveToken", "LeastPrivilege"), new SettingsModel(),
                ValueList.Of(new ExecActionModel("x.exe", null, null)), ValueList<string>.Empty);
            var read = TaskXmlReader.Read(TaskXmlWriter.Write(model));
            T.Eq(label + ": Documentation survives the XML round trip", text, read.RegistrationInfo.Documentation);
        }
    }

    public static void TolerantReading()
    {
        string text = MetadataCodec.Encode(StockMeta());
        string crlf = text.Replace("\n", "\r\n", StringComparison.Ordinal);   // marker CRLF, JSON has no raw newlines
        T.Eq("CRLF after the marker", MetadataProblem.None, MetadataCodec.TryDecode(crlf, out var a));
        T.True("CRLF decodes to the same model", StockMeta().Equals(a));
        T.Eq("trailing newline", MetadataProblem.None, MetadataCodec.TryDecode(text + "\r\n", out _));
        T.Eq("leading blank in the marker line is trimmed", MetadataProblem.None, MetadataCodec.TryDecode(" " + text, out _));
    }

    public static void Problems()
    {
        string good = MetadataCodec.Encode(StockMeta());
        string json = JsonOf(good);
        (string? Text, MetadataProblem Expected, string Label)[] table =
        [
            (null, MetadataProblem.Missing, "null"),
            ("", MetadataProblem.Missing, "empty"),
            (" \r\n ", MetadataProblem.Missing, "whitespace"),
            ("Runs the nightly backup", MetadataProblem.NotCadence, "plain description"),
            ("{\"a\":1}", MetadataProblem.NotCadence, "json without marker"),
            ("cadence/2\n{}", MetadataProblem.NewerSchema, "cadence/2 marker"),
            ("cadence/9\r\n{\"anything\":true}", MetadataProblem.NewerSchema, "cadence/9 marker, CRLF"),
            ("cadence/1\n" + json.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal), MetadataProblem.NewerSchema, "schema 2 in JSON"),
            ("cadence/1\n{\"schema\":2,\"future\":[1,2,3]}", MetadataProblem.NewerSchema, "schema 2, unknown shape"),
            ("cadence/1", MetadataProblem.Malformed, "marker only"),
            ("cadence/1\n", MetadataProblem.Malformed, "marker, empty JSON"),
            ("cadence/1\nnot json", MetadataProblem.Malformed, "garbage"),
            ("cadence/1\n[1,2]", MetadataProblem.Malformed, "array"),
            ("cadence/1\n{}", MetadataProblem.Malformed, "empty object (no schema)"),
            ("cadence/1\n{\"schema\":1}", MetadataProblem.Malformed, "schema only"),
            ("cadence/1\n" + json[..^5], MetadataProblem.Malformed, "truncated"),
            ("cadence/x\n{}", MetadataProblem.Malformed, "non-numeric marker"),
            ("cadence/0\n{}", MetadataProblem.Malformed, "cadence/0"),
            ("cadence/\n{}", MetadataProblem.Malformed, "cadence/ with no number"),
            ("cadence/1\n" + json.Replace("\"id\":\"d4dst0ck\"", "\"id\":\"BAD\"", StringComparison.Ordinal), MetadataProblem.Malformed, "bad id"),
            ("cadence/1\n" + json.Replace("\"kind\":\"runProgram\"", "\"kind\":\"other\"", StringComparison.Ordinal), MetadataProblem.Malformed, "bad kind"),
            ("cadence/1\n" + json.Replace("\"capture\":true", "\"capture\":false", StringComparison.Ordinal), MetadataProblem.Malformed, "capture != hidden"),
            ("cadence/1\n" + json.Replace("\"encoding\":\"utf8\"", "\"encoding\":\"latin1\"", StringComparison.Ordinal), MetadataProblem.Malformed, "bad encoding"),
            ("cadence/1\n" + json.Replace("\"skipHolidays\":\"nyse\"", "\"skipHolidays\":\"mars\"", StringComparison.Ordinal), MetadataProblem.Malformed, "bad skipHolidays token"),
            ("cadence/1\n" + json.Replace("\"anchor\":\"2026-10-01\"", "\"anchor\":\"2026-13-01\"", StringComparison.Ordinal), MetadataProblem.Malformed, "bad anchor"),
            ("cadence/1\n" + json.Replace("\"until\":\"2036-12-31\"", "\"until\":\"soon\"", StringComparison.Ordinal), MetadataProblem.Malformed, "bad skip.until"),
            ("cadence/1\n" + json.Replace("\"2026-11-26\"", "\"26/11/2026\"", StringComparison.Ordinal), MetadataProblem.Malformed, "bad skip date key"),
            ("cadence/1\n" + json.Replace("\"timeoutSec\":1800", "\"timeoutSec\":-1", StringComparison.Ordinal), MetadataProblem.Malformed, "negative timeout"),
            ("cadence/1\n" + json.Replace("\"cron\":[\"0 18 * * 1-5\"]", "\"cron\":[]", StringComparison.Ordinal), MetadataProblem.Malformed, "no cron lines"),
            ("cadence/1\n" + json.Replace("\"cron\":[\"0 18 * * 1-5\"]", "\"cron\":\"0 18 * * 1-5\"", StringComparison.Ordinal), MetadataProblem.Malformed, "cron is not an array"),
            ("cadence/1\n" + json.Replace("\"created\":\"2026-10-01T09:00:00-04:00\"", "\"created\":\"yesterday\"", StringComparison.Ordinal), MetadataProblem.Malformed, "bad created"),
            ("cadence/1\n" + json.Replace("\"env\":{\"PYTHONIOENCODING\":\"utf-8\"}", "\"env\":[]", StringComparison.Ordinal), MetadataProblem.Malformed, "env is not an object"),
        ];
        foreach (var (text, expected, label) in table)
        {
            T.Eq(label, expected, MetadataCodec.TryDecode(text, out var m));
            T.True(label + ": metadata is null", m is null);   // a failure never hands out a partial model
        }

        // Sanity: the replacements really changed the text (otherwise a row proves nothing).
        foreach (var (text, _, label) in table)
            if (label is not ("null" or "empty" or "whitespace")) T.True(label + ": differs from the good text", text != good);
    }

    public static void WrapperContractPropertyNames()
    {
        string text = MetadataCodec.Encode(StockMeta());
        using var doc = JsonDocument.Parse(JsonOf(text));
        var root = doc.RootElement;
        T.Eq("id", "d4dst0ck", root.GetProperty("id").GetString());
        var run = root.GetProperty("run");
        T.Eq("run.exe", @"C:\Windows\py.exe", run.GetProperty("exe").GetString());
        T.Eq("run.args", "-u \"C:\\Users\\Dad\\stocks\\ingest.py\"", run.GetProperty("args").GetString());
        T.Eq("run.cwd", @"C:\Users\Dad\stocks", run.GetProperty("cwd").GetString());
        T.Eq("run.env kind", JsonValueKind.Object, run.GetProperty("env").ValueKind);
        T.Eq("run.env value", "utf-8", run.GetProperty("env").GetProperty("PYTHONIOENCODING").GetString());
        T.Eq("run.timeoutSec", 1800, run.GetProperty("timeoutSec").GetInt32());
        T.Eq("run.hidden", true, run.GetProperty("hidden").GetBoolean());
        T.Eq("run.capture", true, run.GetProperty("capture").GetBoolean());
        T.Eq("run.encoding", "utf8", run.GetProperty("encoding").GetString());
        T.Eq("run.source", @"C:\Users\Dad\stocks\ingest.py", run.GetProperty("source").GetString());
        var skip = run.GetProperty("skip");
        T.Eq("run.skip.cal", "nyse", skip.GetProperty("cal").GetString());
        T.Eq("run.skip.until", "2036-12-31", skip.GetProperty("until").GetString());
        T.Eq("run.skip.dates kind (object)", JsonValueKind.Object, skip.GetProperty("dates").ValueKind);
        T.Eq("run.skip.dates Thanksgiving", "Thanksgiving Day", skip.GetProperty("dates").GetProperty("2026-11-26").GetString());
        var sched = root.GetProperty("schedule");
        T.Eq("schedule.cron is an array", JsonValueKind.Array, sched.GetProperty("cron").ValueKind);
        T.Eq("schedule.cron[0]", "0 18 * * 1-5", sched.GetProperty("cron")[0].GetString());
        T.Eq("schedule.skipHolidays", "nyse", sched.GetProperty("skipHolidays").GetString());
        T.Eq("schedule.anchor", "2026-10-01", sched.GetProperty("anchor").GetString());
        T.Eq("fp", "3f9c0a1b2c3d4e5f", root.GetProperty("fp").GetString());
        T.Eq("created", "2026-10-01T09:00:00-04:00", root.GetProperty("created").GetString());
        T.Eq("updated", "2026-10-02T10:30:15-04:00", root.GetProperty("updated").GetString());
        T.Eq("prev", 2, root.GetProperty("prev").GetArrayLength());
        T.Eq("settings.wakeToRun", false, root.GetProperty("settings").GetProperty("wakeToRun").GetBoolean());
    }

    public static void OptionalMembersAreOmitted()
    {
        var job = ComposeSamples.StockIngest(HolidayCalendar.None);
        job = job with { Run = job.Run with { WorkingDirectory = null, SourcePath = null, Timeout = null, Env = ValueList<KeyValuePair<string, string>>.Empty, Hidden = false } };
        string text = MetadataCodec.Encode(Meta(job));
        using var doc = JsonDocument.Parse(JsonOf(text));
        var run = doc.RootElement.GetProperty("run");
        T.True("no cwd", !run.TryGetProperty("cwd", out _));
        T.True("no source", !run.TryGetProperty("source", out _));
        T.True("no skip", !run.TryGetProperty("skip", out _));
        T.Eq("timeoutSec 0 means none", 0, run.GetProperty("timeoutSec").GetInt32());
        T.Eq("env is still an object", JsonValueKind.Object, run.GetProperty("env").ValueKind);
        T.Eq("capture follows hidden", false, run.GetProperty("capture").GetBoolean());
        T.Eq("skipHolidays none", "none", doc.RootElement.GetProperty("schedule").GetProperty("skipHolidays").GetString());
        T.Eq("decodes", MetadataProblem.None, MetadataCodec.TryDecode(text, out var back));
        T.True("round trip", Meta(job).Equals(back));
    }

    public static void TimeoutRounding()
    {
        (TimeSpan? Timeout, int Seconds)[] table =
        [
            (null, 0), (TimeSpan.Zero, 0), (TimeSpan.FromSeconds(-5), 0), (TimeSpan.FromSeconds(1), 1),
            (TimeSpan.FromMilliseconds(1), 1), (TimeSpan.FromMilliseconds(1500), 2), (TimeSpan.FromMinutes(30), 1800), (TimeSpan.FromHours(100), 360000),
        ];
        foreach (var (t, s) in table)
            T.Eq("timeout " + (t?.ToString() ?? "null"), s, MetadataCodec.TimeoutSeconds(new RunSpec { Exe = "x", Timeout = t }));
    }

    public static void EncodeRejectsBadInput()
    {
        var m = StockMeta();
        T.Throws<ArgumentException>("schema 2 cannot be written", () => MetadataCodec.Encode(m with { Schema = 2 }));
        var dupEnv = m.Job with { Run = m.Job.Run with { Env = ValueList.Of(new KeyValuePair<string, string>("A", "1"), new KeyValuePair<string, string>("a", "2")) } };
        T.Throws<ArgumentException>("duplicate env names (case-insensitive)", () => MetadataCodec.Encode(m with { Job = dupEnv }));
        var dupDate = m with { Skip = new SkipList(HolidayCalendar.Nyse, new DateOnly(2036, 12, 31), ValueList.Of(new Holiday(new DateOnly(2026, 1, 1), "A"), new Holiday(new DateOnly(2026, 1, 1), "B"))) };
        T.Throws<ArgumentException>("duplicate holiday date", () => MetadataCodec.Encode(dupDate));
        var big = m.Job with { Run = m.Job.Run with { Args = new string('x', MetadataCodec.MaxEncodedLength) } };
        T.Throws<InvalidOperationException>("over the 16 KB cap", () => MetadataCodec.Encode(m with { Job = big }));
        var nearCap = m.Job with { Run = m.Job.Run with { Args = new string('x', 8000) } };
        T.True("8 KB of args plus the holiday list still fits", MetadataCodec.Encode(m with { Job = nearCap }).Length < MetadataCodec.MaxEncodedLength);
        string oversized = "cadence/1\n" + new string(' ', MetadataCodec.MaxEncodedLength + 10) + "{}";
        T.Eq("decoding oversized text is Malformed", MetadataProblem.Malformed, MetadataCodec.TryDecode(oversized, out _));
    }

    // [CP-A] Encode used to accept what TryDecode (or the wrapper) then rejects: a 101-character name saved fine and the task read
    // back as CorruptMetadata; an empty or '=' environment name broke ConvertFrom-Json / the environment block at run time.
    public static void EncodeRejectsWhatCannotBeReadBack()
    {
        var m = StockMeta();
        CadenceMetadata With(Func<RunSpec, RunSpec> f) => m with { Job = m.Job with { Run = f(m.Job.Run) } };
        T.Throws<ArgumentException>("name of 101 characters", () => MetadataCodec.Encode(m with { Job = m.Job with { Name = new string('n', 101) } }));
        T.Throws<ArgumentException>("empty name", () => MetadataCodec.Encode(m with { Job = m.Job with { Name = "" } }));
        T.True("name of 100 characters is fine", MetadataCodec.Encode(m with { Job = m.Job with { Name = new string('n', 100) } }).Length > 0);
        T.Throws<ArgumentException>("no cron lines", () => MetadataCodec.Encode(m with { Job = m.Job with { Schedule = m.Job.Schedule with { CronLines = ValueList<string>.Empty } } }));
        T.Throws<ArgumentException>("blank program", () => MetadataCodec.Encode(With(r => r with { Exe = "  " })));
        T.Throws<ArgumentException>("empty env name", () => MetadataCodec.Encode(With(r => r with { Env = ValueList.Of(new KeyValuePair<string, string>("", "1")) })));
        T.Throws<ArgumentException>("env name with '='", () => MetadataCodec.Encode(With(r => r with { Env = ValueList.Of(new KeyValuePair<string, string>("A=B", "1")) })));
        T.Throws<ArgumentException>("env value with NUL", () => MetadataCodec.Encode(With(r => r with { Env = ValueList.Of(new KeyValuePair<string, string>("A", "1\02")) })));
        T.Throws<ArgumentException>("args with NUL", () => MetadataCodec.Encode(With(r => r with { Args = "a\0b" })));
        T.Throws<ArgumentException>("working folder with NUL", () => MetadataCodec.Encode(With(r => r with { WorkingDirectory = "C:\\a\0b" })));
        T.Throws<ArgumentException>("timeout over 24 days", () => MetadataCodec.Encode(With(r => r with { Timeout = TimeSpan.FromDays(25) })));
        T.True("timeout of exactly 24 days is fine", MetadataCodec.Encode(With(r => r with { Timeout = TimeSpan.FromSeconds(MetadataCodec.MaxTimeoutSeconds) })).Length > 0);
        T.True("env value with '=' and % is fine", MetadataCodec.Encode(With(r => r with { Env = ValueList.Of(new KeyValuePair<string, string>("A", "x=%PATH%")) })).Length > 0);
        T.Throws<ComposeException>("the composer reports it as a ComposeException", () => TaskComposer.Compose(ComposeSamples.StockIngest() with { Name = new string('n', 101) }, ComposeSamples.Context()));
    }

    public static void ValueListConverter()
    {
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new ValueListJsonConverter<string>());
        options.Converters.Add(new ValueListJsonConverter<int>());
        T.Eq("strings to JSON", "[\"a\",\"b \\\"q\\\"\"]", JsonSerializer.Serialize(ValueList.Of("a", "b \"q\""), options));
        T.Eq("empty to JSON", "[]", JsonSerializer.Serialize(ValueList<string>.Empty, options));
        T.True("strings from JSON", ValueList.Of("x", "y").Equals(JsonSerializer.Deserialize<ValueList<string>>("[\"x\",\"y\"]", options)));
        T.True("ints from JSON", ValueList.Of(1, 2, 3).Equals(JsonSerializer.Deserialize<ValueList<int>>("[1, 2, 3]", options)));
        T.Throws<JsonException>("object instead of array", () => JsonSerializer.Deserialize<ValueList<string>>("{}", options));
        T.Throws<JsonException>("null element", () => JsonSerializer.Deserialize<ValueList<string>>("[\"a\",null]", options));
        T.Throws<JsonException>("unterminated", () => JsonSerializer.Deserialize<ValueList<string>>("[\"a\"", options));
    }

    public static void HolidayListInMetadataMatchesRules()
    {
        foreach (var cal in new[] { HolidayCalendar.Nyse, HolidayCalendar.UsFederal })
        {
            var job = ComposeSamples.StockIngest(cal);
            var skip = new SkipList(cal, new DateOnly(2036, 12, 31), HolidayRules.Range(cal, 2026, 2036));
            string text = MetadataCodec.Encode(Meta(job, skip));
            T.Eq(cal + ": decode", MetadataProblem.None, MetadataCodec.TryDecode(text, out var back));
            T.True(cal + ": skip list round trips", skip.Equals(back?.Skip));
            T.True(cal + ": 3-9 KB of metadata", text.Length is > 3000 and < 9000, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
