using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class TaskComposerTests
{
    // [CP-A] The frozen fingerprint of the composed Stock job was removed: it coupled the composer to the wrapper text (every wrapper
    // edit churned it) while the projection FORMAT, which is what must never change silently (a format change would flag every
    // deployed task as Drifted), is already pinned by the frozen projection string in TaskXmlTests.

    static readonly string[] StockHead =
    [
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>",
        "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">",
        "  <RegistrationInfo>",
        "    <Author>Cadence</Author>",
        "    <Source>Cadence</Source>",
        "    <Description>Every weekday at 18:00, except NYSE market holidays. Managed by Cadence - edit it in the Cadence app.</Description>",
        "    <Documentation>cadence/1",
        "{JSON}</Documentation>",
        "  </RegistrationInfo>",
        "  <Triggers>",
        "    <CalendarTrigger>",
        "      <StartBoundary>2026-10-01T18:00:00</StartBoundary>",
        "      <Enabled>true</Enabled>",
        "      <ScheduleByWeek>",
        "        <DaysOfWeek>",
        "          <Monday />",
        "          <Tuesday />",
        "          <Wednesday />",
        "          <Thursday />",
        "          <Friday />",
        "        </DaysOfWeek>",
        "        <WeeksInterval>1</WeeksInterval>",
        "      </ScheduleByWeek>",
        "    </CalendarTrigger>",
        "  </Triggers>",
        "  <Principals>",
        "    <Principal id=\"Author\">",
        "      <UserId>TESTPC\\tester</UserId>",
        "      <LogonType>InteractiveToken</LogonType>",
        "      <RunLevel>LeastPrivilege</RunLevel>",
        "    </Principal>",
        "  </Principals>",
        "  <Settings>",
        "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>",
        "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
        "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
        "    <StartWhenAvailable>true</StartWhenAvailable>",
        "    <ExecutionTimeLimit>PT35M</ExecutionTimeLimit>",
        "    <Enabled>true</Enabled>",
        "  </Settings>",
    ];

    static string[] Lines(string xml) => xml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    static CadenceMetadata Decode(TaskDefinitionModel model)
    {
        T.Eq("metadata decodes", MetadataProblem.None, MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out var m));
        return m!;
    }

    public static void StockIngestGoldenXml()
    {
        var job = ComposeSamples.StockIngest();
        var ctx = ComposeSamples.Context();
        var model = TaskComposer.Compose(job, ctx);
        string[] lines = Lines(TaskComposer.ComposeXml(job, ctx));

        T.Eq("line count (47 incl. the empty tail line is not counted)", 47, lines.Length - (lines[^1].Length == 0 ? 1 : 0));
        for (int i = 0; i < StockHead.Length; i++)
        {
            string expected = StockHead[i];
            string actual = lines[i];
            if (expected == "{JSON}</Documentation>")
            {
                T.True("line " + (i + 1) + ": metadata JSON then </Documentation>", actual.EndsWith("</Documentation>", StringComparison.Ordinal));
                string json = actual[..^"</Documentation>".Length];
                T.Eq("embedded text equals Documentation", model.RegistrationInfo.Documentation, "cadence/1\n" + json);
            }
            else T.Eq("line " + (i + 1), expected, actual);
        }

        var action = WrapperScript.BuildAction(@"\Cadence\Stock ingest (Dad)", "Cadence", true);
        string escapedArgs = action.Arguments!.Replace("&", "&amp;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        T.Eq("Actions", "  <Actions Context=\"Author\">", lines[40]);
        T.Eq("Exec", "    <Exec>", lines[41]);
        T.Eq("Command", "      <Command>%SystemRoot%\\System32\\conhost.exe</Command>", lines[42]);
        T.Eq("Arguments", "      <Arguments>" + escapedArgs + "</Arguments>", lines[43]);
        T.Eq("/Exec", "    </Exec>", lines[44]);
        T.Eq("/Actions", "  </Actions>", lines[45]);
        T.Eq("/Task", "</Task>", lines[46]);
    }

    public static void StockIngestMetadataContent()
    {
        var model = TaskComposer.Compose(ComposeSamples.StockIngest(), ComposeSamples.Context());
        var m = Decode(model);
        T.Eq("schema", 1, m.Schema);
        T.Eq("gen", TaskComposer.GeneratorVersion, m.Gen);
        T.Eq("wv", WrapperScript.Version, m.Wv);
        T.Eq("fp equals the model's fingerprint", Fingerprint.Of(model), m.Fp);
        T.Eq("fp survives the XML round trip", m.Fp, Fingerprint.Of(TaskXmlReader.Read(TaskXmlWriter.Write(model))));
        T.Eq("created", ComposeSamples.Now, m.Created);
        T.Eq("updated", ComposeSamples.Now, m.Updated);
        T.Eq("created offset kept", TimeSpan.FromHours(-4), m.Created.Offset);
        T.True("job round trips", ComposeSamples.StockIngest().Equals(m.Job));
        T.Eq("prev empty", 0, m.Prev.Count);
        T.Eq("Author", "Cadence", model.RegistrationInfo.Author);
        T.Eq("Source", "Cadence", model.RegistrationInfo.Source);
        T.Eq("Description", "Every weekday at 18:00, except NYSE market holidays. Managed by Cadence - edit it in the Cadence app.", model.RegistrationInfo.Description);
        T.Eq("principal", new PrincipalModel(ComposeSamples.User, "InteractiveToken", "LeastPrivilege"), model.Principal);
        T.Eq("one trigger", 1, model.Triggers.Count);
        T.True("weekly Mon-Fri at 18:00 from the anchor", model.Triggers[0] is WeeklyTrigger { WeeksInterval: 1, Days: WeekDays.Weekdays } w && w.StartBoundary == new DateTime(2026, 10, 1, 18, 0, 0));
        T.Eq("one Exec action", 1, model.ExecActions.Count);
        T.Eq("no other actions", 0, model.OtherActionsXml.Count);
    }

    public static void HolidayListMatchesRules()
    {
        foreach (var cal in new[] { HolidayCalendar.Nyse, HolidayCalendar.UsFederal })
        {
            var model = TaskComposer.Compose(ComposeSamples.StockIngest(cal), ComposeSamples.Context());
            var skip = Decode(model).Skip;
            T.True(cal + ": run.skip present", skip is not null);
            if (skip is null) continue;
            T.Eq(cal + ": cal", cal, skip.Calendar);
            T.Eq(cal + ": until", new DateOnly(2036, 12, 31), skip.Until);
            var expected = HolidayRules.Range(cal, 2026, 2036);
            T.Eq(cal + ": count equals HolidayRules.Range(2026..2036)", expected.Count, skip.Dates.Count);
            T.True(cal + ": same dates and names, same order", expected.Equals(skip.Dates));
            T.True(cal + ": dates ascending and unique", skip.Dates.Zip(skip.Dates.Skip(1), (a, b) => a.Date < b.Date).All(x => x));
            T.True(cal + ": first date is in 2026 or Dec 2025... never before 2026-01-01", skip.Dates[0].Date >= new DateOnly(2026, 1, 1));
            T.True(cal + ": last date within until", skip.Dates[^1].Date <= skip.Until);
            // The shared 3.7 rows that fall inside the embedded window, looked up in the embedded list itself.
            foreach (var (date, name) in SharedRows(cal))
            {
                var found = skip.Dates.FirstOrDefault(h => h.Date == ComposeSamples.D(date));
                T.Eq(cal + ": " + date, name, found?.Name);
            }
        }
        var nyseSkip = Decode(TaskComposer.Compose(ComposeSamples.StockIngest(HolidayCalendar.Nyse), ComposeSamples.Context())).Skip!;
        T.Eq("Thanksgiving 2026 is listed", "Thanksgiving Day", nyseSkip.Dates.FirstOrDefault(h => h.Date == new DateOnly(2026, 11, 26))?.Name);
        T.True("NYSE does not list Columbus Day 2026-10-12", nyseSkip.Dates.All(h => h.Date != new DateOnly(2026, 10, 12)));
        var fedSkip = Decode(TaskComposer.Compose(ComposeSamples.StockIngest(HolidayCalendar.UsFederal), ComposeSamples.Context())).Skip!;
        T.Eq("federal lists Columbus Day 2026-10-12", "Columbus Day", fedSkip.Dates.FirstOrDefault(h => h.Date == new DateOnly(2026, 10, 12))?.Name);
        T.True("federal lists Veterans Day, NYSE does not", fedSkip.Dates.Any(h => h.Date == new DateOnly(2026, 11, 11)) && nyseSkip.Dates.All(h => h.Date != new DateOnly(2026, 11, 11)));
        T.Eq("window is the current year through +10 (2026-2036)", 2036, nyseSkip.Dates[^1].Date.Year);
    }

    // Rows of PLAN 3.7 inside 2026..2036 (the composer's window for now = 2026): date -> expected name in that calendar (null = open).
    static IEnumerable<(string Date, string? Name)> SharedRows(HolidayCalendar cal)
    {
        (string Date, string? Fed, string? Nyse)[] rows =
        [
            ("2026-01-01", "New Year's Day", "New Year's Day"),
            ("2026-01-19", "Martin Luther King Jr. Day", "Martin Luther King Jr. Day"),
            ("2026-02-16", "Washington's Birthday", "Washington's Birthday"),
            ("2026-04-03", null, "Good Friday"),
            ("2026-05-25", "Memorial Day", "Memorial Day"),
            ("2026-06-19", "Juneteenth", "Juneteenth"),
            ("2026-07-03", "Independence Day (observed)", "Independence Day (observed)"),
            ("2026-09-07", "Labor Day", "Labor Day"),
            ("2026-10-12", "Columbus Day", null),
            ("2026-11-11", "Veterans Day", null),
            ("2026-11-26", "Thanksgiving Day", "Thanksgiving Day"),
            ("2026-12-25", "Christmas Day", "Christmas Day"),
            ("2027-03-26", null, "Good Friday"),
            ("2027-06-18", "Juneteenth (observed)", "Juneteenth (observed)"),
            ("2027-07-05", "Independence Day (observed)", "Independence Day (observed)"),
            ("2027-12-24", "Christmas Day (observed)", "Christmas Day (observed)"),
            ("2027-12-31", "New Year's Day (observed)", null),
        ];
        foreach (var (d, f, n) in rows) yield return (d, cal == HolidayCalendar.Nyse ? n : f);
    }

    public static void NoSkipWhenNone()
    {
        var model = TaskComposer.Compose(ComposeSamples.StockIngest(HolidayCalendar.None), ComposeSamples.Context());
        T.True("no run.skip", Decode(model).Skip is null);
        T.True("no \"skip\" text in the Documentation", !model.RegistrationInfo.Documentation!.Contains("\"skip\"", StringComparison.Ordinal));
        T.Eq("Description without the holiday clause", "Every weekday at 18:00. Managed by Cadence - edit it in the Cadence app.", model.RegistrationInfo.Description);
    }

    public static void WindowFollowsNow()
    {
        var job = ComposeSamples.StockIngest();
        var later = new DateTimeOffset(2031, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5));
        var skip = Decode(TaskComposer.Compose(job, ComposeSamples.Context(now: later))).Skip!;
        T.Eq("until = Dec 31 of year + 10", new DateOnly(2041, 12, 31), skip.Until);
        T.True("same as HolidayRules.Range(2031..2041)", HolidayRules.Range(HolidayCalendar.Nyse, 2031, 2041).Equals(skip.Dates));
    }

    public static void TimeoutAndSettings()
    {
        var job = ComposeSamples.StockIngest();
        var ctx = ComposeSamples.Context();
        T.Eq("1800 s -> PT35M", TimeSpan.FromMinutes(35), TaskComposer.Compose(job, ctx).Settings.ExecutionTimeLimit);
        T.Eq("no timeout -> 72 h backstop", TimeSpan.FromHours(72), TaskComposer.Compose(job with { Run = job.Run with { Timeout = null } }, ctx).Settings.ExecutionTimeLimit);
        T.Eq("zero timeout -> 72 h backstop", TimeSpan.FromHours(72), TaskComposer.Compose(job with { Run = job.Run with { Timeout = TimeSpan.Zero } }, ctx).Settings.ExecutionTimeLimit);
        T.Eq("1 s -> 5 min 1 s", TimeSpan.FromSeconds(301), TaskComposer.Compose(job with { Run = job.Run with { Timeout = TimeSpan.FromSeconds(1) } }, ctx).Settings.ExecutionTimeLimit);
        T.Eq("1.5 s rounds up to 2 s", TimeSpan.FromSeconds(302), TaskComposer.Compose(job with { Run = job.Run with { Timeout = TimeSpan.FromMilliseconds(1500) } }, ctx).Settings.ExecutionTimeLimit);

        var s = TaskComposer.Compose(job, ctx).Settings;
        T.Eq("IgnoreNew", "IgnoreNew", s.MultipleInstancesPolicy);
        T.True("defaults: start when available, run on batteries, no wake, enabled", s.StartWhenAvailable && !s.DisallowStartIfOnBatteries && !s.StopIfGoingOnBatteries && !s.WakeToRun && s.Enabled);

        var custom = job with
        {
            Enabled = false,
            Settings = new JobSettings { StartWhenAvailable = false, RunOnBatteries = false, StopOnBatteries = true, WakeToRun = true },
        };
        var c = TaskComposer.Compose(custom, ctx).Settings;
        T.True("flags map through (inverted battery flag)", !c.Enabled && !c.StartWhenAvailable && c.DisallowStartIfOnBatteries && c.StopIfGoingOnBatteries && c.WakeToRun);
        T.True("Enabled=false is also in the metadata", !Decode(TaskComposer.Compose(custom, ctx)).Job.Enabled);
    }

    public static void HiddenAndVisibleActions()
    {
        var job = ComposeSamples.StockIngest();
        var ctx = ComposeSamples.Context();
        var hidden = TaskComposer.Compose(job, ctx).ExecActions[0];
        T.Eq("hidden action", WrapperScript.BuildAction(@"\Cadence\Stock ingest (Dad)", "Cadence", true), hidden);
        T.Eq("hidden command", @"%SystemRoot%\System32\conhost.exe", hidden.Command);
        T.True("hidden arguments start", hidden.Arguments!.StartsWith(@"--headless %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $ErrorActionPreference='Stop'; $tp='\Cadence\Stock ingest (Dad)'; $rr='Cadence';", StringComparison.Ordinal));

        var visibleJob = job with { Run = job.Run with { Hidden = false } };
        var visible = TaskComposer.Compose(visibleJob, ctx).ExecActions[0];
        T.Eq("visible action", WrapperScript.BuildAction(@"\Cadence\Stock ingest (Dad)", "Cadence", false), visible);
        T.Eq("visible command", @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe", visible.Command);
        T.True("visible arguments start", visible.Arguments!.StartsWith("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $ErrorActionPreference='Stop';", StringComparison.Ordinal));
        var vm = Decode(TaskComposer.Compose(visibleJob, ctx));
        T.True("visible job: hidden=false in metadata", !vm.Job.Run.Hidden);
        T.True("capture=false written for visible (v1 invariant capture == hidden)", TaskComposer.Compose(visibleJob, ctx).RegistrationInfo.Documentation!.Contains("\"hidden\":false,\"capture\":false", StringComparison.Ordinal));
    }

    public static void UserTextNeverReachesTheAction()
    {
        // The user's exe, args, cwd, env and name only exist in the metadata; the action must be identical for any of them.
        var ctx = ComposeSamples.Context();
        var baseline = TaskComposer.Compose(ComposeSamples.StockIngest(), ctx).ExecActions[0];
        var hostile = ComposeSamples.StockIngest() with
        {
            Run = new RunSpec
            {
                Exe = "C:\\%USERNAME%\\\"evil\".exe",
                Args = "%PATH% \"quoted\" & calc ; $(whoami) `x` \r\n 'a'",
                WorkingDirectory = "%TEMP%\\x y",
                Env = ValueList.Of(new KeyValuePair<string, string>("A", "%B% \"c\" $(d)")),
                Timeout = TimeSpan.FromSeconds(1800),
                Hidden = true,
            },
        };
        var model = TaskComposer.Compose(hostile, ctx);
        T.Eq("action unchanged by user text", baseline, model.ExecActions[0]);
        T.True("user text is not in Arguments", !model.ExecActions[0].Arguments!.Contains("evil", StringComparison.Ordinal) && !model.ExecActions[0].Arguments!.Contains("whoami", StringComparison.Ordinal));
        var m = Decode(model);
        T.Eq("exe round trips", hostile.Run.Exe, m.Job.Run.Exe);
        T.Eq("args round trip", hostile.Run.Args, m.Job.Run.Args);
        T.Eq("cwd round trips", hostile.Run.WorkingDirectory, m.Job.Run.WorkingDirectory);
        T.True("env round trips", hostile.Run.Env.Equals(m.Job.Run.Env));
    }

    public static void XmlRoundTripsToTheSameModel()
    {
        var job = ComposeSamples.StockIngest();
        var ctx = ComposeSamples.Context();
        var model = TaskComposer.Compose(job, ctx);
        string xml = TaskComposer.ComposeXml(job, ctx);
        var read = TaskXmlReader.Read(xml);
        T.True("Read(ComposeXml) equals Compose", model.Equals(read));
        T.Eq("fingerprint equal after the round trip", Fingerprint.Of(model), Fingerprint.Of(read));
        T.Eq("Documentation identical", model.RegistrationInfo.Documentation, read.RegistrationInfo.Documentation);
        T.Eq("Write(Read(xml)) is identical", xml, TaskXmlWriter.Write(read));
        T.Eq("fingerprint in the metadata is the fingerprint of the stored task", Decode(read).Fp, Fingerprint.Of(read));
    }

    public static void FingerprintIsStableAndSensitive()
    {
        var job = ComposeSamples.StockIngest();
        var ctx = ComposeSamples.Context();
        string fp = Fingerprint.Of(TaskComposer.Compose(job, ctx));
        T.Eq("composing twice gives the same XML", TaskComposer.ComposeXml(job, ctx), TaskComposer.ComposeXml(job, ctx));
        T.Eq("fp stable when only Now changes (metadata changes, task does not)", fp, Fingerprint.Of(TaskComposer.Compose(job, ComposeSamples.Context(now: ComposeSamples.Now.AddDays(40)))));
        T.Eq("fp stable when only Created / PreviousNames change", fp, Fingerprint.Of(TaskComposer.Compose(job, ComposeSamples.Context(created: ComposeSamples.Now.AddYears(-1), prev: ValueList.Of("Old")))));
        T.Eq("fp stable when only the user's command changes (it is in the metadata, not the action)", fp,
            Fingerprint.Of(TaskComposer.Compose(job with { Run = job.Run with { Args = "other", Exe = "x.exe" } }, ctx)));
        T.Eq("fp stable when the display name changes", fp, Fingerprint.Of(TaskComposer.Compose(job with { Name = "Renamed" }, ctx)));
        T.Eq("fp stable when only the holiday calendar changes (enforced by the wrapper, not by triggers)", fp,
            Fingerprint.Of(TaskComposer.Compose(ComposeSamples.StockIngest(HolidayCalendar.UsFederal), ctx)));
        T.True("fp changes with the cron", fp != Fingerprint.Of(TaskComposer.Compose(job with { Schedule = job.Schedule with { CronLines = ValueList.Of("30 18 * * 1-5") } }, ctx)));
        T.True("fp changes with the task name (it is in the wrapper)", fp != Fingerprint.Of(TaskComposer.Compose(job, ComposeSamples.Context("Stock ingest"))));
        T.True("fp changes with the timeout", fp != Fingerprint.Of(TaskComposer.Compose(job with { Run = job.Run with { Timeout = TimeSpan.FromMinutes(5) } }, ctx)));
        T.True("fp changes with hidden", fp != Fingerprint.Of(TaskComposer.Compose(job with { Run = job.Run with { Hidden = false } }, ctx)));
        T.True("fp changes with enabled", fp != Fingerprint.Of(TaskComposer.Compose(job with { Enabled = false }, ctx)));
        T.Eq("fp ignores the user id (Task Scheduler rewrites it to a SID)", fp, Fingerprint.Of(TaskComposer.Compose(job, ctx with { UserId = @"TESTPC\other" })));
    }

    public static void CreatedPreviousNamesAndWholeSeconds()
    {
        var job = ComposeSamples.StockIngest();
        var created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(1));
        var now = new DateTimeOffset(2026, 10, 1, 9, 0, 0, 987, TimeSpan.FromHours(-4)).AddTicks(1234);
        var m = Decode(TaskComposer.Compose(job, ComposeSamples.Context(now: now, created: created, prev: ValueList.Of("A", "B (2)"))));
        T.Eq("Created kept", created, m.Created);
        T.Eq("Updated is Now truncated to whole seconds", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4)), m.Updated);
        T.True("Prev kept in order", ValueList.Of("A", "B (2)").Equals(m.Prev));
        var fresh = Decode(TaskComposer.Compose(job, ComposeSamples.Context()));
        T.Eq("no Created -> Now", ComposeSamples.Now, fresh.Created);
    }

    public static void CronFailureThrowsWithDiagnostics()
    {
        var job = ComposeSamples.StockIngest();
        foreach (var (cron, label) in new[] { ("@reboot", "@reboot"), ("*/60 * * * *", "step too large"), ("nonsense", "garbage"), ("0 18 * * 1-5 2027", "year field") })
        {
            var bad = job with { Schedule = job.Schedule with { CronLines = ValueList.Of(cron) } };
            try
            {
                TaskComposer.Compose(bad, ComposeSamples.Context());
                T.True(label + ": throws", false);
            }
            catch (ComposeException ex)
            {
                T.True(label + ": has an Error diagnostic", ex.Diagnostics.Any(d => d.Severity == CronSeverity.Error));
                T.True(label + ": message carries the diagnostic text", ex.Diagnostics.Where(d => d.Severity == CronSeverity.Error).All(d => ex.Message.Contains(d.Message, StringComparison.Ordinal)));
            }
        }
        T.Throws<ComposeException>("no cron lines", () => TaskComposer.Compose(job with { Schedule = job.Schedule with { CronLines = ValueList<string>.Empty } }, ComposeSamples.Context()));
        T.Throws<ComposeException>("11 cron lines", () => TaskComposer.Compose(job with { Schedule = job.Schedule with { CronLines = new ValueList<string>(Enumerable.Repeat("0 1 * * *", 11)) } }, ComposeSamples.Context()));
    }

    public static void UnsafeNamesAndIdsThrow()
    {
        var job = ComposeSamples.StockIngest();
        T.Throws<ComposeException>("quote in task name", () => TaskComposer.Compose(job, ComposeSamples.Context("it's")));
        T.Throws<ComposeException>("percent in task name", () => TaskComposer.Compose(job, ComposeSamples.Context("100%")));
        T.Throws<ComposeException>("empty task name", () => TaskComposer.Compose(job, ComposeSamples.Context("")));
        T.Throws<ComposeException>("81-char task name", () => TaskComposer.Compose(job, ComposeSamples.Context(new string('a', 81))));
        T.Throws<ComposeException>("bad folder", () => TaskComposer.Compose(job, ComposeSamples.Context() with { FolderName = "Cad ence" }));
        T.Throws<ComposeException>("bad runs root", () => TaskComposer.Compose(job, ComposeSamples.Context() with { RunsRootName = "a'b" }));
        T.Throws<ComposeException>("bad job id", () => TaskComposer.Compose(job with { Id = "NOPE" }, ComposeSamples.Context()));
        var huge = job with { Run = job.Run with { Args = new string('x', 20000) } };
        var ex = Assert<ComposeException>(() => TaskComposer.Compose(huge, ComposeSamples.Context()));
        T.True("oversized metadata is a ComposeException mentioning the limit", ex is not null && ex.Message.Contains("limit", StringComparison.Ordinal));
        T.Eq("TaskPath", @"\Cadence\Stock ingest (Dad)", TaskComposer.TaskPath(ComposeSamples.Context()));
    }

    static TEx? Assert<TEx>(Action a) where TEx : Exception
    {
        try { a(); }
        catch (TEx ex) { return ex; }
        return null;
    }

    public static void EveryTemplateComposesAndRoundTrips()
    {
        foreach (var t in Templates.All)
        {
            var job = new JobDefinition
            {
                Id = "tmpl" + t.Id.Length.ToString("D4", CultureInfo.InvariantCulture),
                Name = t.SuggestedName,
                Schedule = new ScheduleSpec(ValueList.Of(t.Cron), t.SuggestedSkip, ComposeSamples.Anchor),
                Run = new RunSpec { Exe = @"C:\Windows\System32\notepad.exe", Args = "\"C:\\a b\\c.txt\"" },
            };
            var ctx = ComposeSamples.Context(TaskNaming.ToTaskName(t.SuggestedName, new HashSet<string>()));
            var model = TaskComposer.Compose(job, ctx);
            string xml = TaskComposer.ComposeXml(job, ctx);
            var read = TaskXmlReader.Read(xml);
            T.True(t.Id + ": XML parses back to the same model", model.Equals(read));
            T.Eq(t.Id + ": fingerprint after the round trip", Decode(model).Fp, Fingerprint.Of(read));
            T.True(t.Id + ": job round trips through the metadata", job.Equals(Decode(read).Job));
            T.Eq(t.Id + ": skip present iff the template skips holidays", t.SuggestedSkip != HolidayCalendar.None, Decode(read).Skip is not null);
            T.Eq(t.Id + ": Description starts with the English text", EnglishRenderer.Describe(job.Schedule) + ". Managed by Cadence - edit it in the Cadence app.", read.RegistrationInfo.Description);
        }
    }

    public static void AtLogonJobUsesTheContextUser()
    {
        var job = ComposeSamples.StockIngest(HolidayCalendar.None) with { Schedule = new ScheduleSpec(ValueList.Of("@logon"), HolidayCalendar.None, ComposeSamples.Anchor) };
        var model = TaskComposer.Compose(job, ComposeSamples.Context());
        T.True("one LogonTrigger for the context user", model.Triggers.Count == 1 && model.Triggers[0] is LogonTrigger { UserId: ComposeSamples.User });
        T.True("round trips through XML", model.Equals(TaskXmlReader.Read(TaskXmlWriter.Write(model))));
    }

    public static void MultiLineScheduleAndRenameHistory()
    {
        var job = ComposeSamples.StockIngest(HolidayCalendar.None) with
        {
            Schedule = new ScheduleSpec(ValueList.Of("0 7 * * 1-5", "30 9 * * 6,0"), HolidayCalendar.None, ComposeSamples.Anchor),
        };
        var model = TaskComposer.Compose(job, ComposeSamples.Context(prev: ValueList.Of("Old name")));
        T.True("at least two triggers", model.Triggers.Count >= 2);
        T.True("prev recorded", ValueList.Of("Old name").Equals(Decode(model).Prev));
        T.True("round trips through XML", model.Equals(TaskXmlReader.Read(TaskXmlWriter.Write(model))));
    }
}
