using System.Globalization;
using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;

namespace Cadence.Core.Planning;

/// <summary>
/// Turns an intent (save, delete, re-apply, untrack) plus the current classification into a <see cref="PlannedChange"/> with its English summary.
/// Pure. Refuses (<see cref="PlanRefusedException"/>) to plan anything that would touch a Foreign or NewerVersion task, or anything but a delete of a
/// CorruptMetadata task. <see cref="ComposeException"/> from the composer (cron, validation, size) propagates with its message unchanged.
/// </summary>
public static class Planner
{
    public const string UntrackedNote = " (no longer managed by Cadence)";

    /// <summary>Create (existing is null), Update, Rename (the sanitized display name changed), or Upgrade (nothing changed but the task is Outdated).</summary>
    public static PlannedChange ForSave(JobDefinition edited, ClassifiedTask? existing, IReadOnlySet<string> takenNames, ComposeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(takenNames);
        ArgumentNullException.ThrowIfNull(ctx);

        if (existing is null)
        {
            string name = TaskNaming.ToTaskName(edited.Name, takenNames);
            ComposeContext cctx = ctx with { TaskName = name, Created = null, PreviousNames = ValueList<string>.Empty, CreatedBy = null };
            string xml = TaskComposer.ComposeXml(edited, cctx);
            return new PlannedChange(ChangeKind.Create, edited.Id, null, name, xml, ValueList<FieldDiff>.Empty,
                "Will create " + TaskComposer.TaskPath(cctx));
        }

        RequireManaged(existing, "save");
        RequireNotCopy(existing, "save");
        CadenceMetadata meta = existing.Metadata!;
        if (!string.Equals(edited.Id, meta.Job.Id, StringComparison.Ordinal))
            throw new ArgumentException("The job id of an existing job can't change (" + meta.Job.Id + " vs " + edited.Id + ").", nameof(edited));

        string current = existing.Observed.Name;
        string newName = ChooseName(edited, meta.Job, current, takenNames);
        bool rename = !string.Equals(newName, current, StringComparison.Ordinal);

        ValueList<string> prev = rename ? new ValueList<string>(meta.Prev.Append(current)) : meta.Prev;
        ComposeContext uctx = ctx with { TaskName = newName, Created = meta.Created, PreviousNames = prev, CreatedBy = meta.CreatedBy };
        TaskDefinitionModel model = TaskComposer.Compose(edited, uctx);
        string newXml = TaskXmlWriter.Write(model);

        var changes = new List<FieldDiff>();
        var projection = ProjectionDiff.Between(Neutral(existing.Actual!), Neutral(model));
        JobDiff(meta.Job, edited, projection.Count > 0, changes);
        changes.AddRange(projection);

        string path = TaskComposer.TaskPath(uctx);
        if (rename)
        {
            string rest = Joined(changes.Where(c => c.Field != "Name"));
            return new PlannedChange(ChangeKind.Rename, edited.Id, current, newName, newXml, new ValueList<FieldDiff>(changes),
                "Will rename to " + path + (rest.Length > 0 ? " and update: " + rest : ""));
        }
        if (changes.Count == 0)
        {
            if (existing.Sync == SyncStatus.Outdated)
                return new PlannedChange(ChangeKind.Upgrade, edited.Id, current, current, newXml, ValueList<FieldDiff>.Empty,
                    "Will bring " + path + " up to date with this version of Cadence (your job stays the same)");
            return new PlannedChange(ChangeKind.Update, edited.Id, current, current, null, ValueList<FieldDiff>.Empty, "No changes to save");
        }
        return new PlannedChange(ChangeKind.Update, edited.Id, current, current, newXml, new ValueList<FieldDiff>(changes),
            "Will update: " + Joined(changes));
    }

    /// <summary>Deleting is allowed for Managed and CorruptMetadata tasks only.</summary>
    public static PlannedChange ForDelete(ClassifiedTask existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.Ownership is not (Ownership.Managed or Ownership.CorruptMetadata))
            throw Refusal(existing, "delete");
        return new PlannedChange(ChangeKind.Delete, existing.Job?.Id ?? "", existing.Observed.Name, null, null, ValueList<FieldDiff>.Empty,
            "Will delete " + existing.Observed.Path);
    }

    /// <summary>Re-registers Cadence's version of the job under the task's current name; edits made in Task Scheduler are discarded.</summary>
    public static PlannedChange ForReapply(ClassifiedTask drifted, ComposeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(drifted);
        ArgumentNullException.ThrowIfNull(ctx);
        RequireManaged(drifted, "re-apply");
        RequireNotCopy(drifted, "re-apply");
        if (drifted.DriftDiffs.Any(d => d.Field == "User"))
            throw new PlanRefusedException("'" + drifted.Observed.Name + "' runs as a different user. Re-applying would make it run as you, so Cadence will not re-apply it; delete it, or stop managing it.");
        CadenceMetadata meta = drifted.Metadata!;
        string name = drifted.Observed.Name;
        ComposeContext rctx = ctx with { TaskName = name, Created = meta.Created, PreviousNames = meta.Prev, CreatedBy = meta.CreatedBy };
        string xml = TaskComposer.ComposeXml(meta.Job, rctx);
        string what = drifted.DriftDiffs.Count > 0
            ? " (discards the changes made in Task Scheduler: " + Joined(drifted.DriftDiffs) + ")"
            : "";
        return new PlannedChange(ChangeKind.Reapply, meta.Job.Id, name, name, xml, drifted.DriftDiffs,
            "Will re-apply Cadence's version of " + TaskComposer.TaskPath(rctx) + what);
    }

    /// <summary>
    /// Stops managing the task but keeps it working. The new definition is built from the LIVE definition (Actual): Documentation and Source
    /// removed, " (no longer managed by Cadence)" appended to the Description, and, if the action is still the Cadence wrapper, replaced by a
    /// direct Exec of the job's program. Triggers and settings stay exactly as they are now.
    /// </summary>
    public static PlannedChange ForUntrack(ClassifiedTask managed)
    {
        ArgumentNullException.ThrowIfNull(managed);
        RequireManaged(managed, "stop managing");
        TaskDefinitionModel actual = managed.Actual!;
        RunSpec run = managed.Metadata!.Job.Run;

        bool replaced = false;
        var actions = new List<ExecActionModel>();
        foreach (ExecActionModel a in actual.ExecActions)
        {
            if (WrapperScript.LooksLikeWrapper(a.Arguments))
            {
                replaced = true;
                actions.Add(new ExecActionModel(run.Exe, run.Args.Length == 0 ? null : run.Args, string.IsNullOrEmpty(run.WorkingDirectory) ? null : run.WorkingDirectory));
            }
            else actions.Add(a);
        }

        string description = UntrackedDescription(actual.RegistrationInfo.Description, managed.Metadata.Job.Schedule.SkipHolidays);
        var model = actual with
        {
            RegistrationInfo = actual.RegistrationInfo with { Documentation = null, Source = null, Description = description, Uri = null },
            ExecActions = new ValueList<ExecActionModel>(actions),
        };

        var changes = new List<FieldDiff> { new("Managed by Cadence", "yes", "no") };
        if (replaced) changes.Add(new FieldDiff("Program", "Cadence wrapper", run.Exe));
        string path = managed.Observed.Path;
        string kept = replaced
            ? "it will run " + run.Exe + " directly; output capture, run history, the timeout, holiday skipping, environment variables and hidden mode stop"
            : "its action is kept as it is";
        return new PlannedChange(ChangeKind.Untrack, managed.Metadata.Job.Id, managed.Observed.Name, managed.Observed.Name, TaskXmlWriter.Write(model),
            new ValueList<FieldDiff>(changes), "Will stop managing " + path + ": the task stays in Task Scheduler (" + kept + ")");
    }

    // ---- helpers ----

    /// <summary>
    /// The Description of an untracked task: Cadence's "Managed by Cadence - edit it in the Cadence app." sentence is removed, and so is the
    /// holiday clause (only the wrapper skips holidays, and an untracked task has no wrapper), then <see cref="UntrackedNote"/> is appended.
    /// Text the user wrote in Task Scheduler is otherwise kept. (Checkpoint B: before, the untracked task still claimed both.)
    /// </summary>
    static string UntrackedDescription(string? live, HolidayCalendar skip)
    {
        string text = (live ?? "").Trim();
        if (text.EndsWith(TaskComposer.DescriptionSuffix, StringComparison.Ordinal))
            text = text[..^TaskComposer.DescriptionSuffix.Length];
        string clause = EnglishRenderer.HolidayClause(skip);
        if (clause.Length > 0 && text.EndsWith(clause, StringComparison.Ordinal))
            text = text[..^clause.Length];
        return (text.TrimEnd() + UntrackedNote).TrimStart();
    }

    /// <summary>Keeps the current task name unless the sanitized display name changed.</summary>
    static string ChooseName(JobDefinition edited, JobDefinition old, string current, IReadOnlySet<string> takenNames)
    {
        if (string.Equals(TaskNaming.Sanitize(edited.Name), TaskNaming.Sanitize(old.Name), StringComparison.Ordinal)) return current;
        var others = new HashSet<string>(takenNames.Where(n => !string.Equals(n, current, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        string candidate = TaskNaming.ToTaskName(edited.Name, others);
        return string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase) ? current : candidate;
    }

    /// <summary>Wrapper actions differ only by task name/hidden mode, which the job-level diff reports; hide them from the field diff.</summary>
    static TaskDefinitionModel Neutral(TaskDefinitionModel m) => m with
    {
        ExecActions = new ValueList<ExecActionModel>(m.ExecActions.Select(a =>
            WrapperScript.LooksLikeWrapper(a.Arguments) ? new ExecActionModel("Cadence wrapper", null, null) : a)),
    };

    static void JobDiff(JobDefinition a, JobDefinition b, bool triggersChanged, List<FieldDiff> list)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) list.Add(new FieldDiff("Name", a.Name, b.Name));
        if (a.AgentLock != b.AgentLock) list.Add(new FieldDiff("Agents", a.AgentLock ? "may not change it" : "may change it", b.AgentLock ? "may not change it" : "may change it"));
        if (!a.Schedule.CronLines.SequenceEqual(b.Schedule.CronLines) && !triggersChanged)
            list.Add(new FieldDiff("Schedule", string.Join("; ", a.Schedule.CronLines), string.Join("; ", b.Schedule.CronLines)));
        if (a.Schedule.SkipHolidays != b.Schedule.SkipHolidays)
            list.Add(new FieldDiff("Skip holidays", Cal(a.Schedule.SkipHolidays), Cal(b.Schedule.SkipHolidays)));
        RunSpec x = a.Run, y = b.Run;
        if (!string.Equals(x.Exe, y.Exe, StringComparison.Ordinal)) list.Add(new FieldDiff("Program", x.Exe, y.Exe));
        if (!string.Equals(x.Args, y.Args, StringComparison.Ordinal)) list.Add(new FieldDiff("Arguments", x.Args, y.Args));
        if (!string.Equals(x.WorkingDirectory ?? "", y.WorkingDirectory ?? "", StringComparison.Ordinal))
            list.Add(new FieldDiff("Working folder", x.WorkingDirectory ?? "(none)", y.WorkingDirectory ?? "(none)"));
        if (!x.Env.SequenceEqual(y.Env)) list.Add(new FieldDiff("Environment", Count(x.Env.Count), Count(y.Env.Count)));
        if (x.Timeout != y.Timeout) list.Add(new FieldDiff("Timeout", Timeout(x.Timeout), Timeout(y.Timeout)));
        if (x.Hidden != y.Hidden) list.Add(new FieldDiff("Window", x.Hidden ? "hidden" : "visible", y.Hidden ? "hidden" : "visible"));
        if (x.Encoding != y.Encoding) list.Add(new FieldDiff("Output encoding", x.Encoding.ToString(), y.Encoding.ToString()));
        if (!string.Equals(x.SourcePath ?? "", y.SourcePath ?? "", StringComparison.Ordinal))
            list.Add(new FieldDiff("Source file", x.SourcePath ?? "(none)", y.SourcePath ?? "(none)"));
    }

    static string Cal(HolidayCalendar c) => c switch { HolidayCalendar.UsFederal => "US federal", HolidayCalendar.Nyse => "NYSE", _ => "none" };

    static string Count(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " variable" : " variables");

    static string Timeout(TimeSpan? t) => t is { } v ? ((long)v.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s" : "none";

    /// <summary>Up to four descriptions, first letter lower-cased, joined by "; ".</summary>
    static string Joined(IEnumerable<FieldDiff> diffs)
    {
        var all = diffs.Select(d => Lower(d.Describe())).ToList();
        string s = string.Join("; ", all.Take(4));
        return all.Count > 4 ? s + "; and " + (all.Count - 4).ToString(CultureInfo.InvariantCulture) + " more" : s;
    }

    static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    static void RequireManaged(ClassifiedTask t, string verb)
    {
        if (t.Ownership != Ownership.Managed || t.Metadata is null || t.Actual is null || t.Job is null) throw Refusal(t, verb);
    }

    /// <summary>A task that shares its job id with an older one (<see cref="DuplicateJobIds"/>) is never saved or re-applied: two tasks would share one run history.</summary>
    static void RequireNotCopy(ClassifiedTask t, string verb)
    {
        if (t.DuplicateOf is { } original)
            throw new PlanRefusedException("'" + t.Observed.Name + "' is a copy of '" + original + "' made outside Cadence, so Cadence will not " + verb
                + " it (two tasks would share one run history). Delete it, or stop managing it.");
    }

    public static PlanRefusedException Refusal(ClassifiedTask t, string verb)
    {
        string who = "'" + t.Observed.Name + "'";
        return t.Ownership switch
        {
            Ownership.Foreign => new PlanRefusedException(who + " was not created by Cadence, so Cadence will not " + verb + " it."),
            Ownership.NewerVersion => new PlanRefusedException(who + " was created by a newer version of Cadence and is read-only here; Cadence will not " + verb + " it."),
            Ownership.CorruptMetadata => new PlanRefusedException(who + " has damaged Cadence data, so the only thing Cadence can do with it is delete it (not " + verb + ")."),
            _ => new PlanRefusedException(who + " can't be changed (cannot " + verb + " it)."),
        };
    }
}
