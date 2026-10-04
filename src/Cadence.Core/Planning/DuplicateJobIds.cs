using Cadence.Core.Execution;
using Cadence.Core.Model;

namespace Cadence.Core.Planning;

/// <summary>
/// PLAN risk R21: two managed tasks that carry the same job id (a task exported and imported in Task Scheduler, then re-applied or saved)
/// would share one run history and fight over the same <c>runs\&lt;id&gt;</c> folder. This pass looks at a whole classified folder and marks every
/// task except one as a copy. Pure: no Task Scheduler, no clock.
/// </summary>
public static class DuplicateJobIds
{
    /// <summary>The sentence shown for a copy; <c>{0}</c> is the original's task name.</summary>
    public const string ProblemFormat = "This is a copy of '{0}' made outside Cadence; delete it, or stop managing it";

    /// <summary>
    /// Returns the same tasks, in the same order, with every copy replaced by a Drifted version whose <see cref="ClassifiedTask.DuplicateOf"/>
    /// names the original. The original of a group is the task whose wrapper's <c>$tp</c> equals its own path (a copy still points at the
    /// original). If several qualify (a copy that was re-applied points at itself), the one Cadence wrote first wins (the oldest
    /// <c>updated</c>), then the first by name. Only Managed tasks with a job count; Foreign, Corrupt and NewerVersion tasks are never touched.
    /// </summary>
    public static IReadOnlyList<ClassifiedTask> Resolve(IReadOnlyList<ClassifiedTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        Dictionary<string, List<ClassifiedTask>> groups = new(StringComparer.Ordinal);
        foreach (ClassifiedTask t in tasks)
        {
            if (t.Ownership != Ownership.Managed || t.Job is null) continue;
            if (!groups.TryGetValue(t.Job.Id, out List<ClassifiedTask>? list)) groups[t.Job.Id] = list = [];
            list.Add(t);
        }
        if (groups.Values.All(g => g.Count < 2)) return tasks;

        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // task path -> original's task name
        foreach (List<ClassifiedTask> group in groups.Values)
        {
            if (group.Count < 2) continue;
            List<ClassifiedTask> pointsAtItself = group.Where(PointsAtItself).ToList();
            ClassifiedTask original = (pointsAtItself.Count > 0 ? pointsAtItself : group)
                .OrderBy(t => t.Metadata?.Updated ?? DateTimeOffset.MaxValue)
                .ThenBy(t => t.Observed.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Observed.Name, StringComparer.Ordinal)
                .First();
            foreach (ClassifiedTask t in group)
                if (!ReferenceEquals(t, original)) copies[t.Observed.Path] = original.Observed.Name;
        }

        var result = new List<ClassifiedTask>(tasks.Count);
        foreach (ClassifiedTask t in tasks)
        {
            if (t.Ownership == Ownership.Managed && t.Job is not null && copies.TryGetValue(t.Observed.Path, out string? originalName))
                result.Add(t with
                {
                    Sync = SyncStatus.Drifted,
                    DriftDiffs = ValueList<FieldDiff>.Empty,
                    Problem = string.Format(System.Globalization.CultureInfo.InvariantCulture, ProblemFormat, originalName),
                    DuplicateOf = originalName,
                });
            else result.Add(t);
        }
        return result;
    }

    static bool PointsAtItself(ClassifiedTask t) =>
        t.Actual is { ExecActions.Count: 1 } actual
        && WrapperScript.TryGetTaskPath(actual.ExecActions[0], out string embedded)
        && string.Equals(embedded, t.Observed.Path, StringComparison.OrdinalIgnoreCase);
}
