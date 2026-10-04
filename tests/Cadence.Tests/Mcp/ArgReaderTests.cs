using System.Text.Json;
using Cadence.Agent.Tools;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>Strict argument parsing (PLAN 12.3): unknown properties refused, field paths in every message, anchored patterns.</summary>
public static class ArgReaderTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static string? Problem(Action body)
    {
        try { body(); return null; }
        catch (ArgumentProblem p) { return p.Message; }
    }

    public static void AbsentNullAndEmptyArgumentsAreNoArguments()
    {
        T.True("undefined", Problem(() => ArgReader.Open(default)) is null);
        T.True("null", Problem(() => ArgReader.Open(J("null"))) is null);
        T.True("{}", Problem(() => ArgReader.Open(J("{}"))) is null);
        T.True("a value of a known name works", Problem(() => ArgReader.Open(J("{\"a\":1}"), "a")) is null);
    }

    public static void UnknownPropertiesAreRefusedWithTheAllowedNames()
    {
        string? m = Problem(() => ArgReader.Open(J("{\"jobID\":\"x\"}"), "jobId", "recentRuns"));
        T.True("names the property (" + m + ")", m is not null && m.Contains("'jobID'", StringComparison.Ordinal));
        T.True("lists what is accepted", m is not null && m.Contains("jobId, recentRuns", StringComparison.Ordinal));
        string? none = Problem(() => ArgReader.Open(J("{\"x\":1}")));
        T.True("a no-argument tool says so", none is not null && none.Contains("takes no arguments", StringComparison.Ordinal));
        string? notObject = Problem(() => ArgReader.Open(J("[1]"), "a"));
        T.True("not an object", notObject is not null && notObject.Contains("JSON object", StringComparison.Ordinal));
        string? longName = Problem(() => ArgReader.Open(J("{\"" + new string('x', 500) + "\":1}"), "a"));
        T.True("a very long name is shortened in the message", longName is not null && longName.Length < 200);
    }

    public static void StringRules()
    {
        string? v = null;
        T.True("required missing", Problem(() => ArgReader.Open(J("{}"), "jobId").String("jobId", required: true, help: ArgReader.JobIdHelp))?.StartsWith("jobId is required", StringComparison.Ordinal) == true);
        T.True("optional missing is null", Problem(() => v = ArgReader.Open(J("{}"), "q").String("q")) is null && v is null);
        T.True("wrong type names the field", Problem(() => ArgReader.Open(J("{\"q\":5}"), "q").String("q"))?.StartsWith("q must be a string", StringComparison.Ordinal) == true);
        T.True("too long", Problem(() => ArgReader.Open(J("{\"q\":\"" + new string('a', 11) + "\"}"), "q").String("q", max: 10))?.Contains("at most 10", StringComparison.Ordinal) == true);
        T.True("too short", Problem(() => ArgReader.Open(J("{\"q\":\"a\"}"), "q").String("q", min: 3, max: 10))?.Contains("3 to 10", StringComparison.Ordinal) == true);
        T.True("null value refused", Problem(() => ArgReader.Open(J("{\"q\":null}"), "q").String("q"))?.Contains("must not be null", StringComparison.Ordinal) == true);

        string? bad = Problem(() => ArgReader.Open(J("{\"jobId\":\"D4DST0CK\"}"), "jobId").String("jobId", min: 8, max: 8, pattern: ArgReader.JobIdPattern(), help: ArgReader.JobIdHelp));
        T.True("pattern: names the field and says how to fix it (" + bad + ")", bad is not null && bad.StartsWith("jobId must be a job id", StringComparison.Ordinal) && bad.Contains("cadence_list_jobs", StringComparison.Ordinal));
        T.True("pattern is anchored at the end (a trailing newline is refused)",
            Problem(() => ArgReader.Open(J("{\"jobId\":\"d4dst0ck\\n\"}"), "jobId").String("jobId", pattern: ArgReader.JobIdPattern(), help: ArgReader.JobIdHelp)) is not null);
        T.True("a valid id passes", Problem(() => v = ArgReader.Open(J("{\"jobId\":\"d4dst0ck\"}"), "jobId").String("jobId", pattern: ArgReader.JobIdPattern())) is null && v == "d4dst0ck");
    }

    public static void ChoiceRules()
    {
        string[] values = ["stdout", "stderr"];
        string? v = "x";
        T.True("absent is null", Problem(() => v = ArgReader.Open(J("{}"), "stream").Choice("stream", values)) is null && v is null);
        T.True("valid", Problem(() => v = ArgReader.Open(J("{\"stream\":\"stderr\"}"), "stream").Choice("stream", values)) is null && v == "stderr");
        string? m = Problem(() => ArgReader.Open(J("{\"stream\":\"STDOUT\"}"), "stream").Choice("stream", values));
        T.True("case matters and the message lists the values (" + m + ")", m == "stream must be one of: stdout, stderr.");
        T.True("a number is refused", Problem(() => ArgReader.Open(J("{\"stream\":1}"), "stream").Choice("stream", values)) is not null);
    }

    public static void IntegerRules()
    {
        int n = 0;
        T.True("absent gives the default", Problem(() => n = ArgReader.Open(J("{}"), "c").Int("c", 1, 20, 8)) is null && n == 8);
        T.True("in range", Problem(() => n = ArgReader.Open(J("{\"c\":20}"), "c").Int("c", 1, 20, 8)) is null && n == 20);
        T.True("a whole number written as 5.0 is accepted", Problem(() => n = ArgReader.Open(J("{\"c\":5.0}"), "c").Int("c", 1, 20, 8)) is null && n == 5);
        string[] bad = ["0", "21", "-1", "1.5", "\"5\"", "true", "1e400", "99999999999999999999"];
        foreach (string b in bad)
        {
            string? m = Problem(() => ArgReader.Open(J("{\"c\":" + b + "}"), "c").Int("c", 1, 20, 8));
            T.True("refused: " + b + " (" + m + ")", m == "c must be a whole number from 1 to 20.");
        }
    }

    public static void StringArrayRules()
    {
        IReadOnlyList<string>? list = null;
        T.True("absent is null", Problem(() => list = ArgReader.Open(J("{}"), "cron").StringArray("cron", 1, 10, 1, 200)) is null && list is null);
        T.True("valid", Problem(() => list = ArgReader.Open(J("{\"cron\":[\"a\",\"b\"]}"), "cron").StringArray("cron", 1, 10, 1, 200)) is null && list is { Count: 2 });
        T.True("not an array", Problem(() => ArgReader.Open(J("{\"cron\":\"0 18 * * *\"}"), "cron").StringArray("cron", 1, 10, 1, 200))?.StartsWith("cron must be an array of strings", StringComparison.Ordinal) == true);
        T.True("empty", Problem(() => ArgReader.Open(J("{\"cron\":[]}"), "cron").StringArray("cron", 1, 10, 1, 200))?.Contains("1 to 10 items, but has 0", StringComparison.Ordinal) == true);
        string eleven = "[" + string.Join(",", Enumerable.Repeat("\"a\"", 11)) + "]";
        T.True("too many", Problem(() => ArgReader.Open(J("{\"cron\":" + eleven + "}"), "cron").StringArray("cron", 1, 10, 1, 200))?.Contains("but has 11", StringComparison.Ordinal) == true);
        T.Eq("item of the wrong type has an index path", "cron[1] must be a string.", Problem(() => ArgReader.Open(J("{\"cron\":[\"a\",5]}"), "cron").StringArray("cron", 1, 10, 1, 200)));
        T.Eq("an empty item has an index path", "cron[0] must be 1 to 200 characters long.", Problem(() => ArgReader.Open(J("{\"cron\":[\"\"]}"), "cron").StringArray("cron", 1, 10, 1, 200)));
        T.Eq("a long item has an index path", "cron[0] must be 1 to 200 characters long.", Problem(() => ArgReader.Open(J("{\"cron\":[\"" + new string('a', 201) + "\"]}"), "cron").StringArray("cron", 1, 10, 1, 200)));
    }
}
