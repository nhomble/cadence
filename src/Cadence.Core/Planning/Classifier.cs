using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;

namespace Cadence.Core.Planning;

/// <summary>
/// Builds the compose context for a task already in Task Scheduler: FolderName/RunsRootName from the options, UserId from the gateway,
/// TaskName = the current task name, Created/PreviousNames from the metadata, Now = metadata.Updated (so the expected output is reproducible).
/// </summary>
public delegate ComposeContext ComposeContextFactory(CadenceMetadata metadata, string taskName);

/// <summary>Decides who owns a task and whether it is in sync (PLAN 3.12 with the [CP-A] rules 3a-3e). Pure: no Task Scheduler, no clock, no SIDs.</summary>
public static class Classifier
{
    /// <summary>Re-register when the holiday list ends within this many days.</summary>
    public const int HolidayRenewalDays = 365;

    /// <param name="task">The task as read from Task Scheduler.</param>
    /// <param name="ctxFactory">Context for composing the task as Cadence would write it today.</param>
    /// <param name="today">The local date (never read from a clock here).</param>
    /// <param name="isConnectedUser">Whether a principal's UserId (a SID or DOMAIN\user) is the connected user (Core cannot translate SIDs).</param>
    public static ClassifiedTask Classify(ObservedTask task, ComposeContextFactory ctxFactory, DateOnly today, Func<string?, bool> isConnectedUser)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(ctxFactory);
        ArgumentNullException.ThrowIfNull(isConnectedUser);

        // 1. Parse.
        TaskDefinitionModel actual;
        try { actual = TaskXmlReader.Read(task.Xml ?? ""); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {   // TaskXmlException for what the reader understands; anything else a foreign task could provoke is treated the same way.
            bool ours = (task.Xml ?? "").Contains("cadence/", StringComparison.Ordinal);
            return ours
                ? Other(task, Ownership.CorruptMetadata, null, "This task carries Cadence's marker but its definition can't be read: " + ex.Message)
                : Other(task, Ownership.Foreign, null, null);
        }

        // 2. Metadata.
        MetadataProblem problem = MetadataCodec.TryDecode(actual.RegistrationInfo.Documentation, out CadenceMetadata? meta);
        bool sourceIsCadence = string.Equals(actual.RegistrationInfo.Source, "Cadence", StringComparison.OrdinalIgnoreCase);
        switch (problem)
        {
            case MetadataProblem.Missing or MetadataProblem.NotCadence when !sourceIsCadence:
                return Other(task, Ownership.Foreign, actual, null);
            case MetadataProblem.Missing or MetadataProblem.NotCadence:
                return Other(task, Ownership.CorruptMetadata, actual, "This task says it is managed by Cadence but its job data is missing. It can only be deleted.");
            case MetadataProblem.Malformed:
                return Other(task, Ownership.CorruptMetadata, actual, "This task's Cadence job data is damaged. It can only be deleted.");
            case MetadataProblem.NewerSchema:
                return Other(task, Ownership.NewerVersion, actual, "This task was created by a newer version of Cadence. Update Cadence to edit it.");
        }
        CadenceMetadata m = meta!;

        // 3a. Written by a newer Cadence with the same schema: read-only, never downgraded.
        if (m.Gen > TaskComposer.GeneratorVersion || m.Wv > WrapperScript.Version)
            return new ClassifiedTask(task, Ownership.NewerVersion, SyncStatus.NotApplicable, actual, m, m.Job, ValueList<FieldDiff>.Empty,
                "This task was created by a newer version of Cadence. Update Cadence to edit it.");

        ComposeContext ctx = ctxFactory(m, task.Name);
        var diffs = new List<FieldDiff>();
        var drift = new List<string>();

        // 3b. The wrapper reads its metadata through `$tp`: a renamed or moved copy would read another task's data or fail.
        ExecActionModel? action = actual.ExecActions.Count == 1 ? actual.ExecActions[0] : null;
        bool isWrapper = WrapperScript.TryGetTaskPath(action, out string embeddedPath);
        if (isWrapper && !string.Equals(embeddedPath, task.Path, StringComparison.OrdinalIgnoreCase))
        {
            drift.Add("Renamed or moved outside Cadence (the task still points at " + embeddedPath + ")");
            diffs.Add(new FieldDiff("Task path", embeddedPath, task.Path));
        }

        // 3c. A different principal: compare SIDs through the caller's resolver, never names.
        if (!isConnectedUser(actual.Principal.UserId))
        {
            drift.Add("Runs as a different user");
            diffs.Add(new FieldDiff("User", ctx.UserId, actual.Principal.UserId ?? "(none)"));
        }

        // Cadence's version of the task today (null when the job can no longer be composed).
        TaskDefinitionModel? expected = null;
        string? composeError = null;
        try { expected = TaskComposer.Compose(m.Job, ctx); }
        catch (ComposeException ex) { composeError = ex.Message; }

        // 3d. Edited in Task Scheduler.
        string actualFp = Fingerprint.Of(actual);
        if (!string.Equals(actualFp, m.Fp, StringComparison.Ordinal))
        {
            var fieldDiffs = new List<FieldDiff>();
            if (expected is not null)
                fieldDiffs.AddRange(ProjectionDiff.Between(expected, actual));
            if (fieldDiffs.Count == 0)
                fieldDiffs.Add(isWrapper
                    ? new FieldDiff("Definition", "as Cadence wrote it", "changed")
                    : new FieldDiff("Program", "Cadence wrapper", action is null ? "several actions" : Short(action.Command)));
            drift.Add("Changed in Task Scheduler: " + string.Join("; ", fieldDiffs.Select(d => d.Describe())));
            diffs.AddRange(fieldDiffs);
        }

        if (drift.Count > 0)
            return new ClassifiedTask(task, Ownership.Managed, SyncStatus.Drifted, actual, m, m.Job, new ValueList<FieldDiff>(diffs), string.Join(". ", drift));

        // 3e. Outdated: Cadence would write something different today.
        var outdated = new List<string>();
        if (composeError is not null)
            outdated.Add("Cadence can't rebuild it: " + composeError);
        else if (!string.Equals(Fingerprint.Of(expected!), actualFp, StringComparison.Ordinal))
            outdated.Add("task definition");
        if (m.Gen < TaskComposer.GeneratorVersion) outdated.Add("generator version");
        if (m.Wv < WrapperScript.Version) outdated.Add("wrapper version");
        HolidayCalendar cal = m.Job.Schedule.SkipHolidays;
        if ((cal != HolidayCalendar.None) != (m.Skip is not null))
            outdated.Add("holiday list " + (m.Skip is null ? "missing" : "no longer needed"));
        else if (m.Skip is { } skip)
        {
            if (skip.Calendar != cal) outdated.Add("holiday calendar");
            else
            {
                int firstYear = skip.Dates.Count > 0 ? skip.Dates.Min(d => d.Date.Year) : m.Updated.Year;
                if (skip.Until.Year < firstYear || !skip.Dates.Equals(HolidayRules.Range(cal, firstYear, skip.Until.Year)))
                    outdated.Add("holiday list");
            }
            if (skip.Until < today.AddDays(HolidayRenewalDays)) outdated.Add("holiday list expires soon");
        }

        if (outdated.Count > 0)
            return new ClassifiedTask(task, Ownership.Managed, SyncStatus.Outdated, actual, m, m.Job, ValueList<FieldDiff>.Empty,
                "Cadence will update this task when you save (" + string.Join(", ", outdated) + ").");

        return new ClassifiedTask(task, Ownership.Managed, SyncStatus.InSync, actual, m, m.Job, ValueList<FieldDiff>.Empty, null);
    }

    static ClassifiedTask Other(ObservedTask task, Ownership ownership, TaskDefinitionModel? actual, string? problem) =>
        new(task, ownership, SyncStatus.NotApplicable, actual, null, null, ValueList<FieldDiff>.Empty, problem);

    static string Short(string s) => s.Length <= 80 ? s : s[..79] + "…";
}
