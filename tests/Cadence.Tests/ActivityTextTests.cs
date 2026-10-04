using Cadence.Agent.Audit;
using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>Phase 23: the "Agent activity" panel's lines. Pure: newest first, bounded, control characters escaped, a fake name can't pass as the owner.</summary>
public static class ActivityTextTests
{
    static readonly DateTimeOffset Now = DemoServices.FixedNow;
    static readonly TimeZoneInfo Zone = DemoServices.FixedZone;
    static DateTimeOffset Today(int h, int m, int s = 0) => new(2026, 11, 27, h, m, s, TimeSpan.FromHours(-5));

    static AuditEntry E(DateTimeOffset at, string tool, string outcome, string client = "Claude Code", string? job = null, string? code = null, string detail = "") =>
        new(at, "s1", client, null, tool, null, job, outcome, code, detail);

    public static void RowsReadInPlainWords()
    {
        ActivityRow saved = ActivityText.Row(E(Today(7, 0), "cadence_save_job", "ok", job: "Sync notes", detail: "Created the job"), Now, Zone);
        T.Eq("when", "Today 7:00", saved.When);
        T.Eq("who", "Claude Code", saved.Who);
        T.Eq("what", "saved a schedule", saved.What);
        T.Eq("result", "Done", saved.Result);
        T.Eq("tone", ActivityTone.Ok, saved.Tone);
        T.Eq("the job's name leads the second line", "Sync notes " + (char)0xB7 + " Created the job", saved.Detail);
        T.Eq("a job with nothing said about it", "Sync notes", ActivityText.Row(E(Today(7, 0), "cadence_delete_job", "ok", job: "Sync notes"), Now, Zone).Detail);

        ActivityRow refused = ActivityText.Row(E(Today(7, 1), "cadence_set_job_enabled", "refused", job: "Photo backup", code: "locked", detail: "The owner locked this job."), Now, Zone);
        T.Eq("refused", "Refused: locked", refused.Result);
        T.Eq("refused tone", ActivityTone.Notice, refused.Tone);
        T.Eq("two words", "Refused: schedule too frequent", ActivityText.Row(E(Today(7, 1), "cadence_save_job", "refused", code: "schedule_too_frequent"), Now, Zone).Result);

        T.Eq("connected", "connected", ActivityText.What(E(Today(7, 0), "session", "connected")));
        T.Eq("disconnected", "disconnected", ActivityText.What(E(Today(7, 0), "session", "disconnected")));
        T.Eq("quiet", ActivityTone.Quiet, ActivityText.Row(E(Today(7, 0), "session", "connected"), Now, Zone).Tone);
        T.Eq("suspend by agent", "suspended agents", ActivityText.What(E(Today(7, 0), "cadence_suspend_agents", "suspended")));
        T.Eq("resume by agent", "turned agents back on", ActivityText.What(E(Today(7, 0), "cadence_resume_agents", "resumed")));
        T.Eq("failure", ActivityTone.Problem, ActivityText.Row(E(Today(7, 0), "cadence_run_job_now", "error", code: "task_scheduler"), Now, Zone).Tone);
        T.Eq("suppressed", "More refusals not shown", ActivityText.Result(E(Today(7, 0), "cadence_save_job", "suppressed")).Text);
        T.Eq("an unknown tool is shown as it is", "cadence_future_tool", ActivityText.What(E(Today(7, 0), "cadence_future_tool", "ok")));
    }

    public static void TheOwnersLinesAreTheirsAndAFakeNameIsNot()
    {
        T.Eq("app", "Cadence (the app)", ActivityText.Who(E(Today(7, 0), "app", "resumed", client: "Cadence (the app)")));
        T.Eq("cli", "the command line", ActivityText.Who(E(Today(7, 0), "cli", "suspended", client: "the command line")));
        T.Eq("an agent calling itself the app", "Cadence (the app) (AI agent)", ActivityText.Who(E(Today(7, 0), "cadence_save_job", "ok", client: "Cadence (the app)")));
        T.Eq("an agent calling itself the command line", "the command line (AI agent)", ActivityText.Who(E(Today(7, 0), "cadence_save_job", "ok", client: "the command line")));
        T.Eq("owner words for the owner's changes", "locked a schedule", ActivityText.What(E(Today(7, 0), "app", "ok", code: "locked")));
        T.Eq("limits", "changed the limits", ActivityText.What(E(Today(7, 0), "cli", "ok", code: "limits_changed")));
        T.Eq("owner suspend", "suspended agents", ActivityText.What(E(Today(7, 0), "app", "suspended")));
    }

    public static void ControlCharactersAreShownNotActedOn()
    {
        ActivityRow row = ActivityText.Row(E(Today(7, 0), "cadence_save_job", "ok", client: "Evil\u202E\u0007Agent", job: "Job\r\nname", detail: "a\nb\u001b[31mred\u2028c"), Now, Zone);
        string all = row.Who + row.What + row.Detail + row.Tooltip + row.Result;
        T.True("no control character in any text", !all.Where(c => c != '\n').Any(char.IsControl));
        T.True("no line or paragraph separator", !all.Contains('\u2028', StringComparison.Ordinal));
        T.True("no bidi override", !all.Contains('\u202E', StringComparison.Ordinal));
        T.True("escapes are visible", row.Detail.Contains("\\n", StringComparison.Ordinal) && row.Detail.Contains("\\u001b", StringComparison.Ordinal));
        T.True("a hand-made tool name is escaped too", !ActivityText.What(E(Today(7, 0), "bad\u0007tool", "ok")).Contains('\u0007', StringComparison.Ordinal));
    }

    public static void RowsAreNewestFirstAndBounded()
    {
        var entries = new List<AuditEntry>();
        for (int i = 0; i < 500; i++) entries.Add(E(Today(0, 0).AddSeconds(i), "cadence_list_jobs", "ok"));
        entries.Reverse();      // any order in
        IReadOnlyList<ActivityRow> rows = ActivityText.Rows(entries, Now, Zone);
        T.Eq("capped", ActivityText.MaxRows, rows.Count);
        T.Eq("newest first", "Today 0:08", rows[0].When);
        T.Eq("oldest shown is 200 back", "Today 0:05", rows[^1].When);
        T.Eq("nothing", 0, ActivityText.Rows([], Now, Zone).Count);
        T.Eq("header: all shown", "3 entries, newest first", ActivityText.Header(3, 3));
        T.Eq("header: one", "1 entry, newest first", ActivityText.Header(1, 1));
        T.Eq("header: cut", "Showing the newest 200 entries", ActivityText.Header(200, 201));
    }

    public static void ALongDetailIsCutAndTheTooltipKeepsIt()
    {
        ActivityRow row = ActivityText.Row(E(Today(7, 0), "cadence_save_job", "refused", code: "bad", detail: new string('x', 290)), Now, Zone);
        T.True("cut with an ellipsis", row.Detail.Length <= 140 && row.Detail.EndsWith('\u2026'));
        T.True("tooltip has it all", row.Tooltip.Contains(new string('x', 290), StringComparison.Ordinal));
    }

    public static void ThirtyOneThousandLinesStayCheap()
    {
        var entries = Enumerable.Range(0, 31_500).Select(i => E(Today(0, 0).AddSeconds(i), "cadence_save_job", "refused", code: "invalid_argument", detail: "Bad request " + i)).ToList();
        IReadOnlyList<ActivityRow> rows = ActivityText.Rows(entries, Now, Zone);
        T.Eq("still the cap", ActivityText.MaxRows, rows.Count);
        T.Eq("the newest", "Bad request 31499", rows[0].Detail);
    }

    public static void TodayCountWords()
    {
        var entries = new[] { E(Today(7, 0), "session", "connected"), E(Today(7, 1), "cadence_list_jobs", "ok"), E(Today(7, 0).AddDays(-2), "session", "connected") };
        T.Eq("two today", "2 entries today", ActivityText.TodayCount(entries, Now, Zone));
        T.Eq("one", "1 entry today", ActivityText.TodayCount(entries.Take(1), Now, Zone));
        T.Eq("none", "Nothing today", ActivityText.TodayCount(entries.Skip(2), Now, Zone));
    }

    public static void TheSampleLogReadsWell()
    {
        IReadOnlyList<ActivityRow> rows = ActivityText.Rows(DemoServices.SampleLog, Now, Zone);
        T.Eq("every sample line has a row", DemoServices.SampleLog.Count, rows.Count);
        T.Eq("newest is the disconnect", "disconnected", rows[0].What);
        T.True("the owner's resume is the owner's", rows.Any(r => r.Who == "Cadence (the app)" && r.What == "turned agents back on"));
    }
}
