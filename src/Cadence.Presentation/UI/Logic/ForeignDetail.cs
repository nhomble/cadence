using Cadence.Core.Planning;

namespace Cadence.App.UI.Logic;

/// <summary>One "Label: value" line of the read-only detail of a task Cadence does not manage.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>What the read-only dialog of a foreign task shows (PLAN 7.2 A: Cadence never changes these; the only action is "Open in Task Scheduler").</summary>
public sealed record ForeignDetailText(string Name, string Path, IReadOnlyList<DetailRow> Rows, string Note, string Location);

/// <summary>Pure text for the foreign-task detail and for "Open in Task Scheduler" (Task Scheduler has no deep link, so the way there is spelled out).</summary>
public static class ForeignDetail
{
    public const string Note = "Cadence doesn't manage this task, so it only shows it. To change it, open it in Task Scheduler.";

    public static ForeignDetailText From(ClassifiedTask task, DateTimeOffset now, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(task);
        ForeignText row = ListFormat.DescribeForeign(task, now, tz);
        var rows = new List<DetailRow>
        {
            new("Where", row.Folder),
            new("Status", row.IsEnabled ? "Enabled" : "Disabled"),
            new("Next run", row.Next.StartsWith("Next: ", StringComparison.Ordinal) ? row.Next[6..] : row.Next),
            new("Last run", row.Result),
        };
        if (task.Actual is { } actual)
        {
            foreach (Cadence.Core.TaskXml.ExecActionModel a in actual.ExecActions)
                rows.Add(new DetailRow("Runs", Short((a.Command + (string.IsNullOrWhiteSpace(a.Arguments) ? "" : " " + a.Arguments)).Trim(), 140)));
            if (actual.ExecActions.Count == 0 && actual.OtherActionsXml.Count > 0) rows.Add(new DetailRow("Runs", "(an action Cadence doesn't show)"));
            if (!string.IsNullOrWhiteSpace(actual.RegistrationInfo.Author)) rows.Add(new DetailRow("Made by", Short(actual.RegistrationInfo.Author!.Trim(), 80)));
            if (!string.IsNullOrWhiteSpace(actual.RegistrationInfo.Description)) rows.Add(new DetailRow("Description", Short(OneLine(actual.RegistrationInfo.Description!), 220)));
        }
        else rows.Add(new DetailRow("Runs", "(Cadence can't read this task's definition)"));
        return new ForeignDetailText(row.Name, task.Observed.Path, rows, Note, Location(task.Observed.Path));
    }

    /// <summary>"Task Scheduler Library › Microsoft › Windows › Defrag › ScheduledDefrag": where to click once Task Scheduler is open.</summary>
    public static string Location(string taskPath)
    {
        string[] parts = (taskPath ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "Task Scheduler Library" : "Task Scheduler Library › " + string.Join(" › ", parts);
    }

    /// <summary>The toast after "Open in Task Scheduler": the path is on the clipboard and this says where to look.</summary>
    public static string OpenedToast(string taskPath) => "Opening Task Scheduler. Look under " + Location(taskPath) + " (the path is copied).";

    static string OneLine(string s) => s.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
