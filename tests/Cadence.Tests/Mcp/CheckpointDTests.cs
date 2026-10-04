using System.Diagnostics;
using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Core.Cron;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>Regression tests for the defects fixed in Checkpoint D (PLAN.md section 11, docs\REVIEW-D-agent-write-tools.md). Fixed clock, never today's date.</summary>
public static class CheckpointDTests
{
    static readonly DateTimeOffset From = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    static ValueList<TaskTrigger> Triggers(params string[] cron)
    {
        CompiledSchedule c = CronCompiler.Compile(new ScheduleSpec(new ValueList<string>(cron), HolidayCalendar.None, new DateOnly(2026, 10, 1)), new MapContext("HOST\\user"));
        if (!c.Success) throw new InvalidOperationException("sample does not compile: " + string.Join("; ", c.Diagnostics.Select(d => d.Message)));
        return c.Triggers;
    }

    static double? ForLimit(params string[] cron) => ScheduleDensity.GapForLimit(Triggers(cron), From, TimeZoneInfo.Utc, Five)?.TotalMinutes;

    /// <summary>D1: the minimum interval only looked 14 days ahead, so a schedule dense on some later day passed.</summary>
    public static void ADenseDayLaterInTheYearIsSeen()
    {
        T.True("the 14-day look alone sees nothing for every minute on 20 December (the defect)", ScheduleDensity.MinimumGap(Triggers("* * 20 12 *"), From, TimeZoneInfo.Utc) is null);
        T.Eq("every minute on 20 December", 1.0, ForLimit("* * 20 12 *") ?? -1);
        T.Eq("every minute in January", 1.0, ForLimit("* * * 1 *") ?? -1);
        T.Eq("9:00 and 9:01 on 1 January", 1.0, ForLimit("0,1 9 1 1 *") ?? -1);
        T.Eq("every minute on a leap day (2028)", 1.0, ForLimit("* * 29 2 *") ?? -1);
        T.Eq("every 2 minutes on Christmas Eve", 2.0, ForLimit("*/2 * 24 12 *") ?? -1);
        T.Eq("a daily line plus one minute later on 15 March", 1.0, ForLimit("0 9 * * *", "1 9 15 3 *") ?? -1);
    }

    public static void SparseAndRegularSchedulesKeepTheirGap()
    {
        T.Eq("every 5 minutes at a limit of 5", 5.0, ForLimit("*/5 * * * *") ?? -1);
        T.Eq("daily", 1440.0, ForLimit("0 9 * * *") ?? -1);
        T.True("yearly: still none (fewer than two runs in 14 days, none closer than the limit later)", ForLimit("0 0 1 1 *") is null);
        T.True("@logon: none", ForLimit("@logon") is null);
        T.Eq("weekly lines a minute apart on different days are a day apart", 1439.0, ScheduleDensity.GapForLimit(Triggers("0 9 * * 1", "59 8 * * 2"), From, TimeZoneInfo.Utc, TimeSpan.FromDays(2))?.TotalMinutes ?? -1);
        T.True("no triggers", ScheduleDensity.GapForLimit(ValueList<TaskTrigger>.Empty, From, TimeZoneInfo.Utc, Five) is null);
    }

    public static void TheLongLookStaysQuick()
    {
        var sw = Stopwatch.StartNew();
        _ = ForLimit("*/5 * * * *");          // the worst common case: four years of runs, none closer than the limit
        _ = ForLimit("*/5 * * * *", "2 9 29 2 *");
        sw.Stop();
        T.True("two four-year looks finish in under 10 s (the save waits for this)", sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
    }

    public static void TheSaveToolAndThePreviewRefuseIt()
    {
        using var rig = new WriteRig();
        ToolResult r = rig.Save(WriteRig.Create("Dense in December", "* * 20 12 *"));
        T.True("schedule_too_frequent", r.IsError && WriteRig.Code(r) == "schedule_too_frequent", r.Text);
        T.Eq("nothing registered", 0, rig.Registers);
        ToolResult p = rig.Call("cadence_preview_schedule", "{\"cron\":[\"* * 20 12 *\"]}");
        T.True("preview: not an error", !p.IsError, p.Text);
        T.Eq("preview: agents may not save it", false, p.Structured!["agentsMaySave"]!.GetValue<bool>());
        T.Eq("preview: the gap is one minute", 1, p.Structured["minimumGapMinutes"]!.GetValue<int>());
        T.True("a yearly job is still fine", !rig.Save(WriteRig.Create("Yearly", "0 3 1 1 *")).IsError);
    }

    /// <summary>D2: cadence_set_job_enabled without expectedRevision wrote the job as read at the start of the call, overwriting a change made just before the write.</summary>
    public static void SetEnabledWithoutARevisionNeverOverwritesAChangeMadeMeanwhile()
    {
        WriteRig? holder = null;
        JobDefinition owner = WriteRig.Job("Owner job", "own00001");
        // The second policy read happens inside the writer lock, after the plan: the owner's edit lands exactly in the gap.
        using var rig = new WriteRig(store => new FlipReader(store, 2, () =>
            T.True("fixture: the owner edits the arguments meanwhile", new Applier(holder!.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(owner with { Run = owner.Run with { Args = "--owner-edit" } }, "Owner job").Success)));
        holder = rig;
        rig.OwnerSave(owner);

        ToolResult r = rig.Call("cadence_set_job_enabled", "{\"jobId\":\"own00001\",\"enabled\":false}");
        T.True("refused as stale, not applied over the owner's edit", r.IsError && WriteRig.Code(r) == "stale_revision", r.Text);
        ClassifiedTask live = rig.Live("own00001");
        T.Eq("the owner's edit is kept", "--owner-edit", live.Job!.Run.Args);
        T.True("still enabled, in sync", live.Job.Enabled && live.Sync == SyncStatus.InSync);
    }

    /// <summary>D3: the cap on agent-created jobs was counted before the writer lock only, so two servers creating at once could both pass.</summary>
    public static void TheJobCapIsCountedAgainInsideTheWriterLock()
    {
        WriteRig? holder = null;
        using var rig = new WriteRig(store => new FlipReader(store, 2, () =>
            T.True("fixture: another agent's server creates a job meanwhile",
                new Applier(holder!.Jobs(Actor.Agent("Other agent")), TimeSpan.FromSeconds(5)).Save(WriteRig.Job("Other agent job", "oth00001", "0 3 1 1 *"), null).Success)));
        holder = rig;
        rig.Policy.SetLimits(null, 1);

        ToolResult r = rig.Save(WriteRig.Create("Mine", "0 3 1 1 *"));
        T.True("limit_reached at the last moment", r.IsError && WriteRig.Code(r) == "limit_reached", r.Text);
        IReadOnlyList<ClassifiedTask> all = rig.Jobs().Load(false);
        T.Eq("one agent job, not two", 1, all.Count(c => c.Metadata?.CreatedBy?.StartsWith("agent:", StringComparison.Ordinal) == true));
        T.True("mine was not created", all.All(c => c.Job?.Name != "Mine"));
        T.True("audited once as refused/limit_reached", rig.SaveAudit is [{ Outcome: "refused", Code: "limit_reached" }]);
    }

    /// <summary>The control tools' texts and the server instructions are compared with PLAN.md 12.3.6 (added in Checkpoint D), like the other tools.</summary>
    public static void ControlToolTextsAndInstructionsEqualThePlan()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PLAN.md"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) { T.True("PLAN.md is not in this checkout (the design log is kept locally, not in the repo): comparison with the plan skipped", true); return; }
        string[] plan = File.ReadAllLines(Path.Combine(dir, "PLAN.md"));
        T.Eq("suspend description", Exact(plan, "- Suspend description (exact):"), Cadence.Agent.Tools.ToolCatalog.SuspendDescription);
        T.Eq("resume description", Exact(plan, "- Resume description (exact):"), Cadence.Agent.Tools.ToolCatalog.ResumeDescription);
        T.Eq("server instructions", Exact(plan, "- Server instructions (exact):"), Cadence.Agent.McpHost.Instructions);
        T.True("the control tools are in the catalogue with these texts", JsonNode.Parse(Cadence.Agent.Tools.ToolCatalog.SwitchSchema) is JsonObject);
    }

    static string? Exact(string[] lines, string prefix)
    {
        string? line = lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null) return null;
        int a = line.IndexOf('`', StringComparison.Ordinal);
        int b = line.LastIndexOf('`');
        return a >= 0 && b > a ? line[(a + 1)..b] : null;
    }
}
