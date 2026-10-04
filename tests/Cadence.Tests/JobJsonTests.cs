using Cadence.App.Cli;
using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>The job file of --apply: the sample files shipped in samples\, the 3.9 metadata shape, defaults and every rejection.</summary>
public static class JobJsonTests
{
    static readonly DateOnly Today = new(2026, 10, 1);

    static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Cadence.slnx"))) return d.FullName;
        throw new InvalidOperationException("Cadence.slnx not found above " + AppContext.BaseDirectory);
    }

    static JobJson.Parsed? Parse(string label, string json)
    {
        bool ok = JobJson.TryParse(json, Today, out JobJson.Parsed? p, out string err);
        T.True(label + ": parses (" + err + ")", ok && p is not null);
        return p;
    }

    static void Bad(string label, string json, string expectedPart)
    {
        bool ok = JobJson.TryParse(json, Today, out JobJson.Parsed? p, out string err);
        T.True(label + ": rejected", !ok && p is null);
        T.True(label + ": error mentions '" + expectedPart + "' (was: " + err + ")", err.Contains(expectedPart, StringComparison.Ordinal));
    }

    const string Minimal = """{"name":"Mini","schedule":{"cron":"0 9 * * *"},"run":{"exe":"cmd.exe"}}""";

    public static void TheShippedSamplesParseAndCompose()
    {
        string dir = Path.Combine(RepoRoot(), "samples");
        string[] files = ["stock-ingest.json", "youtube-feed.json", "every-minute-echo.json"];
        foreach (string f in files)
        {
            string text = File.ReadAllText(Path.Combine(dir, f));
            JobJson.Parsed? p = Parse(f, text);
            if (p is null) continue;
            T.True(f + ": has a fixed id (applying twice updates, not duplicates)", !p.IdGenerated);
            T.True(f + ": the id is valid", JobId.IsValid(p.Job.Id));
            try
            {
                var ctx = new ComposeContext("Cadence", "Cadence", ComposeSamples.User, "x", ComposeSamples.Now, null, ValueList<string>.Empty);
                TaskComposer.Compose(p.Job, ctx);
                T.True(f + ": composes into a task", true);
            }
            catch (ComposeException ex) { T.True(f + ": composes into a task", false, ex.Message); }
        }

        JobJson.Parsed? stock = Parse("stock again", File.ReadAllText(Path.Combine(dir, "stock-ingest.json")));
        T.Eq("stock: name", "Stock ingest (Dad)", stock?.Job.Name);
        T.Eq("stock: cron", "0 18 * * 1-5", stock?.Job.Schedule.CronLines[0]);
        T.Eq("stock: NYSE holidays", HolidayCalendar.Nyse, stock?.Job.Schedule.SkipHolidays);
        T.Eq("stock: anchor defaults to today", Today, stock?.Job.Schedule.Anchor);
        T.Eq("stock: timeout 30 minutes", TimeSpan.FromSeconds(1800), stock?.Job.Run.Timeout);
        T.Eq("stock: utf8", OutputEncoding.Utf8, stock?.Job.Run.Encoding);
        T.Eq("stock: PYTHONIOENCODING", "PYTHONIOENCODING=utf-8", string.Join(",", stock!.Job.Run.Env.Select(kv => kv.Key + "=" + kv.Value)));

        JobJson.Parsed? echo = Parse("echo again", File.ReadAllText(Path.Combine(dir, "every-minute-echo.json")));
        T.Eq("echo: cron", "* * * * *", echo?.Job.Schedule.CronLines[0]);
        T.Eq("echo: exe", "cmd.exe", echo?.Job.Run.Exe);
        T.Eq("echo: args", "/d /c \"echo tick\"", echo?.Job.Run.Args);
    }

    public static void TheMetadataShapeOfAJobIsAcceptedAsIs()
    {
        // The JSON a task carries in its Documentation (3.9) is a valid job file: fp/created/updated/prev/schema/gen/wv and run.skip are ignored.
        JobDefinition original = ComposeSamples.StockIngest();
        string doc = TaskComposer.Compose(original, ComposeSamples.Context()).RegistrationInfo.Documentation!;
        string json = doc[(doc.IndexOf('\n', StringComparison.Ordinal) + 1)..];
        JobJson.Parsed? p = Parse("metadata JSON", json);
        T.True("it is the same job, field for field", Equals(original, p?.Job));
        T.True("the id came from the file", p?.IdGenerated == false);
    }

    public static void DefaultsAndLenientSyntax()
    {
        JobJson.Parsed? p = Parse("minimal", Minimal);
        JobDefinition j = p!.Job;
        T.True("id is made up and valid", p.IdGenerated && JobId.IsValid(j.Id));
        T.Eq("enabled by default", true, j.Enabled);
        T.Eq("cron given as one string", "0 9 * * *", string.Join("|", j.Schedule.CronLines));
        T.Eq("no holiday skipping", HolidayCalendar.None, j.Schedule.SkipHolidays);
        T.Eq("anchor is the injected today", Today, j.Schedule.Anchor);
        T.Eq("args empty", "", j.Run.Args);
        T.Eq("no cwd", (string?)null, j.Run.WorkingDirectory);
        T.Eq("no env", 0, j.Run.Env.Count);
        T.Eq("no timeout", (TimeSpan?)null, j.Run.Timeout);
        T.Eq("hidden by default", true, j.Run.Hidden);
        T.Eq("oem output by default", OutputEncoding.Oem, j.Run.Encoding);
        T.Eq("default settings", new JobSettings(), j.Settings);

        JobJson.TryParse(Minimal, Today, out JobJson.Parsed? q, out _);
        T.True("two parses make two different ids", q!.Job.Id != j.Id);

        p = Parse("comments and trailing commas", "// note\n{\"name\":\"C\", /* x */ \"schedule\":{\"cron\":[\"0 9 * * *\",],},\"run\":{\"exe\":\"a.exe\",},}");
        T.Eq("still parsed", "C", p?.Job.Name);

        p = Parse("explicit values", """
            {"id":"abcd1234","name":"Full","enabled":false,
             "schedule":{"cron":["0 9 * * 1","0 10 * * 2"],"skipHolidays":"USFEDERAL","anchor":"2030-02-03"},
             "run":{"kind":"runProgram","exe":"C:\\x\\a.exe","args":"-a \"b\"","cwd":"C:\\x","env":{"A":"1","B":""},"timeoutSec":90,"hidden":false,"capture":false,"encoding":"UTF8","source":"C:\\x\\s.py"},
             "settings":{"startWhenAvailable":false,"runOnBatteries":false,"stopOnBatteries":true,"wakeToRun":true}}
            """);
        j = p!.Job;
        T.Eq("id", "abcd1234", j.Id);
        T.Eq("enabled", false, j.Enabled);
        T.Eq("two cron lines", 2, j.Schedule.CronLines.Count);
        T.Eq("federal", HolidayCalendar.UsFederal, j.Schedule.SkipHolidays);
        T.Eq("anchor", new DateOnly(2030, 2, 3), j.Schedule.Anchor);
        T.Eq("args", "-a \"b\"", j.Run.Args);
        T.Eq("cwd", @"C:\x", j.Run.WorkingDirectory);
        T.Eq("env", "A=1,B=", string.Join(",", j.Run.Env.Select(kv => kv.Key + "=" + kv.Value)));
        T.Eq("timeout", TimeSpan.FromSeconds(90), j.Run.Timeout);
        T.Eq("visible", false, j.Run.Hidden);
        T.Eq("utf8", OutputEncoding.Utf8, j.Run.Encoding);
        T.Eq("source", @"C:\x\s.py", j.Run.SourcePath);
        T.Eq("settings", new JobSettings { StartWhenAvailable = false, RunOnBatteries = false, StopOnBatteries = true, WakeToRun = true }, j.Settings);
        T.Eq("timeoutSec 0 means none", (TimeSpan?)null, Parse("zero timeout", Minimal.Replace("\"exe\":\"cmd.exe\"", "\"exe\":\"cmd.exe\",\"timeoutSec\":0", StringComparison.Ordinal))!.Job.Run.Timeout);
    }

    public static void EveryProblemIsExplained()
    {
        Bad("not JSON", "{oops", "not valid JSON");
        Bad("empty", "", "not valid JSON");
        Bad("array root", "[]", "one JSON object");
        Bad("string root", "\"x\"", "one JSON object");
        Bad("no name", """{"schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"}}""", "name is required");
        Bad("blank name", """{"name":"  ","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"}}""", "name is required");
        Bad("name not text", """{"name":3,"schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"}}""", "name is required");
        Bad("no schedule", """{"name":"n","run":{"exe":"a"}}""", "schedule is required");
        Bad("no cron", """{"name":"n","schedule":{},"run":{"exe":"a"}}""", "schedule.cron is required");
        Bad("empty cron array", """{"name":"n","schedule":{"cron":[]},"run":{"exe":"a"}}""", "at least one");
        Bad("blank cron line", """{"name":"n","schedule":{"cron":[" "]},"run":{"exe":"a"}}""", "at least one");
        Bad("cron not text", """{"name":"n","schedule":{"cron":[1]},"run":{"exe":"a"}}""", "only text lines");
        Bad("cron a number", """{"name":"n","schedule":{"cron":5},"run":{"exe":"a"}}""", "cron line or an array");
        Bad("bad calendar", """{"name":"n","schedule":{"cron":"0 9 * * *","skipHolidays":"lse"},"run":{"exe":"a"}}""", "none, usFederal or nyse");
        Bad("bad anchor", """{"name":"n","schedule":{"cron":"0 9 * * *","anchor":"1/2/2026"},"run":{"exe":"a"}}""", "schedule.anchor");
        Bad("no run", """{"name":"n","schedule":{"cron":"0 9 * * *"}}""", "run is required");
        Bad("no exe", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{}}""", "run.exe is required");
        Bad("blank exe", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":" "}}""", "run.exe is required");
        Bad("wrong kind", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"kind":"sendMail","exe":"a"}}""", "run.kind");
        Bad("args not text", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","args":5}}""", "run.args must be text");
        Bad("hidden not bool", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","hidden":"yes"}}""", "run.hidden must be true or false");
        Bad("capture differs from hidden", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","hidden":true,"capture":false}}""", "run.capture must equal run.hidden");
        Bad("negative timeout", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","timeoutSec":-1}}""", "run.timeoutSec");
        Bad("fractional timeout", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","timeoutSec":1.5}}""", "run.timeoutSec");
        Bad("text timeout", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","timeoutSec":"5"}}""", "run.timeoutSec");
        Bad("bad encoding", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","encoding":"latin1"}}""", "oem or utf8");
        Bad("env not an object", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","env":["A=1"]}}""", "run.env must be an object");
        Bad("env value not text", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","env":{"A":1}}}""", "run.env.A must be text");
        Bad("settings not an object", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"},"settings":true}""", "settings must be an object");
        Bad("settings flag not bool", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"},"settings":{"wakeToRun":1}}""", "settings.wakeToRun");
        Bad("enabled not bool", """{"name":"n","enabled":"no","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"}}""", "enabled must be true or false");
        Bad("id upper case", """{"id":"ABCD1234","name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"}}""", "id must be 8 characters");
        Bad("id too short", """{"id":"abc","name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"}}""", "id must be 8 characters");
        Bad("id not text", """{"id":12345678,"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"}}""", "id must be 8 characters");
    }

    public static void TyposAreNotSilentlyIgnored()
    {
        Bad("top level typo", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"},"enable":false}""", "Unknown field 'enable' in job");
        Bad("schedule typo", """{"name":"n","schedule":{"cron":"0 9 * * *","skipHoliday":"nyse"},"run":{"exe":"a"}}""", "Unknown field 'skipHoliday' in schedule");
        Bad("run typo", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a","cwdd":"c"}}""", "Unknown field 'cwdd' in run");
        Bad("settings typo", """{"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"},"settings":{"wake":true}}""", "Unknown field 'wake' in settings");
        // ...but the fields Cadence fills in itself are accepted at the top level, so a job copied from a task works.
        Parse("filled-in fields", """{"schema":1,"gen":1,"wv":1,"fp":"x","created":"c","updated":"u","prev":[],"name":"n","schedule":{"cron":"0 9 * * *"},"run":{"exe":"a"}}""");
    }
}
