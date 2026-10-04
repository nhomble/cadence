using Cadence.Core.Model;

namespace Cadence.Core.Text;

public enum HumanFrequency { Daily, Weekdays, SpecificDays, MonthlyOnDates, MonthlyOnWeekday, EveryNMinutes, EveryNHours, AtSignIn }

/// <summary>
/// The Simple view of a schedule (PLAN.md 3.5). Only the fields that belong to <see cref="Frequency"/> matter; the others
/// hold the neutral defaults of <see cref="Default"/>, which is also what <see cref="HumanScheduleMapper.Normalize"/> and
/// <see cref="HumanScheduleMapper.TryFromCron"/> produce, so records compare equal after a cron round trip.
/// </summary>
public sealed record HumanSchedule(
    HumanFrequency Frequency,
    ValueList<TimeOnly> Times,          // Daily / Weekdays / SpecificDays / Monthly*: 1..10 times
    WeekDays Days,                      // SpecificDays; also limits EveryN* (All = every day)
    ValueList<int> MonthDates, bool LastDayOfMonth,   // MonthlyOnDates
    WeekOfMonth Week, DayOfWeek Weekday,              // MonthlyOnWeekday
    int Interval,                       // EveryNMinutes: 1,2,3,4,5,6,10,12,15,20,30 ; EveryNHours: 1,2,3,4,6,8,12
    int MinuteOffset,                   // EveryNHours: the minute past the hour (0..59)
    int? FromHour, int? ToHour)         // EveryN*: optional window of whole hours, inclusive (9..17)
{
    /// <summary>A schedule of the given frequency with every other field at its neutral default (no times; all days, or Mon-Fri for Weekdays).</summary>
    public static HumanSchedule Default(HumanFrequency frequency) => new(
        frequency, ValueList<TimeOnly>.Empty, frequency == HumanFrequency.Weekdays ? WeekDays.Weekdays : WeekDays.All,
        ValueList<int>.Empty, false,
        WeekOfMonth.First, DayOfWeek.Monday, 0, 0, null, null);
}
