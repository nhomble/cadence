using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class TemplatesTests
{
    static readonly DateOnly Anchor = new(2026, 9, 30);

    // id, title, subtitle, cron, English (with the template's own suggested skip)
    static readonly (string Id, string Title, string Subtitle, string Cron, string English)[] Expected =
    [
        ("good-morning", "Good morning", "Every weekday at 8:00 - open your apps, sync your notes", "0 8 * * 1-5", "Every weekday at 8:00"),
        ("end-of-day", "End of day", "Every weekday at 17:30 - back up, tidy up", "30 17 * * 1-5", "Every weekday at 17:30"),
        ("weekdays-6pm", "Weekdays at 6pm", "After the market close - e.g. ingest today's stock prices (tip: skip NYSE holidays)", "0 18 * * 1-5",
            "Every weekday at 18:00, except NYSE market holidays"),
        ("hourly", "On the hour", "Every hour, on the hour", "0 * * * *", "Every hour, on the hour"),
        ("every-15-minutes", "Every 15 minutes", "Every 15 minutes, all day", "*/15 * * * *", "Every 15 minutes"),
        ("nightly-2am", "Nightly at 2am", "Every day at 2:00 - e.g. download your YouTube feed", "0 2 * * *", "Every day at 2:00"),
        ("weekly-monday-9am", "Weekly, Monday 9am", "Every Monday at 9:00", "0 9 * * 1", "Every Monday at 9:00"),
        ("at-login", "At login", "When you sign in", "@logon", "When you sign in"),
        ("blank", "Start from scratch", "Pick your own time", "0 9 * * *", "Every day at 9:00"),
    ];

    public static void NineTemplatesInOrder()
    {
        T.Eq("count", 9, Templates.All.Count);
        for (int i = 0; i < Expected.Length; i++)
        {
            var (id, title, subtitle, cron, _) = Expected[i];
            var t = Templates.All[i];
            T.Eq(id + " id", id, t.Id);
            T.Eq(id + " title", title, t.Title);
            T.Eq(id + " subtitle", subtitle, t.Subtitle);
            T.Eq(id + " cron", cron, t.Cron);
        }
        T.Eq("ids are unique", 9, Templates.All.Select(t => t.Id).Distinct().Count());
    }

    public static void EachTemplateCompilesWithExpectedEnglish()
    {
        foreach (var (id, _, _, _, english) in Expected)
        {
            var t = Templates.All.Single(x => x.Id == id);
            var spec = new ScheduleSpec(ValueList.Of(t.Cron), t.SuggestedSkip, Anchor);
            var compiled = CronCompiler.Compile(spec, new MapContext(@"TESTPC\tester"));
            T.True(id + " compiles", compiled.Success && compiled.Triggers.Count == 1);
            T.Eq(id + " english", english, EnglishRenderer.Describe(spec));
            T.True(id + " has a suggested name", t.SuggestedName.Length > 0);
        }
    }

    public static void SuggestedSkipOnlyForTheMarketTemplate()
    {
        foreach (var t in Templates.All)
        {
            T.Eq(t.Id + " skip", t.Id == "weekdays-6pm" ? HolidayCalendar.Nyse : HolidayCalendar.None, t.SuggestedSkip);
        }
    }

    public static void SubtitlesAgreeWithTheRenderer()
    {
        // The card copy starts with the same sentence the editor will show (minus the holiday suffix and the tail after " - ").
        foreach (var t in Templates.All.Where(x => x.Id is "good-morning" or "end-of-day" or "nightly-2am"))
        {
            string english = EnglishRenderer.Describe(new ScheduleSpec(ValueList.Of(t.Cron), HolidayCalendar.None, Anchor));
            T.True(t.Id + " subtitle starts with the renderer sentence", t.Subtitle.StartsWith(english, StringComparison.Ordinal), t.Subtitle);
        }
    }

    public static void TemplatesUseNoUnicodeDashes()
    {
        foreach (var t in Templates.All)
        {
            T.True(t.Id + " ascii copy", (t.Title + t.Subtitle).All(c => c < 128));
        }
    }
}
