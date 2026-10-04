using System.Globalization;
using System.IO;

namespace Cadence.App.UI.Logic;

/// <summary>The columns of the home table, in display order.</summary>
public enum GridColumn { Name, Rate, Schedule, NextRun, LastResult, AvgTime, Success, State, Updated }

/// <summary>One column: its header, the narrowest it may be, the order in which columns are kept when the window narrows (1 = last to go) and the direction of the first click.</summary>
public sealed record ColumnSpec(GridColumn Column, string Id, string Header, double MinWidth, int Priority, bool RightAligned, bool FirstClickDescending);

/// <summary>A column that is shown and how wide it is.</summary>
public sealed record ColumnLayout(GridColumn Column, double Width);

/// <summary>The sort of the table: a column and a direction, persisted as "rate:desc".</summary>
public sealed record SortSpec(GridColumn Column, bool Descending)
{
    /// <summary>Most frequent first: active schedules on top.</summary>
    public static SortSpec Default { get; } = new(GridColumn.Rate, true);

    /// <summary>Garbage, an unknown column or nothing at all gives <see cref="Default"/>.</summary>
    public static SortSpec Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Default;
        string[] parts = text.Trim().Split(':');
        if (parts.Length != 2) return Default;
        ColumnSpec? spec = GridLogic.Columns.FirstOrDefault(c => string.Equals(c.Id, parts[0].Trim(), StringComparison.OrdinalIgnoreCase));
        if (spec is null) return Default;
        string dir = parts[1].Trim();
        if (string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase)) return new SortSpec(spec.Column, true);
        if (string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase)) return new SortSpec(spec.Column, false);
        return Default;
    }

    public string Format() => GridLogic.Spec(Column).Id + ":" + (Descending ? "desc" : "asc");
}

/// <summary>An "other task" row as the sort sees it (its rate is counted later, so it may be missing).</summary>
public sealed record ForeignSortItem(ForeignText Text, RateInfo? Rate);

/// <summary>
/// The pure rules of the home table ([P25]): which columns fit, how rows sort and filter, the counts of the status bar and the window title.
/// Everything here is unit tested; the views only bind.
/// </summary>
public static class GridLogic
{
    /// <summary>All nine columns in display order (minimum widths are what the longest usual cell needs next to its 8 px margins; the design's were a little tighter and cut "Tomorrow 2:00"). Sorting direction "asc" means: A-Z, soonest, problems first, worst first, smallest, oldest; "desc" is the reverse.</summary>
    public static readonly IReadOnlyList<ColumnSpec> Columns =
    [
        new(GridColumn.Name, "name", "Name", 160, 1, false, false),
        new(GridColumn.Rate, "rate", "Rate", 104, 4, false, true),
        new(GridColumn.Schedule, "schedule", "Schedule", 130, 9, false, false),
        new(GridColumn.NextRun, "next", "Next run", 138, 3, false, false),
        new(GridColumn.LastResult, "result", "Last result", 205, 2, false, false),
        new(GridColumn.AvgTime, "avg", "Avg time", 90, 8, true, true),
        new(GridColumn.Success, "success", "Success", 60, 7, true, false),
        new(GridColumn.State, "state", "State", 167, 5, false, false),
        new(GridColumn.Updated, "updated", "Updated", 176, 6, false, true),
    ];

    public static ColumnSpec Spec(GridColumn column) => Columns.First(c => c.Column == column);

    // ---- which columns fit ----

    /// <summary>
    /// The columns to show in <paramref name="availableWidth"/>: taken in priority order while their minimum widths fit (one that does not fit is skipped and the
    /// next is tried), minus the ones the user hid. Name always stays. The spare width goes 60/40 to Name and Schedule, or all to Name when Schedule is out.
    /// Returned in display order.
    /// </summary>
    public static IReadOnlyList<ColumnLayout> Fit(double availableWidth, IReadOnlySet<GridColumn>? userHidden = null)
    {
        var shown = new HashSet<GridColumn>();
        double used = 0;
        foreach (ColumnSpec spec in Columns.OrderBy(c => c.Priority))
        {
            if (spec.Column != GridColumn.Name && userHidden?.Contains(spec.Column) == true) continue;
            if (spec.Column != GridColumn.Name && used + spec.MinWidth > availableWidth) continue;
            shown.Add(spec.Column);
            used += spec.MinWidth;
        }
        double spare = Math.Max(0, availableWidth - used);
        bool schedule = shown.Contains(GridColumn.Schedule);
        var result = new List<ColumnLayout>();
        foreach (ColumnSpec spec in Columns)
        {
            if (!shown.Contains(spec.Column)) continue;
            double width = spec.MinWidth;
            if (spec.Column == GridColumn.Name) width += schedule ? Math.Floor(spare * 0.6) : spare;
            else if (spec.Column == GridColumn.Schedule) width += Math.Floor(spare * 0.4);
            result.Add(new ColumnLayout(spec.Column, width));
        }
        return result;
    }

    /// <summary>"name,rate,..." of the hidden columns as persisted; unknown words and Name are ignored.</summary>
    public static IReadOnlySet<GridColumn> ParseHidden(string? text)
    {
        var set = new HashSet<GridColumn>();
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (string word in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            ColumnSpec? spec = Columns.FirstOrDefault(c => string.Equals(c.Id, word, StringComparison.OrdinalIgnoreCase));
            if (spec is not null && spec.Column != GridColumn.Name) set.Add(spec.Column);
        }
        return set;
    }

    public static string FormatHidden(IEnumerable<GridColumn> hidden) =>
        string.Join(",", Columns.Where(c => c.Column != GridColumn.Name && hidden.Contains(c.Column)).Select(c => c.Id));

    // ---- sorting ----

    /// <summary>A header click: the same column flips its direction, another column starts in its own first-click direction.</summary>
    public static SortSpec Toggle(SortSpec current, GridColumn clicked) =>
        current.Column == clicked ? current with { Descending = !current.Descending } : new SortSpec(clicked, Spec(clicked).FirstClickDescending);

    /// <summary>
    /// The comparer of a column. Rate groups first and the groups do not flip with the direction (active schedules stay on top both ways); every other
    /// column puts empty values last in both directions. Ties break by name.
    /// </summary>
    public static IComparer<RowText> Compare(GridColumn column, bool descending) => new RowComparer(column, descending);

    sealed class RowComparer(GridColumn column, bool desc) : IComparer<RowText>
    {
        public int Compare(RowText? x, RowText? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            RowSortKeys a = x.Keys, b = y.Keys;
            int c;
            switch (column)
            {
                case GridColumn.Rate:
                    c = a.RateGroup.CompareTo(b.RateGroup);
                    if (c != 0) return c;
                    c = a.RunsPerWeek.CompareTo(b.RunsPerWeek);
                    if (desc) c = -c;
                    if (c != 0) return c;
                    c = CompareOptional(x.NextAt, y.NextAt, false);
                    return c != 0 ? c : ByName(x, y);
                case GridColumn.Name:
                    return desc ? ByName(y, x) : ByName(x, y);
                case GridColumn.Schedule:
                    return Directed(x, y, x.Schedule.Length == 0, y.Schedule.Length == 0, string.Compare(x.Schedule, y.Schedule, StringComparison.OrdinalIgnoreCase));
                case GridColumn.NextRun:
                    return Directed(x, y, x.NextAt is null, y.NextAt is null, CompareOptional(x.NextAt, y.NextAt, false));
                case GridColumn.LastResult:
                {
                    bool xe = a.ResultRank == int.MaxValue, ye = b.ResultRank == int.MaxValue;
                    if (xe || ye) return Directed(x, y, xe, ye, 0);
                    c = a.ResultRank.CompareTo(b.ResultRank);
                    if (desc) c = -c;
                    if (c != 0) return c;
                    c = CompareOptional(b.ResultAt, a.ResultAt, false);   // then the newest
                    return c != 0 ? c : ByName(x, y);
                }
                case GridColumn.AvgTime:
                    return Directed(x, y, a.AvgSeconds is null, b.AvgSeconds is null, Nullable.Compare(a.AvgSeconds, b.AvgSeconds));
                case GridColumn.Success:
                    return Directed(x, y, a.SuccessRate is null, b.SuccessRate is null, Nullable.Compare(a.SuccessRate, b.SuccessRate));
                case GridColumn.State:
                    return Directed(x, y, false, false, a.State.CompareTo(b.State));
                case GridColumn.Updated:
                    return Directed(x, y, x.UpdatedAt is null, y.UpdatedAt is null, CompareOptional(x.UpdatedAt, y.UpdatedAt, false));
                default:
                    return ByName(x, y);
            }
        }

        /// <summary>Empty values last whatever the direction; otherwise the direction applies; ties by name.</summary>
        int Directed(RowText x, RowText y, bool xEmpty, bool yEmpty, int compared)
        {
            if (xEmpty && yEmpty) return ByName(x, y);
            if (xEmpty) return 1;
            if (yEmpty) return -1;
            int c = desc ? -compared : compared;
            return c != 0 ? c : ByName(x, y);
        }
    }

    static int ByName(RowText x, RowText y)
    {
        int c = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.Name, y.Name);
        return c != 0 ? c : string.CompareOrdinal(x.ProgramPath, y.ProgramPath);   // last tie-breaker: a total order whatever the data
    }

    static int CompareOptional(DateTimeOffset? x, DateTimeOffset? y, bool emptyFirst)
    {
        if (x is null && y is null) return 0;
        if (x is null) return emptyFirst ? -1 : 1;
        if (y is null) return emptyFirst ? 1 : -1;
        return x.Value.CompareTo(y.Value);
    }

    /// <summary>The comparer of the "other tasks" table: Rate, Next run, Last result and State apply; Name sorts by name; the other columns fall back to Name.</summary>
    public static IComparer<ForeignSortItem> CompareForeign(GridColumn column, bool descending) => new ForeignComparer(column, descending);

    sealed class ForeignComparer(GridColumn column, bool desc) : IComparer<ForeignSortItem>
    {
        public int Compare(ForeignSortItem? x, ForeignSortItem? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            int c;
            switch (column)
            {
                case GridColumn.Rate:
                {
                    int ga = Group(x), gb = Group(y);
                    if (ga == 3 || gb == 3) return ga == gb ? ByName(x, y) : ga == 3 ? 1 : -1;   // not counted yet or unreadable: last in both directions
                    c = ga.CompareTo(gb);
                    if (c != 0) return c;
                    c = (x.Rate?.RunsPerWeek ?? 0).CompareTo(y.Rate?.RunsPerWeek ?? 0);   // a disabled task has no rate until the thread pool has counted it
                    if (desc) c = -c;
                    if (c != 0) return c;
                    c = CompareOptional(x.Text.NextAt, y.Text.NextAt, false);
                    return c != 0 ? c : ByName(x, y);
                }
                case GridColumn.NextRun:
                    return Directed(x, y, x.Text.NextAt is null, y.Text.NextAt is null, CompareOptional(x.Text.NextAt, y.Text.NextAt, false));
                case GridColumn.LastResult:
                {
                    bool xe = x.Text.ResultRank == 5, ye = y.Text.ResultRank == 5;
                    if (xe || ye) return Directed(x, y, xe, ye, 0);
                    c = x.Text.ResultRank.CompareTo(y.Text.ResultRank);
                    if (desc) c = -c;
                    if (c != 0) return c;
                    c = CompareOptional(y.Text.ResultAt, x.Text.ResultAt, false);
                    return c != 0 ? c : ByName(x, y);
                }
                case GridColumn.State:
                    return Directed(x, y, false, false, x.Text.IsEnabled.CompareTo(y.Text.IsEnabled) * -1);
                default:
                    c = ByName(x, y);
                    return column == GridColumn.Name && desc ? -c : c;
            }
        }

        // 0 timed, 1 sign-in or event, 2 never, 3 unknown.
        static int Group(ForeignSortItem i) => !i.Text.IsEnabled ? 2 : i.Rate is null || ReferenceEquals(i.Rate, RateInfo.Unknown) ? 3 : i.Rate.Kind switch
        {
            RateKind.Timed or RateKind.Rare => 0,
            RateKind.AtSignIn or RateKind.EventOnly => 1,
            _ => 2,
        };

        int Directed(ForeignSortItem x, ForeignSortItem y, bool xEmpty, bool yEmpty, int compared)
        {
            if (xEmpty && yEmpty) return ByName(x, y);
            if (xEmpty) return 1;
            if (yEmpty) return -1;
            int c = desc ? -compared : compared;
            return c != 0 ? c : ByName(x, y);
        }
    }

    static int ByName(ForeignSortItem x, ForeignSortItem y)
    {
        int c = string.Compare(x.Text.Name, y.Text.Name, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.Text.Name, y.Text.Name);
        if (c != 0) return c;
        c = string.Compare(x.Text.Folder, y.Text.Folder, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(x.Text.Folder, y.Text.Folder);   // the task path decides last
    }

    // ---- filter ----

    /// <summary>Every word of <paramref name="filter"/> must occur (case-insensitive) in one of: name, schedule sentence, rate, state, last result, the program's file name, path or arguments.</summary>
    public static bool Matches(RowText row, string? filter)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string fileName = row.ProgramPath.Length > 0 ? Path.GetFileName(row.ProgramPath) : "";
        string[] fields = [row.Name, row.Schedule, row.RateText, row.StateText, row.ResultTitle, fileName, row.ProgramPath, row.ProgramArgs];
        foreach (string word in filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (!fields.Any(f => f.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// <summary>The same for an "other task" (name, folder, rate, state, last result).</summary>
    public static bool MatchesForeign(ForeignText row, string rate, string? filter)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string[] fields = [row.Name, row.Folder, rate, row.IsEnabled ? "" : "Disabled", row.Result];
        foreach (string word in filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (!fields.Any(f => f.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    // ---- counts, status bar, window title ----

    /// <summary>
    /// Total, problems and disabled. A problem is a row whose last result failed, couldn't start, didn't report or timed out, or whose state is damaged, changed
    /// outside Cadence, copy or newer version. Disabled is not a problem.
    /// </summary>
    public static (int Total, int Problems, int Disabled) Counts(IEnumerable<RowText> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int total = 0, problems = 0, disabled = 0;
        foreach (RowText row in rows)
        {
            total++;
            if (row.HasProblem || row.IsProblem) problems++;
            if (!row.IsEnabled && !row.IsProblem) disabled++;
        }
        return (total, problems, disabled);
    }

    static string Plural(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    /// <summary>The left of the status bar: "8 schedules · 4 problems · 1 disabled" (parts that are zero are left out), "· showing 3" while a filter is on.</summary>
    public static string StatusText(bool loaded, int total, int problems, int disabled, int? showing = null)
    {
        if (!loaded) return "Reading Task Scheduler…";
        if (total == 0) return "No schedules";
        var parts = new List<string> { Plural(total, "schedule", "schedules") };
        if (problems > 0) parts.Add(Plural(problems, "problem", "problems"));
        if (disabled > 0) parts.Add(disabled.ToString(CultureInfo.InvariantCulture) + " disabled");
        if (showing is { } n) parts.Add("showing " + n.ToString(CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    /// <summary>The window title on the list: "Cadence - 8 schedules, 4 problems"; plain "Cadence" while loading or when empty.</summary>
    public static string ListTitle(bool loaded, int total, int problems)
    {
        const string name = "Cadence";
        if (!loaded || total == 0) return name;
        string text = name + " - " + Plural(total, "schedule", "schedules");
        return problems > 0 ? text + ", " + Plural(problems, "problem", "problems") : text;
    }

    /// <summary>The window title in the editor: "Cadence - New schedule" or "Cadence - Stock ingest (Dad)", plus " (unsaved)" when there are unsaved changes.</summary>
    public static string EditorTitle(bool isNew, string? name, bool dirty)
    {
        string title = "Cadence - " + (isNew ? "New schedule" : string.IsNullOrWhiteSpace(name) ? "Schedule" : name.Trim());
        return dirty ? title + " (unsaved)" : title;
    }
}
