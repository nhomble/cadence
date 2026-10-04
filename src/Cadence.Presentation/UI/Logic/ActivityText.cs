using System.Globalization;
using Cadence.Agent.Audit;
using Cadence.Agent.Tools;
using Cadence.Core.Metadata;

namespace Cadence.App.UI.Logic;

/// <summary>How a row of the agent log reads: good news, a refusal or a switch change (Orange), a failure (Red), or just a note (Dim).</summary>
public enum ActivityTone { Quiet, Ok, Notice, Problem }

/// <summary>
/// One line of the "Agent activity" panel: who, when, what and how it went, all plain text (control characters shown as \uXXXX, never acted on).
/// <see cref="Tooltip"/> carries the full line; <see cref="Detail"/> is the cut one.
/// </summary>
public sealed record ActivityRow(string When, string Who, string What, string Result, ActivityTone Tone, string Detail, string Tooltip);

/// <summary>Pure formatting of the agent log for the panel ([P23] item 6): newest first, bounded, escaped, no raw JSON.</summary>
public static class ActivityText
{
    /// <summary>The panel never shows more than this many lines.</summary>
    public const int MaxRows = 200;

    const int DetailChars = 140;

    public const string EmptyText = "No agent activity yet. When an AI agent uses Cadence, what it did shows up here.";

    /// <summary>Entries in any order become at most <see cref="MaxRows"/> rows, newest first.</summary>
    public static IReadOnlyList<ActivityRow> Rows(IEnumerable<AuditEntry> entries, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.OrderByDescending(e => e.At).Take(MaxRows).Select(e => Row(e, now, zone)).ToList();
    }

    public static ActivityRow Row(AuditEntry e, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(e);
        string when = ListFormat.When(e.At, now, zone);
        string who = Who(e);
        string what = What(e);
        (string result, ActivityTone tone) = Result(e);
        string detail = Detail(e);
        string full = when + "  " + who + "  " + what + "  " + result + (detail.Length > 0 ? "\n" + detail : "");
        return new ActivityRow(when, who, what, result, tone, Cut(detail, DetailChars), full);
    }

    /// <summary>The owner's own lines (tool "app" or "cli") are theirs; every other name is an agent's, shown through <see cref="Actor.Describe"/> so a fake "Cadence (the app)" can't pass as the owner.</summary>
    public static string Who(AuditEntry e) => e.Tool switch
    {
        "app" => Actor.App.Describe(),
        "cli" => Actor.Cli.Describe(),
        _ => AuditText.Escape(Actor.Agent(e.Client).Describe()),
    };

    public static string What(AuditEntry e)
    {
        string tool = e.Tool;
        string phrase = tool switch
        {
            ToolCatalog.GetStatus => "checked the status",
            ToolCatalog.ListJobs => "looked at your schedules",
            ToolCatalog.GetJob => "read a schedule",
            ToolCatalog.GetRunOutput => "read a run's output",
            ToolCatalog.PreviewSchedule => "previewed a schedule",
            ToolCatalog.ListTemplates => "looked at the templates",
            ToolCatalog.SaveJob => "saved a schedule",
            ToolCatalog.SetJobEnabled => "switched a schedule on or off",
            ToolCatalog.RunJobNow => "started a schedule",
            ToolCatalog.DeleteJob => "deleted a schedule",
            ToolCatalog.SuspendAgents => "suspended agents",
            ToolCatalog.ResumeAgents => "turned agents back on",
            "session" => e.Outcome switch { "connected" => "connected", "disconnected" => "disconnected", _ => "session" },
            "app" or "cli" => e.Outcome switch { "suspended" => "suspended agents", "resumed" => "turned agents back on", _ => e.Code switch { "locked" => "locked a schedule", "unlocked" => "unlocked a schedule", "limits_changed" => "changed the limits", _ => "changed a setting" } },
            _ => AuditText.Escape(tool),
        };
        return phrase;
    }

    public static (string Text, ActivityTone Tone) Result(AuditEntry e) => e.Outcome switch
    {
        "ok" => ("Done", ActivityTone.Ok),
        "connected" or "disconnected" => ("", ActivityTone.Quiet),
        "refused" => ("Refused" + (e.Code is { Length: > 0 } c ? ": " + CodeWords(c) : ""), ActivityTone.Notice),
        "suppressed" => ("More refusals not shown", ActivityTone.Notice),
        "suspended" => ("Suspended", ActivityTone.Notice),
        "resumed" => ("Turned back on", ActivityTone.Notice),
        "error" => ("Failed" + (e.Code is { Length: > 0 } c2 ? ": " + CodeWords(c2) : ""), ActivityTone.Problem),
        _ => (AuditText.Escape(e.Outcome), ActivityTone.Quiet),
    };

    /// <summary>"schedule_too_frequent" reads "schedule too frequent".</summary>
    static string CodeWords(string code) => AuditText.Escape(code).Replace('_', ' ');

    /// <summary>The second line: the job's name (when there is one) and then what was said about it.</summary>
    static string Detail(AuditEntry e)
    {
        string job = e.JobName is { Length: > 0 } name ? AuditText.Escape(name) : "";
        string text = AuditText.Escape(e.Detail);
        return job.Length == 0 ? text : text.Length == 0 ? job : job + " " + (char)0xB7 + " " + text;
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "\u2026";

    /// <summary>"3 lines today" for the menu item / panel header (calendar day in <paramref name="zone"/>).</summary>
    public static string TodayCount(IEnumerable<AuditEntry> entries, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTime today = TimeZoneInfo.ConvertTime(now, zone).Date;
        int n = entries.Count(e => TimeZoneInfo.ConvertTime(e.At, zone).Date == today);
        return n == 0 ? "Nothing today" : n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " entry today" : " entries today");
    }

    public static string Header(int shown, int available) =>
        available > shown ? "Showing the newest " + shown.ToString(CultureInfo.InvariantCulture) + " entries" : shown == 1 ? "1 entry, newest first" : shown.ToString(CultureInfo.InvariantCulture) + " entries, newest first";
}
