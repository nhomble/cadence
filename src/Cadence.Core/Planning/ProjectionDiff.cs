using System.Globalization;
using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;

namespace Cadence.Core.Planning;

/// <summary>
/// English field-by-field differences between two task definitions, over exactly what the fingerprint covers (triggers, principal
/// LogonType/RunLevel, settings, actions). Labels: Time, Days, Interval, Program, Arguments, Enabled, "Setting: name"; for tasks with
/// several triggers (or actions) each label is prefixed "Trigger N: " ("Action N: ").
/// </summary>
public static class ProjectionDiff
{
    static readonly string[] DayAbbrev = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    static readonly string[] MonthAbbrev = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>Differences from <paramref name="from"/> (Before) to <paramref name="to"/> (After). Empty when the projections are equal.</summary>
    public static ValueList<FieldDiff> Between(TaskDefinitionModel from, TaskDefinitionModel to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var list = new List<FieldDiff>();

        Triggers(from.Triggers, to.Triggers, list);
        Principal(from.Principal, to.Principal, list);
        Settings(from.Settings, to.Settings, list);
        Actions(from, to, list);

        // Safety net: never report "no difference" when the canonical forms differ.
        if (list.Count == 0 && !string.Equals(Projection.Canonical(from), Projection.Canonical(to), StringComparison.Ordinal))
            list.Add(new FieldDiff("Definition", "as Cadence wrote it", "changed"));
        return new ValueList<FieldDiff>(list);
    }

    // ---- triggers ----

    static void Triggers(ValueList<TaskTrigger> a, ValueList<TaskTrigger> b, List<FieldDiff> list)
    {
        if (a.Count != b.Count)
        {
            list.Add(new FieldDiff("Triggers", Count(a.Count, "trigger"), Count(b.Count, "trigger")));
            return;
        }
        for (int i = 0; i < a.Count; i++)
            Trigger(a.Count > 1 ? "Trigger " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " : "", a[i], b[i], list);
    }

    static void Trigger(string p, TaskTrigger a, TaskTrigger b, List<FieldDiff> list)
    {
        if (a.GetType() != b.GetType())
        {
            list.Add(new FieldDiff(p + "Trigger type", TypeName(a), TypeName(b)));
            return;
        }
        if (a is UnsupportedTrigger ua && b is UnsupportedTrigger ub)
        {
            if (!string.Equals(ua.RawXml, ub.RawXml, StringComparison.Ordinal))
                list.Add(new FieldDiff(p + "Trigger", ua.ElementName, "changed"));
            return;
        }

        if (a.StartBoundary.TimeOfDay != b.StartBoundary.TimeOfDay)
            list.Add(new FieldDiff(p + "Time", Clock(a.StartBoundary), Clock(b.StartBoundary)));
        if (a.StartBoundary.Date != b.StartBoundary.Date)
            list.Add(new FieldDiff(p + "Start date", a.StartBoundary.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), b.StartBoundary.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (a.EndBoundary != b.EndBoundary)
            list.Add(new FieldDiff(p + "End", Stamp(a.EndBoundary), Stamp(b.EndBoundary)));

        switch (a)
        {
            case DailyTrigger da when b is DailyTrigger db:
                if (da.DaysInterval != db.DaysInterval)
                    list.Add(new FieldDiff(p + "Interval", Every(da.DaysInterval, "day"), Every(db.DaysInterval, "day")));
                break;
            case WeeklyTrigger wa when b is WeeklyTrigger wb:
                if (wa.WeeksInterval != wb.WeeksInterval)
                    list.Add(new FieldDiff(p + "Interval", Every(wa.WeeksInterval, "week"), Every(wb.WeeksInterval, "week")));
                if (wa.Days != wb.Days) list.Add(new FieldDiff(p + "Days", Days(wa.Days), Days(wb.Days)));
                break;
            case MonthlyTrigger ma when b is MonthlyTrigger mb:
                if (!ma.Days.Distinct().Order().SequenceEqual(mb.Days.Distinct().Order()))
                    list.Add(new FieldDiff(p + "Days", MonthDays(ma.Days), MonthDays(mb.Days)));
                if (ma.LastDay != mb.LastDay) list.Add(new FieldDiff(p + "Last day", YesNo(ma.LastDay), YesNo(mb.LastDay)));
                if (ma.Months != mb.Months) list.Add(new FieldDiff(p + "Months", Months(ma.Months), Months(mb.Months)));
                break;
            case MonthlyDowTrigger da2 when b is MonthlyDowTrigger db2:
                if (!da2.Weeks.Distinct().Order().SequenceEqual(db2.Weeks.Distinct().Order()))
                    list.Add(new FieldDiff(p + "Weeks", Weeks(da2.Weeks), Weeks(db2.Weeks)));
                if (da2.Days != db2.Days) list.Add(new FieldDiff(p + "Days", Days(da2.Days), Days(db2.Days)));
                if (da2.Months != db2.Months) list.Add(new FieldDiff(p + "Months", Months(da2.Months), Months(db2.Months)));
                break;
            case LogonTrigger la when b is LogonTrigger lb:
                if (string.IsNullOrEmpty(la.UserId) != string.IsNullOrEmpty(lb.UserId))
                    list.Add(new FieldDiff(p + "Applies to", string.IsNullOrEmpty(la.UserId) ? "any user" : "one user", string.IsNullOrEmpty(lb.UserId) ? "any user" : "one user"));
                if (la.Delay != lb.Delay) list.Add(new FieldDiff(p + "Delay", Dur(la.Delay, "none"), Dur(lb.Delay, "none")));
                break;
        }

        if (a.Repetition != b.Repetition)
        {
            if (a.Repetition?.Interval != b.Repetition?.Interval)
                list.Add(new FieldDiff(p + "Interval", Rep(a.Repetition), Rep(b.Repetition)));
            if (a.Repetition is not null && b.Repetition is not null)
            {
                if (a.Repetition.Duration != b.Repetition.Duration)
                    list.Add(new FieldDiff(p + "Repeat for", Dur(a.Repetition.Duration, "no end"), Dur(b.Repetition.Duration, "no end")));
                if (a.Repetition.StopAtDurationEnd != b.Repetition.StopAtDurationEnd)
                    list.Add(new FieldDiff(p + "Stop at end of repeat", YesNo(a.Repetition.StopAtDurationEnd), YesNo(b.Repetition.StopAtDurationEnd)));
            }
        }
        if (a is not LogonTrigger && a.RandomDelay != b.RandomDelay)
            list.Add(new FieldDiff(p + "Random delay", Dur(a.RandomDelay, "none"), Dur(b.RandomDelay, "none")));
        if (a.ExecutionTimeLimit != b.ExecutionTimeLimit)
            list.Add(new FieldDiff(p + "Trigger time limit", Dur(a.ExecutionTimeLimit, "none"), Dur(b.ExecutionTimeLimit, "none")));
        if (a.Enabled != b.Enabled)
            list.Add(new FieldDiff(p + "Trigger enabled", a.Enabled ? "enabled" : "disabled", b.Enabled ? "enabled" : "disabled"));
    }

    // ---- principal, settings ----

    static void Principal(PrincipalModel a, PrincipalModel b, List<FieldDiff> list)
    {
        if (!string.Equals(a.LogonType, b.LogonType, StringComparison.Ordinal))
            list.Add(new FieldDiff("Setting: Logon type", a.LogonType, b.LogonType));
        if (!string.Equals(a.RunLevel, b.RunLevel, StringComparison.Ordinal))
            list.Add(new FieldDiff("Setting: Run level", a.RunLevel, b.RunLevel));
    }

    static void Settings(SettingsModel a, SettingsModel b, List<FieldDiff> list)
    {
        if (a.Enabled != b.Enabled) list.Add(new FieldDiff("Enabled", a.Enabled ? "enabled" : "disabled", b.Enabled ? "enabled" : "disabled"));
        Flag(list, "Hidden", a.Hidden, b.Hidden);
        if (!string.Equals(a.MultipleInstancesPolicy, b.MultipleInstancesPolicy, StringComparison.Ordinal))
            list.Add(new FieldDiff("Setting: Multiple instances", a.MultipleInstancesPolicy, b.MultipleInstancesPolicy));
        Flag(list, "Start when available", a.StartWhenAvailable, b.StartWhenAvailable);
        Flag(list, "Don't start on batteries", a.DisallowStartIfOnBatteries, b.DisallowStartIfOnBatteries);
        Flag(list, "Stop on batteries", a.StopIfGoingOnBatteries, b.StopIfGoingOnBatteries);
        if (a.ExecutionTimeLimit != b.ExecutionTimeLimit)
            list.Add(new FieldDiff("Setting: Time limit", Dur(a.ExecutionTimeLimit, "unlimited"), Dur(b.ExecutionTimeLimit, "unlimited")));
        Flag(list, "Wake to run", a.WakeToRun, b.WakeToRun);
        Flag(list, "Allow run on demand", a.AllowStartOnDemand, b.AllowStartOnDemand);
        Flag(list, "Allow forced stop", a.AllowHardTerminate, b.AllowHardTerminate);
        Flag(list, "Only with network", a.RunOnlyIfNetworkAvailable, b.RunOnlyIfNetworkAvailable);
        Flag(list, "Only when idle", a.RunOnlyIfIdle, b.RunOnlyIfIdle);
        if (a.Priority != b.Priority)
            list.Add(new FieldDiff("Setting: Priority", a.Priority.ToString(CultureInfo.InvariantCulture), b.Priority.ToString(CultureInfo.InvariantCulture)));
        if (a.DeleteExpiredTaskAfter != b.DeleteExpiredTaskAfter)
            list.Add(new FieldDiff("Setting: Delete after expiry", Dur(a.DeleteExpiredTaskAfter, "never"), Dur(b.DeleteExpiredTaskAfter, "never")));
        if (a.RestartCount != b.RestartCount)
            list.Add(new FieldDiff("Setting: Restart count", a.RestartCount?.ToString(CultureInfo.InvariantCulture) ?? "none", b.RestartCount?.ToString(CultureInfo.InvariantCulture) ?? "none"));
        if (a.RestartInterval != b.RestartInterval)
            list.Add(new FieldDiff("Setting: Restart interval", Dur(a.RestartInterval, "none"), Dur(b.RestartInterval, "none")));
        if (!a.OtherElements.Order(StringComparer.Ordinal).SequenceEqual(b.OtherElements.Order(StringComparer.Ordinal)))
            list.Add(new FieldDiff("Setting: Other", Count(a.OtherElements.Count, "extra element"), Count(b.OtherElements.Count, "extra element")));
    }

    static void Flag(List<FieldDiff> list, string label, bool a, bool b)
    {
        if (a != b) list.Add(new FieldDiff("Setting: " + label, YesNo(a), YesNo(b)));
    }

    // ---- actions ----

    static void Actions(TaskDefinitionModel a, TaskDefinitionModel b, List<FieldDiff> list)
    {
        if (a.ExecActions.Count != b.ExecActions.Count)
        {
            list.Add(new FieldDiff("Program", Count(a.ExecActions.Count, "action"), Count(b.ExecActions.Count, "action")));
        }
        else
        {
            for (int i = 0; i < a.ExecActions.Count; i++)
            {
                string p = a.ExecActions.Count > 1 ? "Action " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " : "";
                ExecActionModel x = a.ExecActions[i], y = b.ExecActions[i];
                if (!string.Equals(x.Command, y.Command, StringComparison.Ordinal))
                    list.Add(new FieldDiff(p + "Program", Trunc(x.Command), Trunc(y.Command)));
                if (!string.Equals(x.Arguments ?? "", y.Arguments ?? "", StringComparison.Ordinal))
                    list.Add(new FieldDiff(p + "Arguments", Args(x.Arguments), Args(y.Arguments)));
                if (!string.Equals(x.WorkingDirectory ?? "", y.WorkingDirectory ?? "", StringComparison.Ordinal))
                    list.Add(new FieldDiff(p + "Working folder", Trunc(x.WorkingDirectory ?? "(none)"), Trunc(y.WorkingDirectory ?? "(none)")));
            }
        }
        if (!a.OtherActionsXml.SequenceEqual(b.OtherActionsXml))
            list.Add(new FieldDiff("Other actions", Count(a.OtherActionsXml.Count, "action"), Count(b.OtherActionsXml.Count, "action")));
    }

    // ---- formatting ----

    static string Args(string? args) =>
        string.IsNullOrEmpty(args) ? "(none)" : WrapperScript.LooksLikeWrapper(args) ? "Cadence wrapper script" : Trunc(args);

    static string Trunc(string s) => s.Length <= 80 ? s : s[..79] + "…";

    static string Count(int n, string noun) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

    static string Every(int n, string unit) =>
        n == 1 ? "every " + unit : "every " + n.ToString(CultureInfo.InvariantCulture) + " " + unit + "s";

    static string YesNo(bool v) => v ? "yes" : "no";

    static string Clock(DateTime d) =>
        d.ToString(d.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);

    static string Stamp(DateTime? d) =>
        d is { } v ? v.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "none";

    static string TypeName(TaskTrigger t) => t switch
    {
        TimeTrigger => "one time",
        DailyTrigger => "daily",
        WeeklyTrigger => "weekly",
        MonthlyTrigger => "monthly",
        MonthlyDowTrigger => "monthly on a weekday",
        LogonTrigger => "at sign-in",
        UnsupportedTrigger u => u.ElementName,
        _ => t.GetType().Name,
    };

    static string Rep(Repetition? r) => r is null ? "none" : "every " + Dur(r.Interval, "none");

    /// <summary>"15 min", "1 h 30 min", "2 days", "45 s".</summary>
    static string Dur(TimeSpan? d, string none)
    {
        if (d is not { } t) return none;
        if (t == TimeSpan.Zero) return "0 s";
        var parts = new List<string>();
        if (t.Days > 0) parts.Add(t.Days.ToString(CultureInfo.InvariantCulture) + (t.Days == 1 ? " day" : " days"));
        if (t.Hours > 0) parts.Add(t.Hours.ToString(CultureInfo.InvariantCulture) + " h");
        if (t.Minutes > 0) parts.Add(t.Minutes.ToString(CultureInfo.InvariantCulture) + " min");
        if (t.Seconds > 0) parts.Add(t.Seconds.ToString(CultureInfo.InvariantCulture) + " s");
        return string.Join(" ", parts);
    }

    /// <summary>Monday-first: "Mon-Fri", "Mon, Wed", "every day"; runs of three or more days collapse into a range.</summary>
    static string Days(WeekDays days)
    {
        if ((days & WeekDays.All) == WeekDays.All) return "every day";
        if (days == WeekDays.None) return "no days";
        int[] order = [1, 2, 3, 4, 5, 6, 0];   // Mon..Sat, Sun
        var present = order.Select(i => ((int)days & (1 << i)) != 0).ToArray();
        return Ranges(present, i => DayAbbrev[order[i]]);
    }

    static string Months(MonthSet months)
    {
        if ((months & MonthSet.All) == MonthSet.All) return "every month";
        if (months == MonthSet.None) return "no months";
        var present = Enumerable.Range(0, 12).Select(i => ((int)months & (1 << i)) != 0).ToArray();
        return Ranges(present, i => MonthAbbrev[i]);
    }

    static string Ranges(bool[] present, Func<int, string> name)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < present.Length)
        {
            if (!present[i]) { i++; continue; }
            int j = i;
            while (j + 1 < present.Length && present[j + 1]) j++;
            if (j - i >= 2) parts.Add(name(i) + "-" + name(j));
            else for (int k = i; k <= j; k++) parts.Add(name(k));
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    static string MonthDays(ValueList<int> days) =>
        days.Count == 0 ? "none" : string.Join(", ", days.Distinct().Order().Select(d => d.ToString(CultureInfo.InvariantCulture)));

    static string Weeks(ValueList<WeekOfMonth> weeks) =>
        string.Join(", ", weeks.Distinct().Order().Select(w => w.ToString().ToLowerInvariant()));
}
