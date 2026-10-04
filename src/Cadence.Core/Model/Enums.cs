namespace Cadence.Core.Model;

[Flags]
public enum WeekDays
{
    None = 0, Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8,
    Thursday = 16, Friday = 32, Saturday = 64, Weekdays = 62, Weekend = 65, All = 127,
}   // same bits as TS DaysOfWeek

[Flags]
public enum MonthSet
{
    None = 0, January = 1, February = 2, March = 4, April = 8, May = 16, June = 32,
    July = 64, August = 128, September = 256, October = 512, November = 1024, December = 2048, All = 4095,
}

public enum WeekOfMonth { First = 1, Second = 2, Third = 3, Fourth = 4, Last = 5 }

public enum OutputEncoding { Oem, Utf8 }

/// <summary>The "verb" of a job. Only RunProgram ships in v1 (extension point).</summary>
public enum ActionKind { RunProgram }

public enum HolidayCalendar { None, UsFederal, Nyse }
