using Cadence.App.UI.Logic;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>[P25] The Rate column: runs per week counted from the triggers that really fire, and its label (design 2.3). The counting rule is the contract.</summary>
public static class RateFormatTests
{
    static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.FromHours(-5));

    static ClassifiedTask Task(HolidayCalendar skip, params string[] cron) =>
        new(new ObservedTask("Job", @"\Cadence\Job", "", 3, null, null, null), Ownership.Managed, SyncStatus.InSync, null, null,
            new JobDefinition
            {
                Id = "abcd1234", Name = "Job", Schedule = new ScheduleSpec(new ValueList<string>(cron), skip, new DateOnly(2026, 9, 1)), Run = new RunSpec { Exe = "cmd.exe" },
            },
            ValueList<FieldDiff>.Empty, null);

    static void Case(string name, string[] cron, string label, double perWeek, HolidayCalendar skip = HolidayCalendar.None, RateKind kind = RateKind.Timed, DateTimeOffset? now = null)
    {
        RateInfo r = RateFormat.ForTask(Task(skip, cron), now ?? Now, Eastern);
        T.Eq(name + ": label", label, r.Label);
        T.True(name + ": runs per week", Math.Abs(r.RunsPerWeek - perWeek) < 1e-9, "expected " + perWeek + " actual " + r.RunsPerWeek);
        T.Eq(name + ": kind", kind, r.Kind);
    }

    public static void TheTableOfTheDesign()
    {
        Case("every 15 minutes", ["*/15 * * * *"], "every 15 min", 672);
        Case("every minute", ["* * * * *"], "every minute", 10080);
        Case("hourly", ["0 * * * *"], "hourly", 168);
        Case("every 3 hours", ["15 */3 * * *"], "every 3 h", 56);
        Case("office hours", ["*/15 9-17 * * 1-5"], "every 15 min, weekdays", 180);
        Case("twice a day", ["0 7,19 * * *"], "2× daily", 14);
        Case("twice a day, two lines (the counted fallback)", ["0 7 * * *", "30 19 * * *"], "2× daily", 14);
        Case("irregular", ["5,20,40 */2 * * *"], "~36× daily", 252);
        Case("daily", ["0 2 * * *"], "daily", 7);
        Case("weekdays", ["0 8 * * 1-5"], "weekdays", 5);
        Case("weekdays, NYSE, the week of Thanksgiving is in the window", ["0 18 * * 1-5"], "weekdays", 4.75, HolidayCalendar.Nyse, now: new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(-5)));
        Case("three days a week", ["0 7 * * 1,3,5"], "3× weekly", 3);
        Case("weekly", ["0 9 * * 1"], "weekly", 1);
        Case("twice a month", ["0 9 1,15 * *"], "2× monthly", 24 / 52.0);
        Case("last Friday of the month", ["0 17 * * 5L"], "monthly", 12 / 52.0);
        double days = new DateOnly(2028, 2, 29).DayNumber - new DateOnly(2026, 11, 27).DayNumber;
        Case("leap day", ["0 0 29 2 *"], "less than yearly", 7 / days, kind: RateKind.Rare);
        Case("at sign-in", ["@logon"], "at sign-in", 0, kind: RateKind.AtSignIn);
        Case("daily and at sign-in", ["0 9 * * *", "@logon"], "daily + sign-in", 7);
    }

    static RateInfo Hand(params TaskTrigger[] triggers) => RateFormat.ForTriggers(new ValueList<TaskTrigger>(triggers), HolidayCalendar.None, Now, Eastern);

    public static void TriggersThatAreNotCronAreCountedToo()
    {
        var start = new DateTime(2026, 9, 1, 9, 0, 0);
        RateInfo every3 = Hand(new DailyTrigger(3) { StartBoundary = start });
        T.Eq("every 3 days", "every 3 days", every3.Label);
        T.True("about 7/3 a week (the 28 day count of an every-3-days job is 9 or 10)", Math.Abs(every3.RunsPerWeek - 7 / 3.0) < 0.2, every3.RunsPerWeek.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RateInfo every2Weeks = Hand(new WeeklyTrigger(2, WeekDays.Monday) { StartBoundary = start });
        T.Eq("every 2 weeks", "every 2 weeks", every2Weeks.Label);
        T.True("half a run a week", Math.Abs(every2Weeks.RunsPerWeek - 0.5) < 1e-9, every2Weeks.RunsPerWeek.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RateInfo off = Hand(new TimeTrigger { StartBoundary = start, Enabled = false });
        T.Eq("a disabled trigger never fires", RateKind.Never, off.Kind);
        T.Eq("... and says so", "never", off.Label);
        RateInfo boot = Hand(new UnsupportedTrigger("BootTrigger", "<BootTrigger />"));
        T.Eq("an event trigger", RateKind.EventOnly, boot.Kind);
        T.Eq("... says so", "on an event", boot.Label);
        T.True("... and the tooltip names the element", boot.Tooltip.Contains("BootTrigger", StringComparison.Ordinal));
        T.Eq("nothing readable is a dash", "-", RateInfo.Unknown.Label);
    }

    public static void ADriftedTaskIsMeasuredAsWindowsWillRunIt()
    {
        // Cadence wrote daily at 9:00; somebody changed the task in Task Scheduler to every Monday.
        ClassifiedTask task = Task(HolidayCalendar.None, "0 9 * * *");
        TaskDefinitionModel actual = TaskXmlReader.Read(
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
            + "<Triggers><CalendarTrigger><StartBoundary>2026-09-01T09:00:00</StartBoundary><ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek><Monday /></DaysOfWeek></ScheduleByWeek></CalendarTrigger></Triggers>"
            + "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType></Principal></Principals><Settings><Enabled>true</Enabled></Settings>"
            + "<Actions Context=\"Author\"><Exec><Command>cmd.exe</Command></Exec></Actions></Task>");
        RateInfo inSync = RateFormat.ForTask(task, Now, Eastern);
        RateInfo drifted = RateFormat.ForTask(task with { Sync = SyncStatus.Drifted, Actual = actual }, Now, Eastern);
        T.Eq("in sync: the cron", "daily", inSync.Label);
        T.Eq("drifted: the live trigger, counted", "weekly", drifted.Label);
        T.True("one a week", Math.Abs(drifted.RunsPerWeek - 1) < 1e-9);
    }

    public static void ATemplateUsesTheSameRules()
    {
        Template Get(string id) => Templates.All.Single(t => t.Id == id);
        T.Eq("every 15 minutes", "every 15 min", RateFormat.ForTemplate(Get("every-15-minutes"), Now, Eastern).Label);
        T.Eq("on the hour", "hourly", RateFormat.ForTemplate(Get("hourly"), Now, Eastern).Label);
        T.Eq("nightly", "daily", RateFormat.ForTemplate(Get("nightly-2am"), Now, Eastern).Label);
        T.Eq("weekdays at 6pm skips NYSE holidays, still weekdays", "weekdays", RateFormat.ForTemplate(Get("weekdays-6pm"), Now, Eastern).Label);
        T.Eq("weekly", "weekly", RateFormat.ForTemplate(Get("weekly-monday-9am"), Now, Eastern).Label);
        T.Eq("at login", "at sign-in", RateFormat.ForTemplate(Get("at-login"), Now, Eastern).Label);
        string[] ids = ["every-15-minutes", "hourly", "nightly-2am", "good-morning", "end-of-day", "weekdays-6pm", "weekly-monday-9am", "at-login"];
        double[] perWeek = ids
            .Select(id => RateFormat.ForTemplate(Get(id), Now, Eastern).RunsPerWeek).ToArray();
        T.True("the templates are listed from the most frequent to the least", perWeek.SequenceEqual(perWeek.OrderByDescending(x => x)));
    }

    public static void ARateIsCountedOncePerScheduleAndDay()
    {
        var cache = new RateCache();
        ClassifiedTask every = Task(HolidayCalendar.None, "* * * * *");
        RateInfo first = RateFormat.ForTask(every, Now, Eastern, cache);
        T.Eq("counted once", 1, cache.Computations);
        RateInfo second = RateFormat.ForTask(every, Now.AddSeconds(30), Eastern, cache);
        T.Eq("a refresh 30 s later does not recount", 1, cache.Computations);
        T.True("and gives the same answer", ReferenceEquals(first, second));
        RateFormat.ForTask(Task(HolidayCalendar.None, "0 9 * * *"), Now, Eastern, cache);
        T.Eq("another schedule is counted", 2, cache.Computations);
        RateFormat.ForTask(every, Now.AddDays(1), Eastern, cache);
        T.Eq("tomorrow is counted again", 3, cache.Computations);
    }
}
