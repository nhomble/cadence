using System.Text.Json;
using Cadence.Core.Cron;
using Cadence.Core.History;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.Core.Text;
using Cadence.History;
using Cadence.Scheduler;

namespace Cadence.App.Cli;

/// <summary>
/// <c>--probe [--all]</c>: the managed folder as JSON (PLAN 7.6). Read-only: it only lists and reads. <c>lastResult</c>, <c>badge</c> and the
/// success figures come from run records (<see cref="HistoryService"/>); Task Scheduler's own result code appears only as the raw
/// <c>taskScheduler.lastTaskResult</c> field. Foreign tasks are listed only with <c>--all</c>.
/// </summary>
public static class Probe
{
    public const int NextCount = 5;
    public const int HistoryRows = 20;

    public static string Build(CliEnv env, bool all, string version)
    {
        var jobs = new ManagedJobs(env.Gateway, env.Clock);   // read-only: no actor
        IReadOnlyList<ClassifiedTask> loaded = jobs.Load(includeForeign: all);
        var history = new HistoryService(env.Store);
        DateTimeOffset now = env.Clock();

        return CliFormat.Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("version", version);
            w.WriteString("user", env.Gateway.ConnectedUserId);
            w.WriteString("folder", env.Gateway.Options.FolderName);
            w.WriteString("probedAt", CliFormat.Iso(now));

            w.WriteStartArray("jobs");
            foreach (ClassifiedTask c in loaded.Where(c => c.Ownership != Ownership.Foreign)) WriteJob(w, c, history, env, now);
            w.WriteEndArray();

            if (all)
            {
                w.WriteStartArray("foreign");
                foreach (ClassifiedTask c in loaded.Where(c => c.Ownership == Ownership.Foreign)) WriteForeign(w, c);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });
    }

    static void WriteJob(Utf8JsonWriter w, ClassifiedTask c, HistoryService history, CliEnv env, DateTimeOffset now)
    {
        JobDefinition? job = c.Job;
        w.WriteStartObject();
        CliFormat.Str(w, "id", job?.Id);
        w.WriteString("name", job?.Name ?? c.Observed.Name);
        w.WriteString("taskPath", c.Observed.Path);
        w.WriteString("ownership", CliFormat.OwnershipName(c.Ownership));
        w.WriteString("sync", CliFormat.SyncName(c.Sync));
        w.WriteBoolean("enabled", job?.Enabled ?? c.Actual?.Settings.Enabled ?? false);
        CliFormat.Str(w, "problem", c.Problem);

        // PLAN 12.4.1: who created and last changed the job and when (a job written by v0.1.0 has no actor: "unknown"), its revision, the agent lock.
        CadenceMetadata? meta = c.Metadata;
        CliFormat.Str(w, "created", meta is null ? null : CliFormat.Iso(meta.Created));
        w.WriteString("createdBy", meta is null ? "unknown" : Token(meta.CreatedBy));
        CliFormat.Str(w, "updated", meta is null ? null : CliFormat.Iso(meta.Updated));
        w.WriteString("updatedBy", meta is null ? "unknown" : Token(meta.UpdatedBy));
        CliFormat.Str(w, "revision", c.Actual is null ? null : JobRevision.Of(c));
        w.WriteBoolean("agentLock", job?.AgentLock ?? false);

        if (job is null)
        {
            w.WriteNull("schedule");
            w.WriteNull("cron");
            w.WriteNull("skipHolidays");
            w.WriteStartArray("next");
            w.WriteEndArray();
            w.WriteNull("lastResult");
            w.WriteNull("badge");
            w.WriteNull("lastRunAt");
            w.WriteNull("averageRuntimeMs");
            w.WriteNull("successRate");
            w.WriteNumber("sampleSize", 0);
        }
        else
        {
            w.WriteString("schedule", EnglishRenderer.Describe(job.Schedule));
            w.WriteStartArray("cron");
            foreach (string line in job.Schedule.CronLines) w.WriteStringValue(line);
            w.WriteEndArray();
            w.WriteString("skipHolidays", CliFormat.CalendarName(job.Schedule.SkipHolidays));

            w.WriteStartArray("next");
            foreach (string iso in NextRunTimes(job, env, now)) w.WriteStringValue(iso);
            w.WriteEndArray();

            JobHistory h = history.Load(c, HistoryRows);
            CliFormat.Str(w, "lastResult", h.Summary.LastResult is { } r ? CliFormat.StatusName(r) : null);
            w.WriteString("badge", CliFormat.Badge(h));
            CliFormat.Str(w, "lastRunAt", h.Summary.LastRunAt is { } at ? CliFormat.Iso(at) : null);
            if (h.Summary.AverageRuntime is { } avg) w.WriteNumber("averageRuntimeMs", CliFormat.Ms(avg)); else w.WriteNull("averageRuntimeMs");
            if (h.Summary.SuccessRate is { } rate) w.WriteNumber("successRate", Math.Round(rate, 4)); else w.WriteNull("successRate");
            w.WriteNumber("sampleSize", h.Summary.SampleSize);
        }

        w.WriteStartArray("drift");
        foreach (FieldDiff d in c.DriftDiffs)
        {
            w.WriteStartObject();
            w.WriteString("field", d.Field);
            CliFormat.Str(w, "before", d.Before);
            CliFormat.Str(w, "after", d.After);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Task Scheduler's own numbers, raw. Hidden jobs always show 0 here, so nothing above is derived from it.
        w.WriteStartObject("taskScheduler");
        w.WriteString("state", ((TaskState)c.Observed.State).ToString());
        CliFormat.Str(w, "lastRunTime", CliFormat.Iso(c.Observed.LastRunTime));
        CliFormat.Str(w, "nextRunTime", CliFormat.Iso(c.Observed.NextRunTime));
        if (c.Observed.LastTaskResult is { } code) w.WriteString("lastTaskResult", CliFormat.HResult(code)); else w.WriteNull("lastTaskResult");
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>The normalized actor token ("app", "cli", "agent:Name") or "unknown".</summary>
    static string Token(string? raw) => Actor.Parse(raw).Token ?? "unknown";

    static void WriteForeign(Utf8JsonWriter w, ClassifiedTask c)
    {
        w.WriteStartObject();
        w.WriteString("name", c.Observed.Name);
        w.WriteString("taskPath", c.Observed.Path);
        w.WriteString("ownership", "foreign");
        w.WriteBoolean("enabled", c.Actual?.Settings.Enabled ?? false);
        w.WriteString("state", ((TaskState)c.Observed.State).ToString());
        CliFormat.Str(w, "nextRunTime", CliFormat.Iso(c.Observed.NextRunTime));
        w.WriteEndObject();
    }

    /// <summary>The next runs that will really start (holiday-skipped ones are left out); none for a disabled job or a schedule that does not compile.</summary>
    public static IReadOnlyList<string> NextRunTimes(JobDefinition job, CliEnv env, DateTimeOffset now)
    {
        if (!job.Enabled) return [];
        CompiledSchedule compiled = CronCompiler.Compile(job.Schedule, new MapContext(env.Gateway.ConnectedUserId));
        if (!compiled.Success || compiled.Triggers.Count == 0) return [];
        IReadOnlyList<Occurrence> runs = NextRuns.Next(compiled.Triggers, job.Schedule.SkipHolidays, now, NextCount * 3, env.Zone);
        return runs.Where(o => o.SkippedHoliday is null).Take(NextCount)
            .Select(o => CliFormat.Iso(TimeZoneInfo.ConvertTime(o.At, env.Zone))).ToList();
    }
}
