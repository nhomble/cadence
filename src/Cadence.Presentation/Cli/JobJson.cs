using System.Globalization;
using System.Text.Json;
using Cadence.Core.Metadata;
using Cadence.Core.Model;

namespace Cadence.App.Cli;

/// <summary>
/// Reads the job file of <c>--apply</c>: the PLAN 3.9 job shape (<c>id</c>, <c>name</c>, <c>enabled</c>, <c>schedule</c>, <c>run</c>, <c>settings</c>)
/// without the fields Cadence fills in (<c>schema</c>, <c>gen</c>, <c>wv</c>, <c>fp</c>, <c>created</c>, <c>updated</c>, <c>createdBy</c>, <c>updatedBy</c>, <c>prev</c>, <c>run.skip</c>), which are accepted and
/// ignored so that a job copied out of a task's metadata works. <c>agentLock</c> (true or false, PLAN 12.4.4) is accepted and sets the per-job
/// "AI agents may not change this job" lock. Anything else unknown is an error (a typo must not silently do nothing).
/// <c>id</c> is optional: without it a new id is made, so applying the file again creates another job; give the file an id to make
/// it idempotent. Written by hand over JsonDocument because the DTOs of Cadence.Core.Metadata are internal.
/// </summary>
public static class JobJson
{
    static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "schema", "gen", "wv", "fp", "created", "updated", "createdBy", "updatedBy", "prev" };

    /// <summary>The parsed job, whether its id had to be made up, and whether the file gave the schedule an anchor date (otherwise it is today).</summary>
    /// <remarks><c>AgentLockGiven</c>: whether the file said anything about <c>agentLock</c>; when it did not, applying it over an existing job keeps that job's lock.</remarks>
    public sealed record Parsed(JobDefinition Job, bool IdGenerated, bool AnchorGiven, bool AgentLockGiven = false);

    public static bool TryParse(string json, DateOnly today, out Parsed? parsed, out string error)
    {
        parsed = null;
        error = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            string? problem = Build(doc.RootElement, today, out parsed);
            if (problem is null) return true;
            error = problem;
            parsed = null;
            return false;
        }
        catch (JsonException ex)
        {
            error = "The job file is not valid JSON: " + ex.Message;
            return false;
        }
    }

    static string? Build(JsonElement root, DateOnly today, out Parsed? parsed)
    {
        parsed = null;
        if (root.ValueKind != JsonValueKind.Object) return "The job file must hold one JSON object.";
        if (Unknown(root, "job", "id", "name", "enabled", "agentLock", "schedule", "run", "settings") is { } u0) return u0;

        string id;
        bool generated = false;
        if (root.TryGetProperty("id", out JsonElement idEl))
        {
            if (idEl.ValueKind != JsonValueKind.String || !JobId.IsValid(idEl.GetString())) return "id must be 8 characters of a-z and 0-9.";
            id = idEl.GetString()!;
        }
        else { id = JobId.New(); generated = true; }

        if (!root.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameEl.GetString()))
            return "name is required (text, 1 to 100 characters).";
        string name = nameEl.GetString()!;

        if (Bool(root, "enabled", true, "job", out bool enabled) is { } e1) return e1;
        if (Bool(root, "agentLock", false, "job", out bool agentLock) is { } e0) return e0;

        if (!root.TryGetProperty("schedule", out JsonElement sch) || sch.ValueKind != JsonValueKind.Object) return "schedule is required (an object with cron).";
        if (Unknown(sch, "schedule", "cron", "skipHolidays", "anchor") is { } u1) return u1;
        var cron = new List<string>();
        if (!sch.TryGetProperty("cron", out JsonElement cronEl)) return "schedule.cron is required (a cron line or an array of lines).";
        if (cronEl.ValueKind == JsonValueKind.String) cron.Add(cronEl.GetString()!);
        else if (cronEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement l in cronEl.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.String) return "schedule.cron must contain only text lines.";
                cron.Add(l.GetString()!);
            }
        }
        else return "schedule.cron must be a cron line or an array of lines.";
        if (cron.Count == 0 || cron.Any(string.IsNullOrWhiteSpace)) return "schedule.cron needs at least one non-empty line.";

        HolidayCalendar skip = HolidayCalendar.None;
        if (sch.TryGetProperty("skipHolidays", out JsonElement skipEl))
        {
            string? s = skipEl.ValueKind == JsonValueKind.String ? skipEl.GetString() : null;
            if (string.Equals(s, "none", StringComparison.OrdinalIgnoreCase)) skip = HolidayCalendar.None;
            else if (string.Equals(s, "usFederal", StringComparison.OrdinalIgnoreCase)) skip = HolidayCalendar.UsFederal;
            else if (string.Equals(s, "nyse", StringComparison.OrdinalIgnoreCase)) skip = HolidayCalendar.Nyse;
            else return "schedule.skipHolidays must be none, usFederal or nyse.";
        }
        DateOnly anchor = today;
        bool anchorGiven = sch.TryGetProperty("anchor", out JsonElement anchorEl);
        if (anchorGiven
            && (anchorEl.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(anchorEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out anchor)))
            return "schedule.anchor must be a date like 2026-10-01.";

        if (!root.TryGetProperty("run", out JsonElement run) || run.ValueKind != JsonValueKind.Object) return "run is required (an object with exe).";
        if (Unknown(run, "run", "kind", "exe", "args", "cwd", "env", "timeoutSec", "hidden", "capture", "encoding", "source", "skip") is { } u2) return u2;
        if (run.TryGetProperty("kind", out JsonElement kindEl) && !(kindEl.ValueKind == JsonValueKind.String && kindEl.GetString() == "runProgram"))
            return "run.kind must be runProgram.";
        if (!run.TryGetProperty("exe", out JsonElement exeEl) || exeEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(exeEl.GetString()))
            return "run.exe is required (the program to start).";
        if (Text(run, "args", "run", out string? args) is { } e2) return e2;
        if (Text(run, "cwd", "run", out string? cwd) is { } e3) return e3;
        if (Text(run, "source", "run", out string? source) is { } e4) return e4;
        if (Bool(run, "hidden", true, "run", out bool hidden) is { } e5) return e5;
        if (run.TryGetProperty("capture", out JsonElement capEl))
        {
            if (capEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "run.capture must be true or false.";
            if (capEl.GetBoolean() != hidden) return "run.capture must equal run.hidden (a visible job is not captured).";
        }
        int timeoutSec = 0;
        if (run.TryGetProperty("timeoutSec", out JsonElement toEl)
            && (toEl.ValueKind != JsonValueKind.Number || !toEl.TryGetInt32(out timeoutSec) || timeoutSec < 0))
            return "run.timeoutSec must be a whole number of seconds, 0 for none.";
        OutputEncoding encoding = OutputEncoding.Oem;
        if (run.TryGetProperty("encoding", out JsonElement encEl))
        {
            string? s = encEl.ValueKind == JsonValueKind.String ? encEl.GetString() : null;
            if (string.Equals(s, "oem", StringComparison.OrdinalIgnoreCase)) encoding = OutputEncoding.Oem;
            else if (string.Equals(s, "utf8", StringComparison.OrdinalIgnoreCase)) encoding = OutputEncoding.Utf8;
            else return "run.encoding must be oem or utf8.";
        }
        var env = new List<KeyValuePair<string, string>>();
        if (run.TryGetProperty("env", out JsonElement envEl))
        {
            if (envEl.ValueKind != JsonValueKind.Object) return "run.env must be an object of text values.";
            foreach (JsonProperty p in envEl.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) return "run.env." + p.Name + " must be text.";
                env.Add(new KeyValuePair<string, string>(p.Name, p.Value.GetString()!));
            }
        }

        var settings = new JobSettings();
        if (root.TryGetProperty("settings", out JsonElement setEl))
        {
            if (setEl.ValueKind != JsonValueKind.Object) return "settings must be an object.";
            if (Unknown(setEl, "settings", "startWhenAvailable", "runOnBatteries", "stopOnBatteries", "wakeToRun") is { } u3) return u3;
            if (Bool(setEl, "startWhenAvailable", settings.StartWhenAvailable, "settings", out bool sw) is { } e6) return e6;
            if (Bool(setEl, "runOnBatteries", settings.RunOnBatteries, "settings", out bool rb) is { } e7) return e7;
            if (Bool(setEl, "stopOnBatteries", settings.StopOnBatteries, "settings", out bool sb) is { } e8) return e8;
            if (Bool(setEl, "wakeToRun", settings.WakeToRun, "settings", out bool wk) is { } e9) return e9;
            settings = new JobSettings { StartWhenAvailable = sw, RunOnBatteries = rb, StopOnBatteries = sb, WakeToRun = wk };
        }

        var job = new JobDefinition
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            AgentLock = agentLock,
            Schedule = new ScheduleSpec(new ValueList<string>(cron), skip, anchor),
            Run = new RunSpec
            {
                Exe = exeEl.GetString()!,
                Args = args ?? "",
                WorkingDirectory = string.IsNullOrEmpty(cwd) ? null : cwd,
                Env = new ValueList<KeyValuePair<string, string>>(env),
                Timeout = timeoutSec > 0 ? TimeSpan.FromSeconds(timeoutSec) : null,
                Hidden = hidden,
                Encoding = encoding,
                SourcePath = string.IsNullOrEmpty(source) ? null : source,
            },
            Settings = settings,
        };
        parsed = new Parsed(job, generated, anchorGiven, root.TryGetProperty("agentLock", out _));
        return null;
    }

    static string? Unknown(JsonElement obj, string where, params string[] known)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (Array.IndexOf(known, p.Name) >= 0) continue;
            if (where == "job" && Ignored.Contains(p.Name)) continue;
            return "Unknown field '" + p.Name + "' in " + where + ".";
        }
        return null;
    }

    static string? Bool(JsonElement obj, string name, bool fallback, string where, out bool value)
    {
        value = fallback;
        if (!obj.TryGetProperty(name, out JsonElement el)) return null;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) { value = el.GetBoolean(); return null; }
        return where + "." + name + " must be true or false.";
    }

    static string? Text(JsonElement obj, string name, string where, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out JsonElement el)) return null;
        if (el.ValueKind == JsonValueKind.String) { value = el.GetString(); return null; }
        if (el.ValueKind == JsonValueKind.Null) return null;
        return where + "." + name + " must be text.";
    }
}
