using System.Globalization;
using System.Text.Json.Nodes;
using Cadence.Core.Cron;
using Cadence.Core.History;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.History;

namespace Cadence.Agent.Tools;

/// <summary>
/// The JSON shapes of a job for agents (PLAN 12.3.3): <c>JobListItem</c>, <c>JobView</c>, <c>RunItem</c>, <c>stats</c>. Everything is built with
/// <see cref="JsonObject"/> (names and messages may hold characters that need escaping), times are ISO 8601 with the PC's offset plus a display
/// text, and environment variable VALUES are never included (names only: they are often secrets).
/// </summary>
public static class JobViews
{
    const string MapUser = "user";

    public static string Iso(DateTimeOffset t, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>The job's display name (the task name for a task whose metadata can't be read).</summary>
    public static string DisplayName(ClassifiedTask t) => t.Job?.Name ?? t.Observed.Name;

    /// <summary>12.3.3: InSync ok; Drifted changedOutsideCadence (copy when a duplicate id); Outdated needsRefresh; corrupt damaged; newer newerVersion.</summary>
    public static string StateName(ClassifiedTask t) => t.Ownership switch
    {
        Ownership.CorruptMetadata => "damaged",
        Ownership.NewerVersion => "newerVersion",
        _ => t.Sync switch
        {
            SyncStatus.InSync => "ok",
            SyncStatus.Drifted => t.DuplicateOf is not null ? "copy" : "changedOutsideCadence",
            SyncStatus.Outdated => "needsRefresh",
            _ => "damaged",
        },
    };

    public static string StatusName(RunStatus s) => s switch
    {
        RunStatus.Ok => "ok",
        RunStatus.Failed => "failed",
        RunStatus.TimedOut => "timedOut",
        RunStatus.LaunchError => "couldNotStart",
        RunStatus.Skipped => "skipped",
        RunStatus.Running => "running",
        RunStatus.NotReported => "didNotReport",
        _ => "none",
    };

    /// <summary>"Today 18:00", "Tomorrow 8:00", "Yesterday 18:00", then "Fri 2 Oct 18:00" (the year only when it differs from now's).</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTime a = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        DateTime n = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        string time = a.ToString("H:mm", CultureInfo.InvariantCulture);
        int days = (a.Date - n.Date).Days;
        return days switch
        {
            0 => "Today " + time,
            1 => "Tomorrow " + time,
            -1 => "Yesterday " + time,
            _ => a.ToString(a.Year == n.Year ? "ddd d MMM H:mm" : "ddd d MMM yyyy H:mm", CultureInfo.InvariantCulture),
        };
    }

    static JsonObject TimeItem(DateTimeOffset at, string text, TimeZoneInfo zone) => new() { ["at"] = Iso(at, zone), ["text"] = text };

    /// <summary>12.3.3: <c>by</c> is <see cref="Actor.Describe"/> and <c>byKind</c> is app|cli|agent|unknown; a job written by v0.1.0 has no token and reads as unknown.</summary>
    static JsonObject Attribution(DateTimeOffset at, string? token, TimeZoneInfo zone)
    {
        Actor who = Actor.Parse(token);
        return new JsonObject { ["at"] = Iso(at, zone), ["by"] = who.Describe(), ["byKind"] = who.KindName };
    }

    /// <summary>The runs that Task Scheduler will start next, holiday-skipped ones marked; none for a disabled job or a schedule that does not compile.</summary>
    public static IReadOnlyList<Occurrence> UpcomingRuns(JobDefinition job, DateTimeOffset now, TimeZoneInfo zone, int count)
    {
        if (!job.Enabled || count <= 0) return [];
        CompiledSchedule compiled = CronCompiler.Compile(job.Schedule, new MapContext(MapUser));
        if (!compiled.Success || compiled.Triggers.Count == 0) return [];
        return NextRuns.Next(compiled.Triggers, job.Schedule.SkipHolidays, now, count, zone);
    }

    public static JsonObject NextRunItem(Occurrence o, DateTimeOffset now, TimeZoneInfo zone) => new()
    {
        ["at"] = Iso(o.At, zone),
        ["text"] = PreviewFormatter.FormatOne(o, now, zone),
        ["skipped"] = o.SkippedHoliday,
        ["clockChange"] = o.DstAdjusted,
    };

    /// <summary>The newest non-skipped run (a skipped run is not a result); status none when there is no such run.</summary>
    public static JsonObject LastResult(JobHistory h, TimeZoneInfo zone)
    {
        RunView? r = h.Recent.FirstOrDefault(v => v.Status != RunStatus.Skipped);
        if (r is null) return new JsonObject { ["status"] = "none", ["at"] = null, ["exitCode"] = null, ["message"] = null };
        return new JsonObject { ["status"] = StatusName(r.Status), ["at"] = Iso(r.When, zone), ["exitCode"] = r.ExitCode, ["message"] = r.Message };
    }

    /// <summary>One row of the job list. Rows of damaged or newer-version tasks have no jobId (they can't be addressed) but say what is wrong.</summary>
    public static JsonObject ListItem(ClassifiedTask t, JobHistory h, DateTimeOffset now, TimeZoneInfo zone)
    {
        JobDefinition? job = t.Job;
        Occurrence? next = job is null ? null : UpcomingRuns(job, now, zone, 8).Where(o => o.SkippedHoliday is null).Select(o => (Occurrence?)o).FirstOrDefault();
        return new JsonObject
        {
            ["jobId"] = job?.Id,
            ["name"] = DisplayName(t),
            ["enabled"] = job?.Enabled ?? t.Actual?.Settings.Enabled ?? false,
            ["state"] = StateName(t),
            ["schedule"] = job is null ? null : EnglishRenderer.Describe(job.Schedule),
            ["nextRun"] = next is { } n ? TimeItem(n.At, When(n.At, now, zone), zone) : null,
            ["lastResult"] = StatusName(h.Recent.FirstOrDefault(v => v.Status != RunStatus.Skipped)?.Status ?? (RunStatus)(-1)),
            ["updated"] = t.Metadata is { } m ? Attribution(m.Updated, m.UpdatedBy, zone) : null,
            ["lockedForAgents"] = job?.AgentLock ?? false,
            ["problem"] = t.Problem,
        };
    }

    /// <summary>The full view of one job (12.3.3 JobView). Environment values are not included, only the variable names.</summary>
    public static JsonObject Full(ClassifiedTask t, JobHistory h, DateTimeOffset now, TimeZoneInfo zone, int nextRuns = 5)
    {
        JobDefinition job = t.Job ?? throw new ArgumentException("A job with a readable definition is required.", nameof(t));
        var upcoming = new JsonArray();
        foreach (Occurrence o in UpcomingRuns(job, now, zone, nextRuns)) upcoming.Add(NextRunItem(o, now, zone));

        var cron = new JsonArray();
        foreach (string line in job.Schedule.CronLines) cron.Add(line);   // verbatim

        var envNames = new JsonArray();
        foreach (KeyValuePair<string, string> kv in job.Run.Env) envNames.Add(kv.Key);

        return new JsonObject
        {
            ["jobId"] = job.Id,
            ["name"] = job.Name,
            ["taskPath"] = t.Observed.Path,
            ["enabled"] = job.Enabled,
            ["state"] = StateName(t),
            ["problem"] = t.Problem,
            ["schedule"] = new JsonObject
            {
                ["cron"] = cron,
                ["skipHolidays"] = CalendarName(job.Schedule.SkipHolidays),
                ["description"] = EnglishRenderer.Describe(job.Schedule),
            },
            ["nextRuns"] = upcoming,
            ["program"] = new JsonObject
            {
                ["path"] = job.Run.Exe,
                ["arguments"] = job.Run.Args,
                ["workingFolder"] = job.Run.WorkingDirectory,
                ["hidden"] = job.Run.Hidden,
                ["timeoutMinutes"] = job.Run.Timeout is { } to ? (int)Math.Ceiling(to.TotalMinutes) : 0,
                ["outputEncoding"] = job.Run.Encoding == OutputEncoding.Utf8 ? "utf8" : "oem",
                ["environmentNames"] = envNames,
            },
            ["lastResult"] = LastResult(h, zone),
            ["created"] = t.Metadata is { } c ? Attribution(c.Created, c.CreatedBy, zone) : null,
            ["updated"] = t.Metadata is { } u ? Attribution(u.Updated, u.UpdatedBy, zone) : null,
            ["lockedForAgents"] = job.AgentLock,
            ["revision"] = JobRevision.Of(t),
        };
    }

    public static string CalendarName(HolidayCalendar c) => c switch
    {
        HolidayCalendar.UsFederal => "usFederal",
        HolidayCalendar.Nyse => "nyse",
        _ => "none",
    };

    public static JsonObject Stats(JobHistory h) => new()
    {
        ["averageRuntimeSeconds"] = h.Summary.AverageRuntime is { } avg ? Math.Round(avg.TotalSeconds, 1) : null,
        ["successRate"] = h.Summary.SuccessRate is { } rate ? Math.Round(rate, 4) : null,
        ["sampleSize"] = h.Summary.SampleSize,
    };

    public static JsonArray RecentRuns(JobHistory h, int max, DateTimeOffset now, TimeZoneInfo zone)
    {
        var rows = new JsonArray();
        foreach (RunView r in h.Recent.Take(max))
        {
            rows.Add(new JsonObject
            {
                ["runId"] = r.RunId,
                ["at"] = Iso(r.When, zone),
                ["text"] = When(r.When, now, zone),
                ["durationSeconds"] = r.Duration is { } d ? Math.Round(d.TotalSeconds, 1) : null,
                ["status"] = StatusName(r.Status),
                ["exitCode"] = r.ExitCode,
                ["message"] = r.Message,
                ["hasOutput"] = r.HasOutput,
            });
        }
        return rows;
    }
}
