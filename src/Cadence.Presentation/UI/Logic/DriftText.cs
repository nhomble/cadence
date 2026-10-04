using System.Globalization;
using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.Core.Planning;

namespace Cadence.App.UI.Logic;

/// <summary>What kind of notice the top of the editor shows (PLAN 7.2 B, Phase 15).</summary>
public enum BannerKind
{
    /// <summary>Changed in Task Scheduler: Re-apply or Stop managing.</summary>
    Edited,

    /// <summary>Renamed or moved outside Cadence (its wrapper still points at the old path): Re-apply or Stop managing.</summary>
    Renamed,

    /// <summary>Runs as another user: no Re-apply (it would make the job run as you); Stop managing or Delete.</summary>
    DifferentUser,

    /// <summary>A copy of another job (same job id): no Re-apply; Stop managing or Delete.</summary>
    Duplicate,

    /// <summary>Written by a newer Cadence: read-only.</summary>
    NewerVersion,

    /// <summary>Damaged Cadence data: delete only.</summary>
    Corrupt,

    /// <summary>Cadence would write something different today: a quiet line with "Refresh now", not a warning.</summary>
    Outdated,
}

public enum BannerAction
{
    /// <summary>Re-apply Cadence's version.</summary>
    Reapply,

    /// <summary>Stop managing it (Untrack).</summary>
    Untrack,

    Delete,

    /// <summary>Outdated only: write the task again in this version's format.</summary>
    RefreshNow,

    OpenInTaskScheduler,
}

/// <summary>The notice above the form: a title, the English difference lines, a note and the buttons that exist for this state.</summary>
public sealed record BannerState(BannerKind Kind, string Title, IReadOnlyList<string> Lines, string? Note, IReadOnlyList<BannerAction> Actions)
{
    /// <summary>A quiet Dim line (Outdated), not the Yellow warning.</summary>
    public bool IsQuiet => Kind == BannerKind.Outdated;

    /// <summary>The form is shown read-only (everything except Outdated).</summary>
    public bool LocksForm => Kind != BannerKind.Outdated;

    public bool Has(BannerAction action) => Actions.Contains(action);

    /// <summary>The Red outline of a damaged task; every other warning is Yellow.</summary>
    public bool IsDamage => Kind == BannerKind.Corrupt;
}

/// <summary>The wording of one confirmation dialog.</summary>
public sealed record ConfirmText(string Title, string Message, string Confirm);

/// <summary>
/// Pure text and rules for the drift banner, the Outdated line and the Re-apply / Stop managing confirmations (PLAN 3.12, 7.2 B, Phase 15 with
/// the [CP-B] rules). No WPF, no clock. The ruling question is "which buttons exist": plain drift offers exactly Re-apply and Stop managing;
/// a job that runs as another user or is a copy of another job never offers Re-apply.
/// </summary>
public static class DriftText
{
    public const int MaxLines = 6;

    public const string CorruptTitle = "Damaged schedule";
    public const string CorruptMessage = "This task was created by Cadence but its settings are damaged, so Cadence can't edit or run it. Delete it?";
    public const string NewerTitle = "Newer version";
    public const string NewerMessage = "Created by a newer Cadence version; read-only. Update Cadence to edit it. The task itself keeps running as it is.";

    /// <summary>The banner for a classified task, or null when there is nothing to say (InSync, Foreign).</summary>
    public static BannerState? For(ClassifiedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        switch (task.Ownership)
        {
            case Ownership.CorruptMetadata:
                return new BannerState(BannerKind.Corrupt, "This schedule's Cadence settings are damaged.", [],
                    "Cadence can't edit or run it. The only thing left to do here is delete it.",
                    [BannerAction.Delete, BannerAction.OpenInTaskScheduler]);
            case Ownership.NewerVersion:
                return new BannerState(BannerKind.NewerVersion, "This schedule was made by a newer version of Cadence.", [],
                    "It is read-only here so this version can't damage it. Update Cadence to edit it; the task keeps running as it is.",
                    [BannerAction.OpenInTaskScheduler]);
            case Ownership.Managed:
                break;
            default:
                return null;
        }

        if (task.Sync == SyncStatus.Drifted)
        {
            if (task.DuplicateOf is { } original)
                return new BannerState(BannerKind.Duplicate, "This is a copy of \"" + original + "\" that was made outside Cadence.",
                    ["Two schedules can't share one run history."],
                    "Delete this copy, or stop managing it: it then stays in Task Scheduler as a plain task.",
                    [BannerAction.Untrack, BannerAction.Delete]);

            IReadOnlyList<string> lines = DiffLines(task.DriftDiffs);
            if (task.DriftDiffs.Any(d => d.Field == "User"))
                return new BannerState(BannerKind.DifferentUser, "This schedule now runs as a different user.", lines,
                    "Cadence doesn't offer Re-apply here: it would make the schedule run as you instead. Delete it, or stop managing it.",
                    [BannerAction.Untrack, BannerAction.Delete]);

            bool renamedOnly = task.DriftDiffs.Count > 0 && task.DriftDiffs.All(d => d.Field == "Task path");
            return new BannerState(renamedOnly ? BannerKind.Renamed : BannerKind.Edited,
                renamedOnly ? "This schedule was renamed or moved outside Cadence." : "This schedule was changed outside Cadence (in Task Scheduler).",
                lines,
                renamedOnly
                    ? "The form below shows Cadence's version. Re-apply sets it up again under its current name. Stop managing leaves the task as it is, and Cadence lets go of it."
                    : "The form below shows Cadence's version (the left side of each arrow). Re-apply puts it back in Task Scheduler. Stop managing leaves the task as it is now, and Cadence lets go of it.",
                [BannerAction.Reapply, BannerAction.Untrack]);
        }

        if (task.Sync == SyncStatus.Outdated)
            return new BannerState(BannerKind.Outdated, OutdatedLine(task.Problem), [], null, [BannerAction.RefreshNow]);
        return null;
    }

    /// <summary>"Time 18:00 → 19:00", one per line (what Cadence saved, then what Task Scheduler has now); at most <see cref="MaxLines"/> lines.</summary>
    public static IReadOnlyList<string> DiffLines(IReadOnlyList<FieldDiff> diffs)
    {
        ArgumentNullException.ThrowIfNull(diffs);
        var lines = diffs.Select(DiffLine).ToList();
        if (lines.Count <= MaxLines) return lines;
        int more = lines.Count - (MaxLines - 1);
        lines = lines.Take(MaxLines - 1).ToList();
        lines.Add("and " + more.ToString(CultureInfo.InvariantCulture) + " more changes");
        return lines;
    }

    public static string DiffLine(FieldDiff d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d.Field switch
        {
            "Task path" => "Renamed: now \\" + LastPart(d.After) + ", but its Cadence wrapper still points at " + d.Before,
            "User" => "Runs as " + (d.After ?? "(nobody)") + " (Cadence expects " + (d.Before ?? "you") + ")",
            _ => d.Describe(),
        };
    }

    static string LastPart(string? path) => (path ?? "").TrimStart('\\');

    /// <summary>
    /// The quiet line for an Outdated task: "This task needs a quick refresh (its holiday list is running out)." next to the "Refresh now" link.
    /// [CP-C] It used to say "when you save", but Save is only enabled after an edit, so the line promised something the user could not do.
    /// </summary>
    public static string OutdatedLine(string? problem)
    {
        string p = problem ?? "";
        var reasons = new List<string>();
        if (p.Contains("expires soon", StringComparison.Ordinal)) reasons.Add("its holiday list is running out");
        else if (p.Contains("holiday", StringComparison.Ordinal)) reasons.Add("the holiday list changed");
        if (p.Contains("task definition", StringComparison.Ordinal) || p.Contains("version", StringComparison.Ordinal) || p.Contains("rebuild", StringComparison.Ordinal))
            reasons.Add("this version of Cadence writes tasks a little differently");
        string why = reasons.Count == 0 ? "newer format" : string.Join(" and ", reasons);
        return "This task needs a quick refresh (" + why + "). It keeps running meanwhile.";
    }

    // ---- confirmations ----

    /// <summary>"Re-apply Cadence's version of X?" with what will be overwritten.</summary>
    public static ConfirmText ReapplyConfirm(ClassifiedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        string name = task.Job?.Name ?? task.Observed.Name;
        var bullets = new List<string>();
        foreach (FieldDiff d in task.DriftDiffs.Take(MaxLines))
            bullets.Add("• " + Overwrite(d, task));
        if (task.DriftDiffs.Count > MaxLines)
            bullets.Add("• and " + (task.DriftDiffs.Count - MaxLines).ToString(CultureInfo.InvariantCulture) + " more");
        string what = bullets.Count > 0
            ? "Cadence will write its saved version back to Task Scheduler. What was changed there will be overwritten:\n\n" + string.Join("\n", bullets)
            : "Cadence will write its saved version back to Task Scheduler, replacing the task's current definition.";
        return new ConfirmText("Re-apply Cadence's version of \"" + name + "\"?", what + "\n\nThe schedule keeps its run history.", "Re-apply");
    }

    static string Overwrite(FieldDiff d, ClassifiedTask task)
    {
        if (d.Field == "Task path") return "It is registered again under its current name, " + task.Observed.Path + ".";
        if (d.Field == "Enabled") return string.Equals(d.After, "disabled", StringComparison.Ordinal) ? "It is switched back on in Task Scheduler." : "It is switched back off in Task Scheduler.";
        if (d.Before is not null && d.After is not null) return d.Field + ": " + d.After + " in Task Scheduler is replaced by " + d.Before;
        return d.Field + ": Task Scheduler's version is replaced by Cadence's";
    }

    /// <summary>
    /// "Stop managing X?" The consequences are the ones <c>Planner.ForUntrack</c> carries out, in plain words: what the wrapper did for the job stops
    /// (output capture, run history, the time limit, holiday skipping, environment variables, hidden mode). A job whose action was already replaced in
    /// Task Scheduler keeps that action, so only the bookkeeping stops.
    /// </summary>
    public static ConfirmText UntrackConfirm(ClassifiedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        string name = task.Job?.Name ?? task.Observed.Name;
        var lines = new List<string> { "The task stays in Task Scheduler with its triggers and settings exactly as they are now." };
        JobDefinition? job = task.Job;
        bool wrapper = task.Actual is null || task.Actual.ExecActions.Any(a => WrapperScript.LooksLikeWrapper(a.Arguments));
        if (job is not null && wrapper)
        {
            string program = (job.Run.Exe + (job.Run.Args.Length > 0 ? " " + job.Run.Args : "")).Trim();
            lines.Add("It will run " + Short(program) + " directly.");
            lines.Add("Output capture and run history stop.");
            if (job.Run.Timeout is { } limit) lines.Add("The time limit (" + LimitText(limit) + ") no longer applies: a program that runs too long won't be stopped.");
            if (job.Run.Env.Count > 0) lines.Add(job.Run.Env.Count == 1 ? "The environment variable you set is no longer passed to it." : "The " + job.Run.Env.Count.ToString(CultureInfo.InvariantCulture) + " environment variables you set are no longer passed to it.");
            if (job.Schedule.SkipHolidays != HolidayCalendar.None) lines.Add("It will run on holidays too.");
            if (job.Run.Hidden) lines.Add("A console window will appear each time it runs.");
        }
        else lines.Add("Its action is kept as it is. Cadence's run history and output capture for it stop.");
        lines.Add("Cadence will stop showing it here. You can still find it in Task Scheduler" + (task.Observed.Path.Length > 0 ? " at " + task.Observed.Path : "") + ".");

        string message = string.Join("\n", lines.Select(l => "• " + l));
        return new ConfirmText("Stop managing \"" + name + "\"?", message, "Stop managing");
    }

    /// <summary>"30 min", "1 h", "1 h 30 min", "45 s": a time limit in the shortest plain words.</summary>
    static string LimitText(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
        int minutes = (int)Math.Round(t.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + " min";
        int hours = minutes / 60, rest = minutes % 60;
        return hours.ToString(CultureInfo.InvariantCulture) + " h" + (rest > 0 ? " " + rest.ToString(CultureInfo.InvariantCulture) + " min" : "");
    }

    static string Short(string s) => s.Length <= 90 ? s : s[..89] + "…";
}
