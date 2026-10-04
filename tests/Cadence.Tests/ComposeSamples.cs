using System.Globalization;
using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Model;

namespace Cadence.Tests;

/// <summary>Fixtures shared by the Phase 6 suites (not a test class: the name does not end in "Tests").</summary>
internal static class ComposeSamples
{
    public static readonly DateOnly Anchor = new(2026, 10, 1);
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));
    public const string User = @"TESTPC\tester";

    /// <summary>The "Stock ingest (Dad)" job of PLAN 3.9 / 3.11.</summary>
    public static JobDefinition StockIngest(HolidayCalendar skip = HolidayCalendar.Nyse) => new()
    {
        Id = "d4dst0ck",
        Name = "Stock ingest (Dad)",
        Schedule = new ScheduleSpec(ValueList.Of("0 18 * * 1-5"), skip, Anchor),
        Run = new RunSpec
        {
            Exe = @"C:\Windows\py.exe",
            Args = "-u \"C:\\Users\\Dad\\stocks\\ingest.py\"",
            WorkingDirectory = @"C:\Users\Dad\stocks",
            Env = ValueList.Of(new KeyValuePair<string, string>("PYTHONIOENCODING", "utf-8")),
            Timeout = TimeSpan.FromSeconds(1800),
            Hidden = true,
            Encoding = OutputEncoding.Utf8,
            SourcePath = @"C:\Users\Dad\stocks\ingest.py",
        },
    };

    public static ComposeContext Context(string taskName = "Stock ingest (Dad)", DateTimeOffset? now = null, DateTimeOffset? created = null,
        ValueList<string>? prev = null) =>
        new("Cadence", "Cadence", User, taskName, now ?? Now, created, prev ?? ValueList<string>.Empty);

    public static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
