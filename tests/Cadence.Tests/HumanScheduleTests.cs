using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class HumanScheduleTests
{
    static readonly DateOnly Anchor = new(2026, 9, 30);
    static readonly MapContext Ctx = new(@"TESTPC\tester");

    static TimeOnly At(int h, int m = 0) => new(h, m);

    static ValueList<TimeOnly> Times(params TimeOnly[] t) => new(t);

    static HumanSchedule Make(HumanFrequency f, params TimeOnly[] times) =>
        HumanSchedule.Default(f) with { Times = Times(times) };

    static string Joined(HumanSchedule h) => string.Join(" + ", HumanScheduleMapper.ToCron(h));

    static ValueList<string> Lines(params string[] l) => new(l);

    // ----- ToCron table ---------------------------------------------------------------------------

    public static void ToCronTable()
    {
        var rows = new (string Name, HumanSchedule H, string Cron)[]
        {
            ("Daily 7:00+19:00", Make(HumanFrequency.Daily, At(7), At(19)), "0 7,19 * * *"),
            ("Daily 7:00+19:30", Make(HumanFrequency.Daily, At(7), At(19, 30)), "0 7 * * * + 30 19 * * *"),
            ("Daily single", Make(HumanFrequency.Daily, At(9)), "0 9 * * *"),
            ("Daily unsorted input", Make(HumanFrequency.Daily, At(19), At(7), At(7)), "0 7,19 * * *"),
            ("Daily minutes sorted", Make(HumanFrequency.Daily, At(10, 45), At(8, 15), At(9, 15)), "15 8,9 * * * + 45 10 * * *"),
            ("Weekdays 18:00", Make(HumanFrequency.Weekdays, At(18)), "0 18 * * 1-5"),
            ("SpecificDays Mon/Wed/Fri 7:00",
                Make(HumanFrequency.SpecificDays, At(7)) with { Days = WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday }, "0 7 * * 1,3,5"),
            ("SpecificDays Sat+Sun", Make(HumanFrequency.SpecificDays, At(10)) with { Days = WeekDays.Weekend }, "0 10 * * 6,0"),
            ("SpecificDays Sunday only", Make(HumanFrequency.SpecificDays, At(10)) with { Days = WeekDays.Sunday }, "0 10 * * 0"),
            ("MonthlyOnDates 1,15+Last 9:00",
                Make(HumanFrequency.MonthlyOnDates, At(9)) with { MonthDates = ValueList.Of(1, 15), LastDayOfMonth = true }, "0 9 1,15,L * *"),
            ("MonthlyOnDates only last",
                Make(HumanFrequency.MonthlyOnDates, At(23)) with { LastDayOfMonth = true }, "0 23 L * *"),
            ("MonthlyOnDates sorted", Make(HumanFrequency.MonthlyOnDates, At(9)) with { MonthDates = ValueList.Of(15, 1) }, "0 9 1,15 * *"),
            ("MonthlyOnWeekday Last Fri 17:00",
                Make(HumanFrequency.MonthlyOnWeekday, At(17)) with { Week = WeekOfMonth.Last, Weekday = DayOfWeek.Friday }, "0 17 * * 5L"),
            ("MonthlyOnWeekday First Mon 9:00",
                Make(HumanFrequency.MonthlyOnWeekday, At(9)) with { Week = WeekOfMonth.First, Weekday = DayOfWeek.Monday }, "0 9 * * 1#1"),
            ("MonthlyOnWeekday Fourth Sun",
                Make(HumanFrequency.MonthlyOnWeekday, At(9)) with { Week = WeekOfMonth.Fourth, Weekday = DayOfWeek.Sunday }, "0 9 * * 0#4"),
            ("EveryNMinutes 15 hours 9-17 weekdays",
                HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 15, FromHour = 9, ToHour = 17, Days = WeekDays.Weekdays },
                "*/15 9-17 * * 1-5"),
            ("EveryNMinutes 15 all day", HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 15 }, "*/15 * * * *"),
            ("EveryNMinutes 1", HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 1 }, "* * * * *"),
            ("EveryNMinutes 30 Mon+Wed",
                HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 30, Days = WeekDays.Monday | WeekDays.Wednesday }, "*/30 * * * 1,3"),
            ("EveryNMinutes window covering the day is dropped",
                HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 10, FromHour = 0, ToHour = 23 }, "*/10 * * * *"),
            ("EveryNHours 3 at :15", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 3, MinuteOffset = 15 }, "15 */3 * * *"),
            ("EveryNHours 1", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 1 }, "0 * * * *"),
            ("EveryNHours 2 window 8-22",
                HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 2, FromHour = 8, ToHour = 22, MinuteOffset = 30 }, "30 8-22/2 * * *"),
            ("EveryNHours 3 window snaps its end",
                HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 3, FromHour = 9, ToHour = 17 }, "0 9-15/3 * * *"),
            ("EveryNHours 1 window", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 1, FromHour = 9, ToHour = 17 }, "0 9-17 * * *"),
            ("EveryNHours weekdays",
                HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 6, Days = WeekDays.Weekdays }, "0 */6 * * 1-5"),
            ("AtSignIn", HumanSchedule.Default(HumanFrequency.AtSignIn), "@logon"),
        };
        foreach (var (name, h, cron) in rows) T.Eq(name, cron, Joined(h));
    }

    public static void InvalidSchedulesGiveNoCron()
    {
        var rows = new (string Name, HumanSchedule H)[]
        {
            ("Daily without times", HumanSchedule.Default(HumanFrequency.Daily)),
            ("11 times", Make(HumanFrequency.Daily, [.. Enumerable.Range(0, 11).Select(i => At(i))])),
            ("SpecificDays without days", Make(HumanFrequency.SpecificDays, At(9)) with { Days = WeekDays.None }),
            ("MonthlyOnDates without dates", Make(HumanFrequency.MonthlyOnDates, At(9))),
            ("MonthlyOnDates day 32", Make(HumanFrequency.MonthlyOnDates, At(9)) with { MonthDates = ValueList.Of(32) }),
            ("EveryNMinutes interval 7", HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 7 }),
            ("EveryNMinutes interval 0", HumanSchedule.Default(HumanFrequency.EveryNMinutes)),
            ("EveryNMinutes window reversed", HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 5, FromHour = 17, ToHour = 9 }),
            ("EveryNHours interval 5", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 5 }),
            ("EveryNHours minute 60", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 2, MinuteOffset = 60 }),
            ("EveryNHours window with one run", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 12, FromHour = 9, ToHour = 17 }),
        };
        foreach (var (name, h) in rows)
        {
            T.Eq(name + ": no cron", 0, HumanScheduleMapper.ToCron(h).Count);
            T.True(name + ": not valid", !HumanScheduleMapper.IsValid(h));
        }
    }

    public static void NormalizeRules()
    {
        var daily = HumanScheduleMapper.Normalize(Make(HumanFrequency.SpecificDays, At(9)) with { Days = WeekDays.All });
        T.Eq("SpecificDays all -> Daily", HumanFrequency.Daily, daily.Frequency);
        var weekdays = HumanScheduleMapper.Normalize(Make(HumanFrequency.SpecificDays, At(9)) with { Days = WeekDays.Weekdays });
        T.Eq("SpecificDays Mon-Fri -> Weekdays", HumanFrequency.Weekdays, weekdays.Frequency);
        var clean = HumanScheduleMapper.Normalize(Make(HumanFrequency.Daily, At(9, 0)) with { Interval = 5, FromHour = 3, Week = WeekOfMonth.Last });
        T.True("unused fields reset", clean == Make(HumanFrequency.Daily, At(9)));
        var seconds = HumanScheduleMapper.Normalize(Make(HumanFrequency.Daily, new TimeOnly(9, 0, 30)));
        T.Eq("seconds dropped", At(9), seconds.Times[0]);
        var half = HumanScheduleMapper.Normalize(HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 5, FromHour = 9 });
        T.True("half a window is no window", half.FromHour is null && half.ToHour is null);
        T.True("Normalize is idempotent",
            HumanScheduleMapper.Normalize(daily) == daily && HumanScheduleMapper.Normalize(clean) == clean);
        var covers = HumanScheduleMapper.Normalize(HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 2, FromHour = 0, ToHour = 23 });
        T.True("whole-day hour window dropped", covers.FromHour is null && covers.ToHour is null);
        var snapped = HumanScheduleMapper.Normalize(HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 4, FromHour = 8, ToHour = 18 });
        T.True("hour window end snaps to last run", snapped.FromHour == 8 && snapped.ToHour == 16);
    }

    // ----- TryFromCron ----------------------------------------------------------------------------

    public static void TryFromCronCannotBeEditedSimply()
    {
        var rows = new string[][]
        {
            ["*/7 * * * *"],
            ["0 9 13 * 5"],
            ["5,20,40 */2 * * *"],
            ["0 9 1 */3 *"],                 // months restricted
            ["0 9 * 1,7 *"],
            ["0 0 29 2 *"],
            ["0,30 9 * * *"],                // minute list, one hour
            ["0,30 9-17 * * *"],
            ["0 9-17/5 * * *"],              // interval 5 is not offered for hours
            ["*/7 9-17 * * *"],
            ["*/90 * * * *"],                // does not compile at all
            ["0 9 * * 1#1,2#2"],
            ["0 9 * * 1#1,5"],
            ["0 9 * * 1#5"],
            ["0 9 15W * *"],
            ["0 9 * * * 2027"],
            ["@reboot"],
            ["@logon", "0 9 * * *"],
            ["hello"],
            ["0 7 * * *", "30 19 * * 1"],    // two different shapes
            ["0 9 1,15 * *", "0 9 * * 1"],
            ["0 7 * * *", "*/15 * * * *"],
            ["*/15 * * * *", "*/20 * * * *"],
            ["0 0 * * *", "0 1 * * *", "0 2 * * *", "0 3 * * *", "0 4 * * *", "0 5 * * *", "0 6 * * *", "0 7 * * *", "0 8 * * *", "0 9 * * *", "0 10 * * *"],
            ["0 12-12/12 * * *"],            // window with a single run
        };
        foreach (var lines in rows)
        {
            T.True(string.Join(" + ", lines) + " is cron-only", !HumanScheduleMapper.TryFromCron(new ValueList<string>(lines), out _));
        }
        T.True("empty list", !HumanScheduleMapper.TryFromCron(ValueList<string>.Empty, out _));
    }

    public static void TryFromCronRecognizesShapes()
    {
        var rows = new (string[] Lines, HumanSchedule H)[]
        {
            (["0 8 * * 1-5"], Make(HumanFrequency.Weekdays, At(8))),
            (["30 17 * * MON-FRI"], Make(HumanFrequency.Weekdays, At(17, 30))),
            (["30 17 * * mon,tue,wed,thu,fri"], Make(HumanFrequency.Weekdays, At(17, 30))),
            (["0 9 * * 0-6"], Make(HumanFrequency.Daily, At(9))),
            (["0 9 * * 0,1,2,3,4,5,6,7"], Make(HumanFrequency.Daily, At(9))),
            (["@daily"], Make(HumanFrequency.Daily, At(0))),
            (["@midnight"], Make(HumanFrequency.Daily, At(0))),
            (["0 0 0 * * *"], Make(HumanFrequency.Daily, At(0))),
            (["@weekly"], Make(HumanFrequency.SpecificDays, At(0)) with { Days = WeekDays.Sunday }),
            (["@monthly"], Make(HumanFrequency.MonthlyOnDates, At(0)) with { MonthDates = ValueList.Of(1) }),
            (["@hourly"], HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 1 }),
            (["@logon"], HumanSchedule.Default(HumanFrequency.AtSignIn)),
            (["@LOGON"], HumanSchedule.Default(HumanFrequency.AtSignIn)),
            (["0 7 * * *", "30 19 * * *"], Make(HumanFrequency.Daily, At(7), At(19, 30))),
            (["0 19 * * *", "0 7 * * *"], Make(HumanFrequency.Daily, At(7), At(19))),
            (["0 7,19 * * *"], Make(HumanFrequency.Daily, At(7), At(19))),
            (["0 7 * * 1,3,5"], Make(HumanFrequency.SpecificDays, At(7)) with { Days = WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday }),
            (["0 10 * * SAT,SUN"], Make(HumanFrequency.SpecificDays, At(10)) with { Days = WeekDays.Weekend }),
            (["0 9 1,15,L * *"], Make(HumanFrequency.MonthlyOnDates, At(9)) with { MonthDates = ValueList.Of(1, 15), LastDayOfMonth = true }),
            (["0 23 L * *"], Make(HumanFrequency.MonthlyOnDates, At(23)) with { LastDayOfMonth = true }),
            (["0 9 1-31 * *"], Make(HumanFrequency.MonthlyOnDates, At(9)) with { MonthDates = new ValueList<int>(Enumerable.Range(1, 31)) }),
            (["0 17 * * 5L"], Make(HumanFrequency.MonthlyOnWeekday, At(17)) with { Week = WeekOfMonth.Last, Weekday = DayOfWeek.Friday }),
            (["0 9 * * 1#1"], Make(HumanFrequency.MonthlyOnWeekday, At(9)) with { Week = WeekOfMonth.First, Weekday = DayOfWeek.Monday }),
            (["0 9 * * 0#2"], Make(HumanFrequency.MonthlyOnWeekday, At(9)) with { Week = WeekOfMonth.Second, Weekday = DayOfWeek.Sunday }),
            (["0 9 * * 0L"], Make(HumanFrequency.MonthlyOnWeekday, At(9)) with { Week = WeekOfMonth.Last, Weekday = DayOfWeek.Sunday }),
            (["*/15 9-17 * * 1-5"],
                HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 15, FromHour = 9, ToHour = 17, Days = WeekDays.Weekdays }),
            (["*/15 * * * *"], HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 15 }),
            (["* * * * *"], HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 1 }),
            (["*/1 * * * *"], HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 1 }),
            (["*/30 * * * 0,6"], HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 30, Days = WeekDays.Weekend }),
            (["*/10 0-23 * * *"], HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 10 }),
            (["15 */3 * * *"], HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 3, MinuteOffset = 15 }),
            (["0 * * * *"], HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 1 }),
            (["0 */2 * * *"], HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 2 }),
            (["0 0-22/2 * * *"], HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 2 }),
            (["0 9-17/3 * * *"], HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 3, FromHour = 9, ToHour = 15 }),
            (["0 9-17 * * 1-5"], HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 1, FromHour = 9, ToHour = 17, Days = WeekDays.Weekdays }),
        };
        foreach (var (lines, expected) in rows)
        {
            bool ok = HumanScheduleMapper.TryFromCron(new ValueList<string>(lines), out var h);
            T.True(string.Join(" + ", lines) + " recognized", ok);
            T.True(string.Join(" + ", lines) + " value", h == expected, h.ToString());
        }
    }

    // ----- round trips ----------------------------------------------------------------------------

    static HumanSchedule Gen(Random r)
    {
        var f = (HumanFrequency)r.Next(8);
        var h = HumanSchedule.Default(f);
        var times = Enumerable.Range(0, r.Next(1, 11)).Select(_ => At(r.Next(24), r.Next(4) == 0 ? r.Next(60) : 0));
        WeekDays RandomDays()
        {
            var d = WeekDays.None;
            foreach (var flag in new[] { WeekDays.Monday, WeekDays.Tuesday, WeekDays.Wednesday, WeekDays.Thursday, WeekDays.Friday, WeekDays.Saturday, WeekDays.Sunday })
            {
                if (r.Next(2) == 0) d |= flag;
            }
            return d == WeekDays.None ? WeekDays.Tuesday : d;
        }
        int? from = null, to = null;
        if (r.Next(2) == 0)
        {
            from = r.Next(0, 20);
            to = r.Next(from.Value, 24);
        }
        switch (f)
        {
            case HumanFrequency.Daily:
            case HumanFrequency.Weekdays:
                return h with { Times = new ValueList<TimeOnly>(times) };
            case HumanFrequency.SpecificDays:
                return h with { Times = new ValueList<TimeOnly>(times), Days = RandomDays() };
            case HumanFrequency.MonthlyOnDates:
                return h with
                {
                    Times = new ValueList<TimeOnly>(times),
                    MonthDates = new ValueList<int>(Enumerable.Range(0, r.Next(0, 6)).Select(_ => r.Next(1, 32))),
                    LastDayOfMonth = r.Next(3) == 0,
                };
            case HumanFrequency.MonthlyOnWeekday:
                return h with { Times = new ValueList<TimeOnly>(times), Week = (WeekOfMonth)r.Next(1, 6), Weekday = (DayOfWeek)r.Next(7) };
            case HumanFrequency.EveryNMinutes:
                return h with
                {
                    Interval = HumanScheduleMapper.MinuteIntervals[r.Next(HumanScheduleMapper.MinuteIntervals.Count)],
                    Days = r.Next(2) == 0 ? WeekDays.All : RandomDays(), FromHour = from, ToHour = to,
                };
            case HumanFrequency.EveryNHours:
                return h with
                {
                    Interval = HumanScheduleMapper.HourIntervals[r.Next(HumanScheduleMapper.HourIntervals.Count)],
                    MinuteOffset = r.Next(60), Days = r.Next(2) == 0 ? WeekDays.All : RandomDays(), FromHour = from, ToHour = to,
                };
            default:
                return h;
        }
    }

    public static void RoundTripProperty()
    {
        var r = new Random(20261001);
        int valid = 0, tried = 0;
        var frequencies = new HashSet<HumanFrequency>();
        while (valid < 400 && tried < 20_000)
        {
            tried++;
            var h = Gen(r);
            if (!HumanScheduleMapper.IsValid(h)) continue;
            valid++;
            frequencies.Add(h.Frequency);
            var n = HumanScheduleMapper.Normalize(h);
            var cron = HumanScheduleMapper.ToCron(h);
            bool ok = HumanScheduleMapper.TryFromCron(cron, out var back);
            T.True($"#{valid} {n.Frequency} {string.Join(" + ", cron)} recognized", ok);
            T.True($"#{valid} {string.Join(" + ", cron)} round trip", back == n, back.ToString());
            // the regenerated cron is the same text, and it compiles
            T.Eq($"#{valid} stable text", string.Join(" + ", cron), Joined(back));
            var compiled = CronCompiler.Compile(new ScheduleSpec(cron, HolidayCalendar.None, Anchor), Ctx);
            T.True($"#{valid} compiles", compiled.Success, string.Join(" ", compiled.Diagnostics.Select(d => d.Message)));
            T.True($"#{valid} has english", EnglishRenderer.Describe(compiled, HolidayCalendar.None) != "Invalid schedule");
        }
        T.True("at least 200 valid schedules generated", valid >= 200, valid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        T.Eq("every frequency exercised", 8, frequencies.Count);
    }

    public static void RoundTripExhaustiveEveryN()
    {
        int count = 0;
        foreach (int n in HumanScheduleMapper.MinuteIntervals)
        {
            foreach (var window in new (int?, int?)[] { (null, null), (0, 23), (9, 17), (9, 9), (0, 5), (22, 23) })
            {
                foreach (var days in new[] { WeekDays.All, WeekDays.Weekdays, WeekDays.Weekend, WeekDays.Monday })
                {
                    var h = HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = n, FromHour = window.Item1, ToHour = window.Item2, Days = days };
                    count += Check(h);
                }
            }
        }
        foreach (int n in HumanScheduleMapper.HourIntervals)
        {
            foreach (int minute in new[] { 0, 1, 15, 59 })
            {
                foreach (var window in new (int?, int?)[] { (null, null), (0, 23), (0, 12), (6, 18), (9, 17), (1, 23), (12, 23) })
                {
                    var h = HumanSchedule.Default(HumanFrequency.EveryNHours) with
                    {
                        Interval = n, MinuteOffset = minute, FromHour = window.Item1, ToHour = window.Item2, Days = WeekDays.Weekdays,
                    };
                    count += Check(h);
                }
            }
        }
        T.True("exhaustive EveryN cases ran", count > 200);
    }

    static int Check(HumanSchedule h)
    {
        if (!HumanScheduleMapper.IsValid(h)) return 0;
        var cron = HumanScheduleMapper.ToCron(h);
        bool ok = HumanScheduleMapper.TryFromCron(cron, out var back);
        T.True(string.Join(" + ", cron) + " round trip", ok && back == HumanScheduleMapper.Normalize(h), back.ToString());
        return 1;
    }

    public static void EveryTemplateIsSimpleAndSemanticallyEqual()
    {
        foreach (var t in Templates.All)
        {
            var lines = ValueList.Of(t.Cron);
            bool ok = HumanScheduleMapper.TryFromCron(lines, out var h);
            T.True(t.Id + " recognized by the Simple view", ok);
            var again = HumanScheduleMapper.ToCron(h);
            var a = CronCompiler.Compile(new ScheduleSpec(lines, HolidayCalendar.None, Anchor), Ctx);
            var b = CronCompiler.Compile(new ScheduleSpec(again, HolidayCalendar.None, Anchor), Ctx);
            T.True(t.Id + " same triggers", a.Success && b.Success && a.Triggers.Equals(b.Triggers), string.Join(" + ", again));
            T.Eq(t.Id + " same english", EnglishRenderer.Describe(a, HolidayCalendar.None), EnglishRenderer.Describe(b, HolidayCalendar.None));
        }
    }

    public static void SimpleEnglishMatchesWhatTheViewPromises()
    {
        var rows = new (string Name, HumanSchedule H, string Text)[]
        {
            ("Daily 7:00+19:00", Make(HumanFrequency.Daily, At(7), At(19)), "Every day at 7:00 and 19:00"),
            ("Weekdays 18:00", Make(HumanFrequency.Weekdays, At(18)), "Every weekday at 18:00"),
            ("Mon/Wed/Fri 7:00", Make(HumanFrequency.SpecificDays, At(7)) with { Days = WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday },
                "Every Monday, Wednesday and Friday at 7:00"),
            ("Last Fri 17:00", Make(HumanFrequency.MonthlyOnWeekday, At(17)) with { Week = WeekOfMonth.Last, Weekday = DayOfWeek.Friday },
                "On the last Friday of every month at 17:00"),
            ("1st, 15th, last", Make(HumanFrequency.MonthlyOnDates, At(9)) with { MonthDates = ValueList.Of(1, 15), LastDayOfMonth = true },
                "On the 1st, 15th and last day of every month at 9:00"),
            ("Every 15 min 9-17 weekdays",
                HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Interval = 15, FromHour = 9, ToHour = 17, Days = WeekDays.Weekdays },
                "Every 15 minutes from 9:00 to 17:45, on weekdays"),
            ("Every 2 hours", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 2 }, "Every 2 hours"),
            ("Every hour", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 1 }, "Every hour, on the hour"),
            ("Every hour 9-17", HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 1, FromHour = 9, ToHour = 17 }, "Every hour from 9:00 to 17:00"),
            ("At sign in", HumanSchedule.Default(HumanFrequency.AtSignIn), "When you sign in"),
        };
        foreach (var (name, h, text) in rows)
        {
            T.Eq(name, text, EnglishRenderer.Describe(new ScheduleSpec(HumanScheduleMapper.ToCron(h), HolidayCalendar.None, Anchor)));
        }
    }
}
