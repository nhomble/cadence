using Cadence.App.UI.Logic;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>[P25] The pure rules of the home table: which columns fit, the comparers, the filter, the counts, the status text and the window title.</summary>
public static class GridLogicTests
{
    // ---- columns that fit ----

    static string Shown(double width, params GridColumn[] hidden) =>
        string.Join(",", GridLogic.Fit(width, new HashSet<GridColumn>(hidden)).Select(c => c.Column));

    public static void ColumnsDropOutByPriorityAsTheWindowNarrows()
    {
        // The table's width is the window's minus the 16 px gutters and the scroll bar.
        T.Eq("1280 px: every column", "Name,Rate,Schedule,NextRun,LastResult,AvgTime,Success,State,Updated", Shown(1280 - 32 - 17));
        T.Eq("1100 px: Schedule goes first, then Avg time", "Name,Rate,NextRun,LastResult,Success,State,Updated", Shown(1100 - 32 - 17));
        T.Eq("900 px: six columns (Updated needs more room than Success)", "Name,Rate,NextRun,LastResult,Success,State", Shown(900 - 32 - 17));
        T.Eq("a very narrow table keeps the name", "Name", Shown(100));
        T.Eq("the user hides Updated", "Name,Rate,Schedule,NextRun,LastResult,AvgTime,Success,State", Shown(1280 - 32 - 17, GridColumn.Updated));
        T.Eq("Name can not be hidden", "Name,Rate,Schedule,NextRun,LastResult,AvgTime,Success,State,Updated", Shown(1280 - 32 - 17, GridColumn.Name));
        T.Eq("1150 px is too narrow for all nine", "Name,Rate,NextRun,LastResult,AvgTime,Success,State,Updated", Shown(1150));
        T.Eq("hiding Updated frees room for Schedule", "Name,Rate,Schedule,NextRun,LastResult,AvgTime,Success,State", Shown(1150, GridColumn.Updated));
    }

    public static void TheSpareWidthGoesToNameAndSchedule()
    {
        IReadOnlyList<ColumnLayout> wide = GridLogic.Fit(1500);
        double total = wide.Sum(c => c.Width);
        T.True("the widths fill the table", total <= 1500 && total > 1498, total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        double spare = 1500 - GridLogic.Columns.Sum(c => c.MinWidth);
        T.True("Name gets 60%", Math.Abs(wide.Single(c => c.Column == GridColumn.Name).Width - (GridLogic.Spec(GridColumn.Name).MinWidth + Math.Floor(spare * 0.6))) < 0.01);
        T.True("Schedule gets 40%", Math.Abs(wide.Single(c => c.Column == GridColumn.Schedule).Width - (GridLogic.Spec(GridColumn.Schedule).MinWidth + Math.Floor(spare * 0.4))) < 0.01);
        IReadOnlyList<ColumnLayout> noSchedule = GridLogic.Fit(900 - 49);
        T.True("without Schedule all the spare goes to Name", noSchedule.Sum(c => c.Width) > 900 - 49 - 1 && noSchedule.Single(c => c.Column == GridColumn.Name).Width > 160);
        foreach (ColumnLayout c in GridLogic.Fit(1231))
            T.True(c.Column + " is not narrower than its minimum", c.Width >= GridLogic.Spec(c.Column).MinWidth);
    }

    public static void HiddenColumnsAreStoredAsAList()
    {
        T.Eq("round trip", "rate,updated", GridLogic.FormatHidden(GridLogic.ParseHidden("updated, RATE ,bogus,name")));
        T.Eq("nothing", 0, GridLogic.ParseHidden(null).Count);
        T.Eq("garbage", 0, GridLogic.ParseHidden(";;;").Count);
    }

    // ---- the sort setting ----

    public static void TheSortSettingIsParsedAndGarbageGivesTheDefault()
    {
        T.Eq("default", "rate:desc", SortSpec.Default.Format());
        T.Eq("parse", new SortSpec(GridColumn.Name, false), SortSpec.Parse("name:asc"));
        T.Eq("parse next", new SortSpec(GridColumn.NextRun, true), SortSpec.Parse(" NEXT : Desc "));
        foreach (string garbage in new[] { "", "   ", "nonsense", "name", "name:sideways", "bogus:asc", "name:asc:desc", ":" })
            T.Eq("garbage '" + garbage + "'", SortSpec.Default, SortSpec.Parse(garbage));
        T.Eq("null", SortSpec.Default, SortSpec.Parse(null));
        foreach (ColumnSpec c in GridLogic.Columns)
            foreach (bool desc in new[] { false, true })
                T.Eq("round trip " + c.Id + desc, new SortSpec(c.Column, desc), SortSpec.Parse(new SortSpec(c.Column, desc).Format()));
    }

    public static void AHeaderClickFlipsTheSameColumnAndStartsAnotherInItsOwnDirection()
    {
        T.Eq("Rate again flips to ascending", new SortSpec(GridColumn.Rate, false), GridLogic.Toggle(SortSpec.Default, GridColumn.Rate));
        T.Eq("Name first click is A-Z", new SortSpec(GridColumn.Name, false), GridLogic.Toggle(SortSpec.Default, GridColumn.Name));
        T.Eq("Name again is Z-A", new SortSpec(GridColumn.Name, true), GridLogic.Toggle(new SortSpec(GridColumn.Name, false), GridColumn.Name));
        T.Eq("Avg time first click is slowest first", new SortSpec(GridColumn.AvgTime, true), GridLogic.Toggle(SortSpec.Default, GridColumn.AvgTime));
        T.Eq("Updated first click is newest first", new SortSpec(GridColumn.Updated, true), GridLogic.Toggle(SortSpec.Default, GridColumn.Updated));
        T.Eq("Success first click is worst first", new SortSpec(GridColumn.Success, false), GridLogic.Toggle(SortSpec.Default, GridColumn.Success));
    }

    // ---- the comparers ----

    static RowText Row(string name, RowSortKeys? keys = null, DateTimeOffset? nextAt = null, DateTimeOffset? updatedAt = null, string schedule = "s", string program = "", bool problem = false,
        bool enabled = true, string state = "", string rate = "", bool hasProblem = false) =>
        new(name, schedule, "", "", "", BadgeKind.None, BadgeKind.None, null, null, null, problem, enabled, SortKeys: keys, NextAt: nextAt, UpdatedAt: updatedAt, ProgramPath: program,
            StateText: state, HasProblem: hasProblem, Rate: rate.Length == 0 ? null : new RateInfo(RateKind.Timed, 1, rate, "", false));

    static RowSortKeys Keys(int group = 0, double perWeek = 0, int rank = int.MaxValue, DateTimeOffset? at = null, double? avg = null, double? success = null, RowState state = RowState.Normal) =>
        new(group, perWeek, rank, at, avg, success, state);

    static string Order(IEnumerable<RowText> rows, GridColumn column, bool desc) =>
        string.Join(",", rows.OrderBy(r => r, GridLogic.Compare(column, desc)).Select(r => r.Name));

    static DateTimeOffset At(int day, int hour = 9) => new(2026, 11, day, hour, 0, 0, TimeSpan.FromHours(-5));

    public static void RateGroupsComeFirstInBothDirections()
    {
        RowText[] rows =
        [
            Row("busy", Keys(0, 100)), Row("slow", Keys(0, 5)), Row("signin", Keys(1)), Row("never", Keys(2)), Row("disabled", Keys(3, 1000)), Row("damaged", Keys(4)),
        ];
        T.Eq("desc: most frequent first, groups fixed", "busy,slow,signin,never,disabled,damaged", Order(rows.Reverse(), GridColumn.Rate, true));
        T.Eq("asc: least frequent first inside the group, groups still fixed", "slow,busy,signin,never,disabled,damaged", Order(rows.Reverse(), GridColumn.Rate, false));
        RowText[] ties = [Row("b", Keys(0, 7), At(30)), Row("a", Keys(0, 7), At(30)), Row("c", Keys(0, 7), At(28))];
        T.Eq("equal rate: next run soonest, then name", "c,a,b", Order(ties, GridColumn.Rate, true));
    }

    public static void NameSortIsCaseInsensitiveThenOrdinal()
    {
        RowText[] rows = [Row("sync notes"), Row("Zeta"), Row("alpha"), Row("Beta"), Row("Alpha")];
        T.Eq("A-Z", "Alpha,alpha,Beta,sync notes,Zeta", Order(rows, GridColumn.Name, false));
        T.Eq("Z-A", "Zeta,sync notes,Beta,alpha,Alpha", Order(rows, GridColumn.Name, true));
    }

    public static void EmptyValuesSortLastInBothDirections()
    {
        RowText[] avg = [Row("none", Keys(avg: null)), Row("fast", Keys(avg: 1.0)), Row("slow", Keys(avg: 30.0))];
        T.Eq("avg asc", "fast,slow,none", Order(avg, GridColumn.AvgTime, false));
        T.Eq("avg desc (slowest first)", "slow,fast,none", Order(avg, GridColumn.AvgTime, true));
        RowText[] success = [Row("none", Keys(success: null)), Row("good", Keys(success: 1.0)), Row("bad", Keys(success: 0.2))];
        T.Eq("success asc (worst first)", "bad,good,none", Order(success, GridColumn.Success, false));
        T.Eq("success desc", "good,bad,none", Order(success, GridColumn.Success, true));
        RowText[] next = [Row("never", nextAt: null), Row("later", nextAt: At(30)), Row("soon", nextAt: At(27, 18))];
        T.Eq("next asc (soonest first)", "soon,later,never", Order(next, GridColumn.NextRun, false));
        T.Eq("next desc", "later,soon,never", Order(next, GridColumn.NextRun, true));
        RowText[] updated = [Row("none", updatedAt: null), Row("old", updatedAt: At(1)), Row("new", updatedAt: At(25))];
        T.Eq("updated desc (newest first)", "new,old,none", Order(updated, GridColumn.Updated, true));
        T.Eq("updated asc", "old,new,none", Order(updated, GridColumn.Updated, false));
        RowText[] schedule = [Row("empty", schedule: ""), Row("b", schedule: "Every day"), Row("a", schedule: "Every Monday")];
        T.Eq("schedule A-Z, empty last", "b,a,empty", Order(schedule, GridColumn.Schedule, false));
        T.Eq("schedule Z-A, empty last", "a,b,empty", Order(schedule, GridColumn.Schedule, true));
    }

    public static void LastResultSortsProblemsFirstThenNewest()
    {
        RowText[] rows =
        [
            Row("ok", Keys(rank: 4, at: At(27))), Row("failed-old", Keys(rank: 0, at: At(20))), Row("failed-new", Keys(rank: 0, at: At(26))), Row("timeout", Keys(rank: 1, at: At(26))),
            Row("running", Keys(rank: 2, at: At(27))), Row("skipped", Keys(rank: 3, at: At(26))), Row("never", Keys(rank: 5)), Row("damaged", Keys(rank: int.MaxValue)),
        ];
        T.Eq("asc: problems first, then newest", "failed-new,failed-old,timeout,running,skipped,ok,never,damaged", Order(rows, GridColumn.LastResult, false));
        T.Eq("desc: reversed ranks, empty still last", "never,ok,skipped,running,timeout,failed-new,failed-old,damaged", Order(rows, GridColumn.LastResult, true));
    }

    public static void StateSortsProblemsFirst()
    {
        RowText[] rows =
        [
            Row("normal", Keys(state: RowState.Normal)), Row("disabled", Keys(state: RowState.Disabled)), Row("running", Keys(state: RowState.Running)),
            Row("newer", Keys(state: RowState.Newer)), Row("changed", Keys(state: RowState.Changed)), Row("damaged", Keys(state: RowState.Damaged)),
        ];
        T.Eq("asc", "damaged,changed,newer,running,disabled,normal", Order(rows, GridColumn.State, false));
    }

    // ---- every comparer is a total order that never throws ----

    static ForeignSortItem Foreign(string name, bool enabled = true, RateInfo? rate = null, DateTimeOffset? nextAt = null, int rank = 5, DateTimeOffset? resultAt = null, string folder = @"\Microsoft") =>
        new(new ForeignText(name, folder, "", "", enabled, NextAt: nextAt, ResultRank: rank, ResultAt: resultAt), rate);

    public static void TheOtherTasksTableSortsWhileTheRatesAreStillBeingCounted()
    {
        // [Found live] 163 other tasks are shown with no rate until the thread pool has counted them; a disabled one (group 2) has Rate == null and the Rate comparer dereferenced it,
        // which Array.Sort reports as "Failed to compare two elements in the array" (the real cause is the NullReferenceException inside).
        ForeignSortItem[] items = [Foreign("a", enabled: false), Foreign("b", enabled: false), Foreign("c"), Foreign("d", rate: new RateInfo(RateKind.Timed, 5, "x", "", false)), Foreign("e", enabled: false, rate: RateInfo.Unknown)];
        foreach (GridColumn column in Enum.GetValues<GridColumn>())
            foreach (bool desc in new[] { false, true })
                T.Eq("no throw " + column + desc, 5, items.OrderBy(i => i, GridLogic.CompareForeign(column, desc)).ToList().Count);
        T.Eq("the sort itself (what the list view model does)", "d,a,b,e,c", string.Join(",", items.OrderBy(i => i, GridLogic.CompareForeign(GridColumn.Rate, true)).Select(i => i.Text.Name)));
    }

    static void Axioms<T2>(string what, IReadOnlyList<T2> items, IComparer<T2> cmp)
    {
        static int Sign(int v) => Math.Sign(v);
        for (int i = 0; i < items.Count; i++)
        {
            if (cmp.Compare(items[i], items[i]) != 0) T.True(what + ": reflexive", false);
            for (int j = 0; j < items.Count; j++)
            {
                int ij = Sign(cmp.Compare(items[i], items[j])), ji = Sign(cmp.Compare(items[j], items[i]));
                if (ij != -ji) T.True(what + ": antisymmetric at " + i + "," + j, false);
                for (int k = 0; k < items.Count; k++)
                {
                    int jk = Sign(cmp.Compare(items[j], items[k])), ik = Sign(cmp.Compare(items[i], items[k]));
                    if (ij <= 0 && jk <= 0 && ik > 0) T.True(what + ": transitive (<=) at " + i + "," + j + "," + k, false);
                    if (ij < 0 && jk < 0 && ik >= 0) T.True(what + ": transitive (<) at " + i + "," + j + "," + k, false);
                    if (ij == 0 && jk == 0 && ik != 0) T.True(what + ": equivalence transitive at " + i + "," + j + "," + k, false);
                }
            }
        }
        // Sorting must work whatever the input order.
        var rnd = new Random(7);
        for (int round = 0; round < 5; round++)
            T.Eq(what + ": sort does not throw", items.Count, items.OrderBy(_ => rnd.Next()).OrderBy(x => x, cmp).ToList().Count);
    }

    public static void EveryComparerIsAStrictWeakOrderOverAwkwardData()
    {
        var rnd = new Random(20261003);
        string[] names = ["a", "A", "b", "Sync notes", "sync notes", "", "é", "z"];
        DateTimeOffset?[] times = [null, At(1), At(1), At(20), At(30)];
        var rows = new List<RowText>();
        for (int i = 0; i < 26; i++)
        {
            RowSortKeys keys = i % 7 == 0 ? RowSortKeys.None : new RowSortKeys(rnd.Next(0, 5), new[] { 0.0, 1.0, 1.0, 99.5, double.NaN, double.PositiveInfinity, -0.0 }[rnd.Next(7)],
                new[] { 0, 1, 2, 3, 4, 5, int.MaxValue }[rnd.Next(7)], times[rnd.Next(times.Length)], new double?[] { null, 0, 0.04, double.NaN, 30 }[rnd.Next(5)],
                new double?[] { null, 0, 0.5, 1, double.NaN }[rnd.Next(5)], (RowState)rnd.Next(6));
            string name = names[rnd.Next(names.Length)] + (rnd.Next(3) == 0 ? "" : i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            RowText row = Row(name, keys, nextAt: times[rnd.Next(times.Length)], updatedAt: times[rnd.Next(times.Length)], schedule: new[] { "", "Every day", "every day", "x" }[rnd.Next(4)], enabled: rnd.Next(4) != 0);
            rows.Add(i % 11 == 0 ? row with { SortKeys = null } : row);
        }
        foreach (GridColumn column in Enum.GetValues<GridColumn>())
            foreach (bool desc in new[] { false, true })
                Axioms("rows " + column + (desc ? " desc" : " asc"), rows, GridLogic.Compare(column, desc));

        var foreign = new List<ForeignSortItem>();
        RateInfo?[] rates = [null, RateInfo.Unknown, new(RateKind.Timed, 5, "a", "", false), new(RateKind.Timed, 5, "b", "", false), new(RateKind.Rare, 0.1, "c", "", false), new(RateKind.AtSignIn, 0, "d", "", false),
            new(RateKind.EventOnly, 0, "e", "", false), new(RateKind.Never, 0, "f", "", false), new(RateKind.Timed, double.NaN, "g", "", false)];
        for (int i = 0; i < 26; i++)
            foreign.Add(Foreign(names[rnd.Next(names.Length)] + (rnd.Next(3) == 0 ? "" : i.ToString(System.Globalization.CultureInfo.InvariantCulture)), rnd.Next(3) != 0, rates[rnd.Next(rates.Length)], times[rnd.Next(times.Length)],
                rnd.Next(6), times[rnd.Next(times.Length)], rnd.Next(2) == 0 ? @"\Microsoft" : @"\microsoft"));
        foreach (GridColumn column in Enum.GetValues<GridColumn>())
            foreach (bool desc in new[] { false, true })
                Axioms("foreign " + column + (desc ? " desc" : " asc"), foreign, GridLogic.CompareForeign(column, desc));
    }

    // ---- filter ----

    public static void TheFilterNeedsEveryWord()
    {
        RowText stock = Row("Stock ingest (Dad)", schedule: "Every weekday at 18:00", program: @"C:\Users\Dad\stocks\ingest.py", state: "", rate: "weekdays");
        T.True("empty filter shows everything", GridLogic.Matches(stock, ""));
        T.True("whitespace too", GridLogic.Matches(stock, "   "));
        T.True("a name word, any case", GridLogic.Matches(stock, "STOCK"));
        T.True("two words in different fields", GridLogic.Matches(stock, "ingest weekday"));
        T.True("the rate", GridLogic.Matches(stock, "weekdays"));
        T.True("the program's file name", GridLogic.Matches(stock, "ingest.py"));
        T.True("the program's path", GridLogic.Matches(stock, @"dad\stocks"));
        T.True("a word that is nowhere does not match", !GridLogic.Matches(stock, "stock backup"));
        T.True("the state", GridLogic.Matches(Row("x", state: "Changed in Task Scheduler"), "changed"));
        T.True("every word must occur", !GridLogic.Matches(stock, "weekday sunday"));
    }

    public static void AForeignRowIsFilteredByNameFolderRateStateAndResult()
    {
        var text = new ForeignText("ScheduledDefrag", @"\Microsoft\Windows\Defrag", "", "Ok \u00B7 2 d ago", true);
        T.True("name", GridLogic.MatchesForeign(text, "daily", "defrag"));
        T.True("folder", GridLogic.MatchesForeign(text, "daily", "windows"));
        T.True("rate", GridLogic.MatchesForeign(text, "daily", "daily"));
        T.True("result", GridLogic.MatchesForeign(text, "daily", "2 d"));
        T.True("nothing", !GridLogic.MatchesForeign(text, "daily", "nothing"));
        T.True("a disabled one says so", GridLogic.MatchesForeign(text with { IsEnabled = false }, "-", "disabled"));
    }

    // ---- counts, status and title ----

    public static void CountsTotalProblemsAndDisabled()
    {
        RowText[] rows =
        [
            Row("ok"), Row("failed", hasProblem: true), Row("damaged", problem: true), Row("off", enabled: false), Row("off and failed", enabled: false, hasProblem: true),
        ];
        T.Eq("counts", (5, 3, 2), GridLogic.Counts(rows));
        T.Eq("nothing", (0, 0, 0), GridLogic.Counts([]));
    }

    public static void TheStatusTextLeavesOutZeroParts()
    {
        T.Eq("loading", "Reading Task Scheduler\u2026", GridLogic.StatusText(false, 0, 0, 0));
        T.Eq("none", "No schedules", GridLogic.StatusText(true, 0, 0, 0));
        T.Eq("one", "1 schedule", GridLogic.StatusText(true, 1, 0, 0));
        T.Eq("some", "8 schedules", GridLogic.StatusText(true, 8, 0, 0));
        T.Eq("problems", "8 schedules \u00B7 4 problems", GridLogic.StatusText(true, 8, 4, 0));
        T.Eq("one problem", "2 schedules \u00B7 1 problem \u00B7 1 disabled", GridLogic.StatusText(true, 2, 1, 1));
        T.Eq("filtered", "8 schedules \u00B7 4 problems \u00B7 showing 3", GridLogic.StatusText(true, 8, 4, 0, 3));
    }

    public static void TheWindowTitleSaysWhatTheWindowHolds()
    {
        T.Eq("loading", "Cadence", GridLogic.ListTitle(false, 0, 0));
        T.Eq("empty", "Cadence", GridLogic.ListTitle(true, 0, 0));
        T.Eq("one schedule", "Cadence - 1 schedule", GridLogic.ListTitle(true, 1, 0));
        T.Eq("some", "Cadence - 8 schedules, 4 problems", GridLogic.ListTitle(true, 8, 4));
        T.Eq("one problem", "Cadence - 2 schedules, 1 problem", GridLogic.ListTitle(true, 2, 1));
        T.Eq("new schedule", "Cadence - New schedule", GridLogic.EditorTitle(true, "Weekdays at 6pm", false));
        T.Eq("an existing one", "Cadence - Stock ingest (Dad)", GridLogic.EditorTitle(false, "  Stock ingest (Dad) ", false));
        T.Eq("unsaved", "Cadence - Stock ingest (Dad) (unsaved)", GridLogic.EditorTitle(false, "Stock ingest (Dad)", true));
        T.Eq("unsaved and new", "Cadence - New schedule (unsaved)", GridLogic.EditorTitle(true, "", true));
    }
}
