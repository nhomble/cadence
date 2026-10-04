using System.Globalization;
using Cadence.App.Cli;
using Cadence.Core.Cron;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.History;

namespace Cadence.App.UI.Logic;

/// <summary>The colored dot of a list row. Decided by <see cref="ListFormat.Badge"/> from run records and the sync status, never from Task Scheduler's result code.</summary>
public enum BadgeKind
{
    /// <summary>Never ran (a hollow dim ring).</summary>
    None,
    Ok,
    Failed,
    TimedOut,

    /// <summary>Task Scheduler started the job but no run record exists (Red outline).</summary>
    NotReported,
    Running,

    /// <summary>The newest run was a holiday skip (neutral, not a failure).</summary>
    Skipped,

    /// <summary>Changed outside Cadence (Yellow).</summary>
    Drifted,
    Disabled,

    /// <summary>A corrupt or newer-version task (warning icon).</summary>
    Warning,
}

/// <summary>The color of a table cell's text ([P25]): problems are colored, everything else is Fg or DimText. There are no dots; words carry the state.</summary>
public enum TextTone { Normal, Dim, Red, Orange, Yellow, Cyan }

/// <summary>What the State column says when it is not empty. The order is the State column's sort order (problems first).</summary>
public enum RowState { Damaged, Changed, Newer, Running, Disabled, Normal }

/// <summary>What a row sorts by, filled in when the row is described so sorting itself is a cheap comparison ([P25], <see cref="GridLogic.Compare"/>).</summary>
public sealed record RowSortKeys(int RateGroup, double RunsPerWeek, int ResultRank, DateTimeOffset? ResultAt, double? AvgSeconds, double? SuccessRate, RowState State)
{
    public static RowSortKeys None { get; } = new(2, 0, int.MaxValue, null, null, null, RowState.Normal);
}

/// <summary>
/// The display strings of one schedule row (PLAN 7.2 A). Pure data so it can be unit tested without WPF. The members after <see cref="Locked"/> are the
/// cells of the [P25] table; they are optional so older positional constructions still compile.
/// </summary>
public sealed record RowText(
    string Name,
    string Schedule,
    string Next,
    string ResultTitle,
    string ResultDetail,
    BadgeKind Badge,
    BadgeKind Tone,
    string? BadgeTooltip,
    string? ResultTooltip,
    string? Note,
    bool IsProblem,
    bool IsEnabled,
    string? Updated = null,
    string? UpdatedTooltip = null,
    bool Locked = false,
    RateInfo? Rate = null,
    DateTimeOffset? NextAt = null,
    string NextCell = "",
    string ResultCell = "",
    TextTone ResultTone = TextTone.Normal,
    string StateText = "",
    TextTone StateTone = TextTone.Normal,
    string AvgText = "-",
    string SuccessText = "-",
    string UpdatedCell = "",
    DateTimeOffset? UpdatedAt = null,
    string ProgramPath = "",
    string ProgramArgs = "",
    RowSortKeys? SortKeys = null,
    bool HasProblem = false)
{
    /// <summary>The Rate column's text ("every 15 min"); empty for a row without one (damaged, newer version).</summary>
    public string RateText => Rate?.Label ?? "";

    /// <summary>The tooltip of the Rate cell; a disabled schedule says what it would be.</summary>
    public string? RateTooltip => Rate is null ? null : (IsEnabled ? "" : "Disabled. When enabled: ") + Rate.Tooltip;

    public RowSortKeys Keys => SortKeys ?? RowSortKeys.None;
}

/// <summary>The display strings of one read-only "other task" row. The members after <see cref="IsEnabled"/> are for the [P25] table.</summary>
public sealed record ForeignText(string Name, string Folder, string Next, string Result, bool IsEnabled,
    DateTimeOffset? NextAt = null, string NextCell = "", TextTone ResultTone = TextTone.Normal, int ResultRank = 5, DateTimeOffset? ResultAt = null);

/// <summary>Pure formatting for the list, the empty-state cards and the foreign section (no WPF, no clock, no time zone of its own).</summary>
public static class ListFormat
{
    /// <summary>The Task Scheduler code of a NotReported row when it is a launch failure (Windows could not start the job at all), else null.</summary>
    public static int? LaunchFailureCode(RunView run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Status != RunStatus.NotReported) return null;
        if (run.ExitCode is { } c && TaskResultCodes.IsLaunchFailure(c)) return c;
        return TaskResultCodes.LaunchFailureCodeOf(run.Message);
    }

    public const string DidntReportTitle = "Didn't report";
    public const string DriftNote = "Changed in Task Scheduler";

    // ---- time words ----

    /// <summary>"just now", "5 min ago", "2 h ago", "yesterday", "3 d ago", then "12 Oct" (and the year when it differs). Calendar days are in <paramref name="tz"/>.</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo tz)
    {
        TimeSpan age = now - when;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";
        if (age < TimeSpan.FromHours(24)) return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago";
        DateTime w = TimeZoneInfo.ConvertTime(when, tz).DateTime;
        DateTime n = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        int days = Math.Max(1, (n.Date - w.Date).Days);
        if (days == 1) return "yesterday";
        if (days < 7) return days.ToString(CultureInfo.InvariantCulture) + " d ago";
        return w.ToString(w.Year == n.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>"Today 18:00", "Tomorrow 8:00", then "Thu 26 Nov 18:00" (the year only when it differs).</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo tz)
    {
        DateTime a = TimeZoneInfo.ConvertTime(at, tz).DateTime;
        DateTime n = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        string time = a.ToString("H:mm", CultureInfo.InvariantCulture);
        int days = (a.Date - n.Date).Days;
        if (days == 0) return "Today " + time;
        if (days == 1) return "Tomorrow " + time;
        return a.ToString(a.Year == n.Year ? "ddd d MMM H:mm" : "ddd d MMM yyyy H:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>The "Next:" text of a row.</summary>
    public static string NextText(DateTimeOffset? next, bool enabled, DateTimeOffset now, TimeZoneInfo tz)
    {
        if (!enabled) return "Disabled";
        return next is { } at ? "Next: " + When(at, now, tz) : "No upcoming run";
    }

    /// <summary>The first run that will really start (a holiday-skipped occurrence is passed over); null for a disabled or uncompilable job.</summary>
    public static DateTimeOffset? NextRun(ScheduleSpec schedule, bool enabled, DateTimeOffset now, TimeZoneInfo tz, string userId = "user")
    {
        if (!enabled) return null;
        CompiledSchedule compiled = CronCompiler.Compile(schedule, new MapContext(userId));
        if (!compiled.Success || compiled.Triggers.Count == 0) return null;
        // An "at sign-in" schedule has no clock time.
        IReadOnlyList<Occurrence> runs = NextRuns.Next(compiled.Triggers, schedule.SkipHolidays, now, 8, tz);
        foreach (Occurrence o in runs)
            if (o.SkippedHoliday is null) return o.At;
        return null;
    }

    /// <summary>The "Next: ..." line of a template card ("When you sign in" for @logon).</summary>
    public static string TemplateNext(Template template, DateTimeOffset now, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Cron.StartsWith("@logon", StringComparison.OrdinalIgnoreCase)) return "Runs when you sign in";
        var spec = new ScheduleSpec(ValueList.Of(template.Cron), template.SuggestedSkip, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime));
        return NextText(NextRun(spec, true, now, tz), true, now, tz);
    }

    // ---- the row ----

    /// <summary>The badge rule (3.13 [CP-B]): problems are Warning, a drifted job Yellow, a disabled job Dim, else the newest run decides.</summary>
    public static BadgeKind Badge(ClassifiedTask task, JobHistory history, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(history);
        if (task.Ownership is Ownership.CorruptMetadata or Ownership.NewerVersion) return BadgeKind.Warning;
        if (task.Sync == SyncStatus.Drifted) return BadgeKind.Drifted;
        if (!enabled) return BadgeKind.Disabled;
        RunView? newest = history.Recent.Count > 0 ? history.Recent[0] : null;
        if (newest?.Status == RunStatus.Running) return BadgeKind.Running;
        if (newest?.Status == RunStatus.Skipped) return BadgeKind.Skipped;
        return history.Summary.LastResult switch
        {
            RunStatus.Ok => BadgeKind.Ok,
            RunStatus.Failed or RunStatus.LaunchError => BadgeKind.Failed,
            RunStatus.TimedOut => BadgeKind.TimedOut,
            RunStatus.NotReported => BadgeKind.NotReported,
            RunStatus.Running => BadgeKind.Running,
            RunStatus.Skipped => BadgeKind.Skipped,
            _ => BadgeKind.None,
        };
    }

    /// <param name="rate">The row's rate when the caller memoized it (<see cref="RateCache"/>); null counts it here.</param>
    public static RowText Describe(ClassifiedTask task, JobHistory history, DateTimeOffset? next, DateTimeOffset now, TimeZoneInfo tz, RateInfo? rate = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(history);

        if (task.Ownership is Ownership.CorruptMetadata or Ownership.NewerVersion)
        {
            bool corrupt = task.Ownership == Ownership.CorruptMetadata;
            return new RowText(task.Observed.Name,
                corrupt ? "Damaged settings - this task can only be deleted" : "Created by a newer version of Cadence - read-only",
                "", "", "", BadgeKind.Warning, BadgeKind.Warning, task.Problem, null, null, IsProblem: true, IsEnabled: true,
                StateText: corrupt ? "Damaged settings" : "Newer Cadence version", StateTone: corrupt ? TextTone.Red : TextTone.Yellow, AvgText: "", SuccessText: "",
                SortKeys: new RowSortKeys(4, 0, int.MaxValue, null, null, null, corrupt ? RowState.Damaged : RowState.Newer), HasProblem: true);
        }

        JobDefinition? job = task.Job;
        bool enabled = job?.Enabled ?? false;
        // A copy made outside Cadence carries its original's job (and name): its own task name tells the two rows apart.
        string name = task.DuplicateOf is not null ? task.Observed.Name : job?.Name ?? task.Observed.Name;
        string schedule = job is null ? "" : EnglishRenderer.Describe(job.Schedule);
        BadgeKind badge = Badge(task, history, enabled);
        // The result text keeps the color of the last run even when the dot says drifted or disabled.
        BadgeKind tone = Badge(task with { Sync = SyncStatus.InSync }, history, enabled: true);
        (string title, string detail, string? tip) = Result(history, now, tz);

        string? badgeTip = badge switch
        {
            BadgeKind.Drifted => task.DuplicateOf is { } of ? "This is a copy of \"" + of + "\" made outside Cadence." : "This schedule was changed outside Cadence (in Task Scheduler).",
            BadgeKind.Disabled => "Disabled",
            BadgeKind.Running => "Running now",
            BadgeKind.Skipped => "Skipped on a holiday",
            BadgeKind.NotReported => tip,
            _ => null,
        };
        string? note = task.DuplicateOf is { } original ? "A copy of \"" + original + "\"" : task.Sync == SyncStatus.Drifted ? DriftNoteFor(task) : null;

        // [P25] the table cells: one value each, color only for problems and "Running".
        rate ??= RateFormat.ForTask(task, now, tz);
        (string stateText, TextTone stateTone, RowState state) = State(task, badge, enabled);
        string cell = ResultCell(history, title, detail, now, tz);
        TextTone resultTone = tone switch
        {
            BadgeKind.Failed or BadgeKind.NotReported => TextTone.Red,
            BadgeKind.TimedOut => TextTone.Orange,
            BadgeKind.Running => TextTone.Cyan,
            BadgeKind.Skipped or BadgeKind.None => TextTone.Dim,
            _ => TextTone.Normal,
        };
        JobSummary summary = history.Summary;
        string avg = summary.AverageRuntime is { } runtime ? CliFormat.Duration(runtime) : "-";
        string success = summary.SuccessRate is { } rateValue ? Math.Round(rateValue * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "-";
        (string updatedCell, DateTimeOffset? updatedAt) = AgentText.UpdatedCell(task, now, tz);
        int rateGroup = !enabled ? 3 : rate.Kind switch { RateKind.Timed or RateKind.Rare => 0, RateKind.AtSignIn or RateKind.EventOnly => 1, _ => 2 };
        var keys = new RowSortKeys(rateGroup, rate.RunsPerWeek, ResultRank(tone, history), history.Recent.Count > 0 ? ResultTime(history) : null,
            summary.AverageRuntime?.TotalSeconds, summary.SuccessRate, state);
        string nextCell = !enabled ? "-" : next is { } at ? When(at, now, tz) : rate.Kind == RateKind.AtSignIn ? "At sign-in" : "None";
        bool problem = tone is BadgeKind.Failed or BadgeKind.NotReported or BadgeKind.TimedOut || state == RowState.Changed;
        string program = job?.Run.SourcePath ?? job?.Run.Exe ?? "";

        // [P23] "Updated 2 h ago by Claude Code" (every "by ..." through Actor.Describe) and the lock; null for a task without Cadence data.
        return new RowText(name, schedule, NextText(next, enabled, now, tz), title, detail, badge, tone, badgeTip, tip, note, IsProblem: false, IsEnabled: enabled,
            Updated: AgentText.UpdatedLine(task, now, tz), UpdatedTooltip: AgentText.RowTooltip(task, now, tz), Locked: job?.AgentLock == true,
            Rate: rate, NextAt: enabled ? next : null, NextCell: nextCell, ResultCell: cell, ResultTone: resultTone, StateText: stateText, StateTone: stateTone,
            AvgText: avg, SuccessText: success, UpdatedCell: updatedCell, UpdatedAt: updatedAt, ProgramPath: program, ProgramArgs: job?.Run.Args ?? "", SortKeys: keys, HasProblem: problem);
    }

    /// <summary>The State column: empty when the schedule is enabled, in sync and idle; else one phrase (a problem in Yellow, "Running now" in Cyan, "Disabled" dim).</summary>
    static (string Text, TextTone Tone, RowState State) State(ClassifiedTask task, BadgeKind badge, bool enabled)
    {
        if (task.Sync == SyncStatus.Drifted)
        {
            string text = task.DuplicateOf is { } of ? "Copy of \"" + of + "\"" : DriftNoteFor(task);
            return (text, TextTone.Yellow, RowState.Changed);
        }
        if (badge == BadgeKind.Running) return ("Running now", TextTone.Cyan, RowState.Running);
        if (!enabled) return ("Disabled", TextTone.Dim, RowState.Disabled);
        return ("", TextTone.Normal, RowState.Normal);
    }

    /// <summary>"Ok · 14 min ago": the result and when, without the duration (that is the Avg time column). "Never run" for a schedule without records.</summary>
    static string ResultCell(JobHistory history, string title, string detail, DateTimeOffset now, TimeZoneInfo tz)
    {
        if (history.Recent.Count == 0) return "Never run";
        RunView newest = history.Recent[0];
        if (newest.Status is RunStatus.Running or RunStatus.Skipped) return detail.Length > 0 ? title + " · " + detail : title;
        // "Couldn't start (access denied)" fills the column on its own: the time is in the tooltip and in the editor.
        if (title.StartsWith("Couldn't start (", StringComparison.Ordinal) && ResultRow(history).Status == RunStatus.NotReported) return title;
        return title + " · " + Ago(ResultRow(history).When, now, tz);
    }

    static RunView ResultRow(JobHistory history) =>
        history.Recent.FirstOrDefault(r => r.Status is not (RunStatus.Skipped or RunStatus.Running)) ?? history.Recent[0];

    static DateTimeOffset ResultTime(JobHistory history) =>
        history.Recent[0].Status is RunStatus.Running or RunStatus.Skipped ? history.Recent[0].When : ResultRow(history).When;

    /// <summary>The Last result sort rank: problems first, then Running, Skipped, Ok, never run.</summary>
    static int ResultRank(BadgeKind tone, JobHistory history) => history.Recent.Count == 0 ? 5 : tone switch
    {
        BadgeKind.Failed or BadgeKind.NotReported => 0,
        BadgeKind.TimedOut => 1,
        BadgeKind.Running => 2,
        BadgeKind.Skipped => 3,
        BadgeKind.Ok => 4,
        _ => 5,
    };

    /// <summary>The yellow line of a drifted row: what kind of change it was.</summary>
    static string DriftNoteFor(ClassifiedTask task) => DriftText.For(task)?.Kind switch
    {
        BannerKind.DifferentUser => "Runs as a different user",
        BannerKind.Renamed => "Renamed or moved outside Cadence",
        _ => DriftNote,
    };

    /// <summary>The right-hand result text: title (colored by the badge) and a dim detail line.</summary>
    public static (string Title, string Detail, string? Tooltip) Result(JobHistory history, DateTimeOffset now, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Recent.Count == 0) return ("No runs yet", "", null);
        RunView newest = history.Recent[0];
        if (newest.Status == RunStatus.Running) return ("Running…", "started " + Ago(newest.When, now, tz), null);
        if (newest.Status == RunStatus.Skipped)
            return ("Skipped", string.IsNullOrWhiteSpace(newest.Message) ? Ago(newest.When, now, tz) : newest.Message + " · " + Ago(newest.When, now, tz), null);

        RunView row = history.Recent.FirstOrDefault(r => r.Status is not (RunStatus.Skipped or RunStatus.Running)) ?? newest;
        string ago = Ago(row.When, now, tz);
        string withDuration = row.Duration is { } d ? ago + " · " + CliFormat.Duration(d) : ago;
        switch (row.Status)
        {
            case RunStatus.Ok:
                return ("Ok", withDuration, null);
            case RunStatus.Failed:
                return (row.ExitCode is { } code ? "Failed (exit " + code.ToString(CultureInfo.InvariantCulture) + ")" : "Failed", withDuration, row.Message);
            case RunStatus.TimedOut:
                return ("Timed out", ago, row.Message);
            case RunStatus.LaunchError:
                return ("Couldn't start", ago, row.Message);
            case RunStatus.NotReported:
            {
                // The list says it in a few neutral words (the full sentence wrapped onto two lines in the row); the tooltip and the editor keep it all.
                if (LaunchFailureCode(row) is { } launch && TaskResultCodes.ListLabel(launch) is { } label)
                    return (label, ago, row.Message + "\nLast tried " + ago + ".");
                return (DidntReportTitle, ago, row.Message);
            }
            default:
                return ("No runs yet", "", null);
        }
    }

    // ---- sorting ----

    /// <summary>Alphabetical (case-insensitive, then ordinal), with warning rows last.</summary>
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> rows, Func<T, RowText> text) =>
        rows.OrderBy(r => text(r).IsProblem)
            .ThenBy(r => text(r).Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => text(r).Name, StringComparer.Ordinal)
            .ToList();

    // ---- foreign (read-only) rows ----

    public static ForeignText DescribeForeign(ClassifiedTask task, DateTimeOffset now, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObservedTask o = task.Observed;
        bool enabled = task.Actual?.Settings.Enabled ?? o.State != 1;
        string path = o.Path;
        int cut = path.LastIndexOf('\\');
        string folder = cut > 0 ? path[..cut] : "\\";
        string name = cut >= 0 && cut + 1 < path.Length ? path[(cut + 1)..] : o.Name;

        string next = !enabled ? "Disabled"
            : o.NextRunTime is { } nr ? "Next: " + When(ToOffset(nr, tz), now, tz) : "No upcoming run";

        string result = "Never run";
        TextTone tone = TextTone.Dim;
        int rank = 5;
        DateTimeOffset? resultAt = null;
        if (o.LastRunTime is { } lr && lr.Year >= 2000 && o.LastTaskResult is { } code)
        {
            string text = code == 0 ? "Ok" : TaskResultCodes.Describe(code);
            resultAt = ToOffset(lr, tz);
            result = text + " · " + Ago(resultAt.Value, now, tz);
            // 0x4130x are Task Scheduler's own status words (ready, running, not run yet), not failures.
            bool failure = code != 0 && ((uint)code is < 0x00041300 or > 0x000413FF);
            tone = failure ? TextTone.Red : TextTone.Normal;
            rank = failure ? 0 : 4;
        }
        DateTimeOffset? nextAt = enabled && o.NextRunTime is { } n ? ToOffset(n, tz) : null;
        string nextCell = !enabled ? "-" : nextAt is { } at ? When(at, now, tz) : "None";
        return new ForeignText(name, folder, next, result, enabled, nextAt, nextCell, tone, rank, resultAt);
    }

    static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo tz)
    {
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, tz.GetUtcOffset(unspecified));
    }
}
