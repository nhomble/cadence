using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class TaskXmlTests
{
    // ===== samples ====================================================================================

    /// <summary>Captured from Task Scheduler on this machine (stored form).</summary>
    const string Normalized = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Source>Cadence</Source><Author>Cadence</Author><Description>Spike task. Managed by Cadence.</Description><Documentation>cadence:v1;{"id":"abc","ver":1}</Documentation><URI>\CadenceSpike\headless</URI></RegistrationInfo>
          <Principals><Principal id="Author"><UserId>S-1-5-21-1111111111-2222222222-3333333333-1001</UserId><LogonType>InteractiveToken</LogonType></Principal></Principals>
          <Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT5M</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><StartWhenAvailable>true</StartWhenAvailable><IdleSettings><StopOnIdleEnd>true</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine></Settings>
          <Triggers><TimeTrigger><StartBoundary>2026-09-30T22:34:22</StartBoundary><Repetition><Interval>PT1M</Interval><Duration>PT3M</Duration></Repetition></TimeTrigger></Triggers>
          <Actions Context="Author"><Exec><Command>%SystemRoot%\System32\conhost.exe</Command><Arguments>--headless cmd.exe /c exit 0</Arguments></Exec></Actions>
        </Task>
        """;

    /// <summary>What the same task looked like as authored: explicit defaults, different order, user name instead of SID.</summary>
    const string Authored = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>Someone Else</Author>
            <Source>Cadence</Source>
            <Description>A different description</Description>
            <Documentation>cadence:v1;{"id":"zzz","ver":9}</Documentation>
          </RegistrationInfo>
          <Triggers>
            <TimeTrigger>
              <StartBoundary>2026-09-30T22:34:22</StartBoundary>
              <Enabled>true</Enabled>
              <Repetition>
                <Interval>PT1M</Interval>
                <Duration>PT3M</Duration>
                <StopAtDurationEnd>false</StopAtDurationEnd>
              </Repetition>
            </TimeTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>TESTPC\tester</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <AllowHardTerminate>true</AllowHardTerminate>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <Hidden>false</Hidden>
            <Priority>7</Priority>
            <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>%SystemRoot%\System32\conhost.exe</Command>
              <Arguments>--headless cmd.exe /c exit 0</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    static string Doc(string triggers, string settings = "", string principal = "<UserId>TESTPC\\tester</UserId><LogonType>InteractiveToken</LogonType>", string actions = "<Exec><Command>c.exe</Command></Exec>") =>
        "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers>" + triggers + "</Triggers>" +
        "<Principals><Principal id=\"Author\">" + principal + "</Principal></Principals>" +
        "<Settings>" + settings + "</Settings><Actions Context=\"Author\">" + actions + "</Actions></Task>";

    static string Fp(string xml) => Fingerprint.Of(TaskXmlReader.Read(xml));

    static string Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    static readonly DateTime Start = new(2026, 10, 1, 18, 0, 0, DateTimeKind.Unspecified);

    static TaskDefinitionModel ModelOf(params TaskTrigger[] triggers) => new(
        new RegistrationInfoModel("Cadence", "Daily. Managed by Cadence.", "cadence/1\n{\"id\":\"x\"}", "Cadence", null, null),
        new ValueList<TaskTrigger>(triggers),
        new PrincipalModel(@"TESTPC\tester", "InteractiveToken", "LeastPrivilege"),
        new SettingsModel { StartWhenAvailable = true, DisallowStartIfOnBatteries = false, StopIfGoingOnBatteries = false, ExecutionTimeLimit = TimeSpan.FromMinutes(35) },
        ValueList.Of(new ExecActionModel(@"%SystemRoot%\System32\conhost.exe", "--headless cmd.exe /c exit 0", null)),
        ValueList<string>.Empty);

    // ===== IsoDuration ================================================================================

    public static void IsoDurationParse()
    {
        var rows = new (string Text, TimeSpan Expected)[]
        {
            ("PT1M", TimeSpan.FromMinutes(1)), ("PT90M", TimeSpan.FromMinutes(90)), ("PT1H30M", TimeSpan.FromMinutes(90)),
            ("P1D", TimeSpan.FromDays(1)), ("P1DT2H", TimeSpan.FromHours(26)), ("PT0S", TimeSpan.Zero),
            ("PT72H", TimeSpan.FromHours(72)), ("P3D", TimeSpan.FromDays(3)), ("PT45S", TimeSpan.FromSeconds(45)),
            ("P0D", TimeSpan.Zero), ("PT1H1M1S", new TimeSpan(1, 1, 1)), ("P2DT3H4M5S", new TimeSpan(2, 3, 4, 5)),
        };
        foreach (var (text, expected) in rows)
        {
            T.True("parse " + text, IsoDuration.TryParse(text, out var v) && v == expected);
        }
    }

    public static void IsoDurationRejects()
    {
        string[] bad = ["", "P", "PT", "P1DT", "1D", "PT5", "PT1.5S", "PT1,5S", "P1Y", "P1M", "P1W", "P1Y2M", "PT-5M", "-PT5M", "PT1M1H", "P1D1D", "PTM", "pt1m", "P1DT1D", "PT1H1H", "P9999999999D", "P999999999D"];
        foreach (string b in bad)
        {
            T.True("rejects '" + b + "'", !IsoDuration.TryParse(b, out _));
        }
        T.True("null rejected", !IsoDuration.TryParse(null, out _));
        T.Throws<FormatException>("Parse throws FormatException", () => IsoDuration.Parse("P1M"));
    }

    public static void IsoDurationFormat()
    {
        var rows = new (TimeSpan Value, string Expected)[]
        {
            (TimeSpan.FromMinutes(90), "PT1H30M"), (TimeSpan.FromDays(1), "P1D"), (TimeSpan.FromHours(26), "P1DT2H"),
            (TimeSpan.Zero, "PT0S"), (TimeSpan.FromMinutes(1), "PT1M"), (TimeSpan.FromHours(72), "P3D"),
            (TimeSpan.FromSeconds(3661), "PT1H1M1S"), (TimeSpan.FromSeconds(30), "PT30S"), (new TimeSpan(0, 8, 0, 0, 900), "PT8H"),
        };
        foreach (var (v, e) in rows) T.Eq("format " + e, e, IsoDuration.Format(v));
        T.Throws<ArgumentOutOfRangeException>("negative throws", () => IsoDuration.Format(TimeSpan.FromMinutes(-1)));
        for (int m = 0; m <= 3000; m += 7)
        {
            var ts = TimeSpan.FromMinutes(m);
            if (!IsoDuration.TryParse(IsoDuration.Format(ts), out var back) || back != ts) { T.True("format/parse round trip " + m, false); return; }
        }
        T.True("format/parse round trip over 0..3000 minutes", true);
    }

    // ===== fingerprint: the key property ==============================================================

    public static void AuthoredVersusNormalized()
    {
        string a = Fp(Authored), n = Fp(Normalized);
        T.Eq("authored and Task-Scheduler-normalized XML give the same fingerprint", a, n);
        T.True("fingerprint is 16 lowercase hex characters", n.Length == 16 && n.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
        T.Eq("projections are identical strings", Projection.Canonical(TaskXmlReader.Read(Authored)), Projection.Canonical(TaskXmlReader.Read(Normalized)));
    }

    public static void NinetyMinutesVersusOneHourThirty()
    {
        T.Eq("PT90M equals PT1H30M", Fp(Normalized.Replace("<Interval>PT1M</Interval><Duration>PT3M</Duration>", "<Interval>PT90M</Interval><Duration>PT3H</Duration>", StringComparison.Ordinal)),
            Fp(Normalized.Replace("<Interval>PT1M</Interval><Duration>PT3M</Duration>", "<Interval>PT1H30M</Interval><Duration>PT180M</Duration>", StringComparison.Ordinal)));
        T.True("90 minutes differs from 60 minutes", Fp(Normalized.Replace("<Interval>PT1M</Interval>", "<Interval>PT90M</Interval>", StringComparison.Ordinal)) != Fp(Normalized.Replace("<Interval>PT1M</Interval>", "<Interval>PT60M</Interval>", StringComparison.Ordinal)));
    }

    public static void FingerprintIgnoresRegistrationAndUser()
    {
        string n = Fp(Normalized);
        T.Eq("different Documentation/Author/Description", n, Fp(Normalized.Replace("cadence:v1;{\"id\":\"abc\",\"ver\":1}", "something else entirely", StringComparison.Ordinal)));
        T.Eq("different principal user", n, Fp(Normalized.Replace("S-1-5-21-1111111111-2222222222-3333333333-1001", @"OTHER\Person", StringComparison.Ordinal)));
        T.Eq("removed RegistrationInfo", n, Fp(Normalized.Replace(Normalized.Substring(Normalized.IndexOf("<RegistrationInfo>", StringComparison.Ordinal), Normalized.IndexOf("</RegistrationInfo>", StringComparison.Ordinal) + 19 - Normalized.IndexOf("<RegistrationInfo>", StringComparison.Ordinal)), "", StringComparison.Ordinal)));
        T.Eq("IdleSettings and UseUnifiedSchedulingEngine noise removed", n, Fp(Normalized.Replace("<IdleSettings><StopOnIdleEnd>true</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>", "", StringComparison.Ordinal)));
        T.Eq("Principal id and Actions Context ignored", n, Fp(Normalized.Replace("Principal id=\"Author\"", "Principal id=\"X\"", StringComparison.Ordinal).Replace("Context=\"Author\"", "Context=\"Y\"", StringComparison.Ordinal)));
    }

    public static void FingerprintDetectsRealEdits()
    {
        string n = Fp(Normalized);
        var edits = new (string Name, string From, string To)[]
        {
            ("trigger time changed", "2026-09-30T22:34:22", "2026-09-30T22:35:22"),
            ("trigger date changed", "2026-09-30T22:34:22", "2026-10-01T22:34:22"),
            ("command changed", "conhost.exe", "conhost2.exe"),
            ("arguments changed", "exit 0", "exit 1"),
            ("repetition interval changed", "<Interval>PT1M</Interval>", "<Interval>PT2M</Interval>"),
            ("repetition duration changed", "<Duration>PT3M</Duration>", "<Duration>PT4M</Duration>"),
            ("repetition removed", "<Repetition><Interval>PT1M</Interval><Duration>PT3M</Duration></Repetition>", ""),
            ("task disabled", "<StartWhenAvailable>true</StartWhenAvailable>", "<StartWhenAvailable>true</StartWhenAvailable><Enabled>false</Enabled>"),
            ("trigger disabled", "<TimeTrigger>", "<TimeTrigger><Enabled>false</Enabled>"),
            ("execution time limit changed", "PT5M</ExecutionTimeLimit>", "PT6M</ExecutionTimeLimit>"),
            ("execution time limit removed (72 h default)", "<ExecutionTimeLimit>PT5M</ExecutionTimeLimit>", ""),
            ("battery flag changed", "<DisallowStartIfOnBatteries>false", "<DisallowStartIfOnBatteries>true"),
            ("StartWhenAvailable changed", "<StartWhenAvailable>true", "<StartWhenAvailable>false"),
            ("multiple instances policy changed", "IgnoreNew", "Parallel"),
            ("wake to run", "<StartWhenAvailable>true</StartWhenAvailable>", "<StartWhenAvailable>true</StartWhenAvailable><WakeToRun>true</WakeToRun>"),
            ("run level", "<LogonType>InteractiveToken</LogonType>", "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>"),
            ("random delay added", "<Repetition>", "<RandomDelay>PT5M</RandomDelay><Repetition>"),
            ("end boundary added", "<Repetition>", "<EndBoundary>2027-01-01T00:00:00</EndBoundary><Repetition>"),
        };
        foreach (var (name, from, to) in edits)
        {
            string edited = Normalized.Replace(from, to, StringComparison.Ordinal);
            T.True("edit applied: " + name, edited != Normalized);
            T.True("detected: " + name, Fp(edited) != n);
        }
    }

    public static void FingerprintDetectsScheduleEdits()
    {
        string weekly(string days, int every = 1, string time = "09:00:00") =>
            Doc("<CalendarTrigger><StartBoundary>2026-10-01T" + time + "</StartBoundary><ScheduleByWeek><DaysOfWeek>" + days + "</DaysOfWeek><WeeksInterval>" + every + "</WeeksInterval></ScheduleByWeek></CalendarTrigger>");
        string baseFp = Fp(weekly("<Monday /><Friday />"));
        T.Eq("day order is irrelevant", baseFp, Fp(weekly("<Friday /><Monday />")));
        T.True("days changed", Fp(weekly("<Monday /><Thursday />")) != baseFp);
        T.True("weeks interval changed", Fp(weekly("<Monday /><Friday />", 2)) != baseFp);
        T.True("time changed", Fp(weekly("<Monday /><Friday />", 1, "09:01:00")) != baseFp);

        string monthly(string days, string months = "<January /><July />") =>
            Doc("<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByMonth><DaysOfMonth>" + days + "</DaysOfMonth><Months>" + months + "</Months></ScheduleByMonth></CalendarTrigger>");
        T.Eq("monthly day order is irrelevant", Fp(monthly("<Day>1</Day><Day>15</Day>")), Fp(monthly("<Day>15</Day><Day>1</Day>")));
        T.True("monthly days changed", Fp(monthly("<Day>1</Day><Day>15</Day>")) != Fp(monthly("<Day>1</Day><Day>16</Day>")));
        T.True("monthly Last added", Fp(monthly("<Day>1</Day>")) != Fp(monthly("<Day>1</Day><Day>Last</Day>")));
        T.True("monthly months changed", Fp(monthly("<Day>1</Day>")) != Fp(monthly("<Day>1</Day>", "<January />")));

        string dow(string weeks, string days = "<Monday />") =>
            Doc("<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByMonthDayOfWeek><Weeks>" + weeks + "</Weeks><DaysOfWeek>" + days + "</DaysOfWeek><Months><January /></Months></ScheduleByMonthDayOfWeek></CalendarTrigger>");
        T.True("monthly dow weeks changed", Fp(dow("<Week>1</Week>")) != Fp(dow("<Week>2</Week>")));
        T.True("monthly dow Last", Fp(dow("<Week>4</Week>")) != Fp(dow("<Week>Last</Week>")));
        T.Eq("monthly dow week order irrelevant", Fp(dow("<Week>1</Week><Week>Last</Week>")), Fp(dow("<Week>Last</Week><Week>1</Week>")));

        string daily(int n) => Doc("<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByDay><DaysInterval>" + n + "</DaysInterval></ScheduleByDay></CalendarTrigger>");
        T.True("daily interval changed", Fp(daily(1)) != Fp(daily(2)));
        T.Eq("absent DaysInterval means 1", Fp(daily(1)), Fp(Doc("<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByDay /></CalendarTrigger>")));
    }

    public static void OffsetStartBoundaryEquivalence()
    {
        var utc = DateTimeOffset.Parse("2026-10-20T02:30:00Z", CultureInfo.InvariantCulture);
        string z = Doc("<TimeTrigger><StartBoundary>2026-10-20T02:30:00Z</StartBoundary></TimeTrigger>");
        string stored = Doc("<TimeTrigger><StartBoundary>" + utc.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + "</StartBoundary></TimeTrigger>");
        string plain = Doc("<TimeTrigger><StartBoundary>" + utc.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "</StartBoundary></TimeTrigger>");
        T.Eq("Z form equals the stored local-with-offset form", Fp(z), Fp(stored));
        T.Eq("offset form equals the plain local wall clock", Fp(stored), Fp(plain));
        var t = TaskXmlReader.Read(z).Triggers[0];
        T.Eq("Z sets StartBoundaryOffset", (DateTimeOffset?)utc, t.StartBoundaryOffset);
        T.Eq("Z sets StartBoundary as local", utc.ToLocalTime().DateTime, t.StartBoundary);
        T.True("StartBoundary is Unspecified", t.StartBoundary.Kind == DateTimeKind.Unspecified);
        T.True("plain value has no offset", TaskXmlReader.Read(plain).Triggers[0].StartBoundaryOffset is null);
    }

    // ===== reader =====================================================================================

    public static void ReaderAppliesDefaults()
    {
        var m = TaskXmlReader.Read("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>a.exe</Command></Exec></Actions></Task>");
        var s = m.Settings;
        T.True("Enabled true", s.Enabled);
        T.True("DisallowStartIfOnBatteries true when absent", s.DisallowStartIfOnBatteries);
        T.True("StopIfGoingOnBatteries true when absent", s.StopIfGoingOnBatteries);
        T.Eq("ExecutionTimeLimit PT72H when absent", (TimeSpan?)TimeSpan.FromHours(72), s.ExecutionTimeLimit);
        T.Eq("MultipleInstancesPolicy IgnoreNew", "IgnoreNew", s.MultipleInstancesPolicy);
        T.True("StartWhenAvailable false", !s.StartWhenAvailable);
        T.Eq("Priority 7", 7, s.Priority);
        T.True("AllowStartOnDemand and AllowHardTerminate true", s.AllowStartOnDemand && s.AllowHardTerminate);
        T.Eq("principal defaults", new PrincipalModel(null, "InteractiveToken", "LeastPrivilege"), m.Principal);
        T.Eq("no triggers", 0, m.Triggers.Count);
        T.Eq("exec action", new ExecActionModel("a.exe", null, null), m.ExecActions[0]);
        T.Eq("registration all null", new RegistrationInfoModel(null, null, null, null, null, null), m.RegistrationInfo);
    }

    public static void ReaderSettings()
    {
        var unlimited = TaskXmlReader.Read(Doc("", "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>")).Settings;
        T.Eq("PT0S means unlimited (null)", (TimeSpan?)null, unlimited.ExecutionTimeLimit);

        var s = TaskXmlReader.Read(Doc("", "<Hidden>true</Hidden><WakeToRun>true</WakeToRun><RunOnlyIfIdle>true</RunOnlyIfIdle><Priority>4</Priority><DeleteExpiredTaskAfter>P1D</DeleteExpiredTaskAfter><RestartOnFailure><Interval>PT5M</Interval><Count>3</Count></RestartOnFailure><AllowStartOnDemand>false</AllowStartOnDemand>")).Settings;
        T.True("Hidden, WakeToRun, RunOnlyIfIdle", s.Hidden && s.WakeToRun && s.RunOnlyIfIdle);
        T.Eq("Priority", 4, s.Priority);
        T.Eq("DeleteExpiredTaskAfter", (TimeSpan?)TimeSpan.FromDays(1), s.DeleteExpiredTaskAfter);
        T.Eq("RestartCount", (int?)3, s.RestartCount);
        T.Eq("RestartInterval", (TimeSpan?)TimeSpan.FromMinutes(5), s.RestartInterval);
        T.True("AllowStartOnDemand false", !s.AllowStartOnDemand);

        var other = TaskXmlReader.Read(Doc("", "<NetworkSettings><Name>Home</Name><Id>x</Id></NetworkSettings><DisallowStartOnRemoteAppSession>true</DisallowStartOnRemoteAppSession>")).Settings;
        T.Eq("unknown Settings children preserved (count)", 2, other.OtherElements.Count);
        T.True("preserved sorted", string.CompareOrdinal(other.OtherElements[0], other.OtherElements[1]) < 0);
        T.True("preserved as canonical outer XML", other.OtherElements.Contains("<DisallowStartOnRemoteAppSession>true</DisallowStartOnRemoteAppSession>"));

        var nonDefaultIdle = TaskXmlReader.Read(Doc("", "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>")).Settings;
        T.Eq("non-default IdleSettings is preserved", 1, nonDefaultIdle.OtherElements.Count);
        var idleDur = TaskXmlReader.Read(Doc("", "<IdleSettings><Duration>PT10M</Duration><WaitTimeout>PT1H</WaitTimeout><StopOnIdleEnd>true</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>")).Settings;
        T.Eq("default IdleSettings with Duration/WaitTimeout is ignored", 0, idleDur.OtherElements.Count);
        var idleReal = TaskXmlReader.Read(Doc("", "<RunOnlyIfIdle>true</RunOnlyIfIdle><IdleSettings><Duration>PT10M</Duration><StopOnIdleEnd>true</StopOnIdleEnd></IdleSettings>")).Settings;
        T.Eq("IdleSettings kept when RunOnlyIfIdle is true", 1, idleReal.OtherElements.Count);
    }

    public static void ReaderTriggers()
    {
        var m = TaskXmlReader.Read(Doc(
            "<TimeTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><EndBoundary>2027-01-01T00:00:00</EndBoundary><Enabled>false</Enabled><Repetition><Interval>PT1H</Interval></Repetition><ExecutionTimeLimit>PT10M</ExecutionTimeLimit><Id>t1</Id><RandomDelay>PT5M</RandomDelay></TimeTrigger>" +
            "<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByWeek><DaysOfWeek><Monday /><Sunday /></DaysOfWeek><WeeksInterval>2</WeeksInterval></ScheduleByWeek></CalendarTrigger>" +
            "<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByMonth><DaysOfMonth><Day>1</Day><Day>Last</Day></DaysOfMonth><Months><March /><December /></Months></ScheduleByMonth></CalendarTrigger>" +
            "<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByMonthDayOfWeek><Weeks><Week>2</Week><Week>Last</Week></Weeks><DaysOfWeek><Friday /></DaysOfWeek><Months><June /></Months></ScheduleByMonthDayOfWeek></CalendarTrigger>" +
            "<LogonTrigger><UserId>TESTPC\\tester</UserId><Delay>PT30S</Delay></LogonTrigger>" +
            "<BootTrigger><Delay>PT1M</Delay></BootTrigger>" +
            "<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary></CalendarTrigger>"));
        T.Eq("trigger count", 7, m.Triggers.Count);
        var t = (TimeTrigger)m.Triggers[0];
        T.Eq("time start", new DateTime(2026, 10, 1, 9, 0, 0), t.StartBoundary);
        T.Eq("time end", (DateTime?)new DateTime(2027, 1, 1), t.EndBoundary);
        T.True("time disabled", !t.Enabled);
        T.Eq("time repetition indefinite, stop false", new Repetition(TimeSpan.FromHours(1), null, false), t.Repetition);
        T.Eq("time ETL", (TimeSpan?)TimeSpan.FromMinutes(10), t.ExecutionTimeLimit);
        T.Eq("time random delay", (TimeSpan?)TimeSpan.FromMinutes(5), t.RandomDelay);
        T.Eq("time id", "t1", t.Id);
        var w = (WeeklyTrigger)m.Triggers[1];
        T.Eq("weekly days", WeekDays.Monday | WeekDays.Sunday, w.Days);
        T.Eq("weekly interval", 2, w.WeeksInterval);
        var mo = (MonthlyTrigger)m.Triggers[2];
        T.Eq("monthly days", ValueList.Of(1), mo.Days);
        T.True("monthly Last", mo.LastDay);
        T.Eq("monthly months", MonthSet.March | MonthSet.December, mo.Months);
        var md = (MonthlyDowTrigger)m.Triggers[3];
        T.Eq("dow weeks", ValueList.Of(WeekOfMonth.Second, WeekOfMonth.Last), md.Weeks);
        T.Eq("dow days", WeekDays.Friday, md.Days);
        var lg = (LogonTrigger)m.Triggers[4];
        T.Eq("logon user", @"TESTPC\tester", lg.UserId);
        T.Eq("logon delay", (TimeSpan?)TimeSpan.FromSeconds(30), lg.Delay);
        T.Eq("logon StartBoundary stays default", default(DateTime), lg.StartBoundary);
        var boot = (UnsupportedTrigger)m.Triggers[5];
        T.Eq("boot element name", "BootTrigger", boot.ElementName);
        T.Eq("boot raw xml is canonical and namespace free", "<BootTrigger><Delay>PT1M</Delay></BootTrigger>", boot.RawXml);
        T.True("calendar trigger without schedule is unsupported", m.Triggers[6] is UnsupportedTrigger { ElementName: "CalendarTrigger" });
    }

    public static void ReaderActions()
    {
        var m = TaskXmlReader.Read(Doc("", "", actions:
            "<Exec><Command>a.exe</Command><Arguments>-x \"y\"</Arguments><WorkingDirectory>C:\\w</WorkingDirectory></Exec><Exec><Command>b.exe</Command><Arguments></Arguments></Exec><SendEmail><Subject>Hi</Subject><To>a@b.c</To></SendEmail><ShowMessage><Title>t</Title><Body>b</Body></ShowMessage>"));
        T.Eq("two exec actions", 2, m.ExecActions.Count);
        T.Eq("first exec", new ExecActionModel("a.exe", "-x \"y\"", @"C:\w"), m.ExecActions[0]);
        T.Eq("empty arguments is null", new ExecActionModel("b.exe", null, null), m.ExecActions[1]);
        T.Eq("non-Exec actions preserved in order", 2, m.OtherActionsXml.Count);
        T.Eq("SendEmail canonical (children sorted)", "<SendEmail><Subject>Hi</Subject><To>a@b.c</To></SendEmail>", m.OtherActionsXml[0]);
    }

    public static void ReaderErrors()
    {
        T.Throws<TaskXmlException>("not xml", () => TaskXmlReader.Read("this is not xml"));
        T.Throws<TaskXmlException>("empty", () => TaskXmlReader.Read(""));
        T.Throws<TaskXmlException>("wrong root", () => TaskXmlReader.Read("<Foo xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\" />"));
        T.Throws<TaskXmlException>("no namespace", () => TaskXmlReader.Read("<Task />"));
        T.Throws<TaskXmlException>("month unit rejected", () => TaskXmlReader.Read(Doc("<TimeTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><Repetition><Interval>P1M</Interval></Repetition></TimeTrigger>")));
        T.Throws<TaskXmlException>("fractional seconds rejected", () => TaskXmlReader.Read(Doc("", "<ExecutionTimeLimit>PT1.5S</ExecutionTimeLimit>")));
        T.Throws<TaskXmlException>("bad boolean", () => TaskXmlReader.Read(Doc("", "<Enabled>maybe</Enabled>")));
        T.Throws<TaskXmlException>("bad day name", () => TaskXmlReader.Read(Doc("<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByWeek><DaysOfWeek><Funday /></DaysOfWeek></ScheduleByWeek></CalendarTrigger>")));
        T.Throws<TaskXmlException>("day 32", () => TaskXmlReader.Read(Doc("<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByMonth><DaysOfMonth><Day>32</Day></DaysOfMonth></ScheduleByMonth></CalendarTrigger>")));
        T.Throws<TaskXmlException>("week 5", () => TaskXmlReader.Read(Doc("<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByMonthDayOfWeek><Weeks><Week>5</Week></Weeks></ScheduleByMonthDayOfWeek></CalendarTrigger>")));
        T.Throws<TaskXmlException>("bad start boundary", () => TaskXmlReader.Read(Doc("<TimeTrigger><StartBoundary>yesterday</StartBoundary></TimeTrigger>")));
        T.Throws<TaskXmlException>("Exec without command", () => TaskXmlReader.Read(Doc("", "", actions: "<Exec><Arguments>x</Arguments></Exec>")));
        T.Throws<TaskXmlException>("weeks interval 0", () => TaskXmlReader.Read(Doc("<CalendarTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><ScheduleByWeek><WeeksInterval>0</WeeksInterval></ScheduleByWeek></CalendarTrigger>")));
        try { TaskXmlReader.Read("<a"); }
        catch (TaskXmlException ex) { T.True("message is plain text", ex.Message.Length > 10 && !ex.Message.Contains("   at ", StringComparison.Ordinal)); }
    }

    // ===== writer: goldens ============================================================================

    static string TriggersSection(TaskTrigger t)
    {
        string xml = Lf(TaskXmlWriter.Write(ModelOf(t)));
        int a = xml.IndexOf("  <Triggers>", StringComparison.Ordinal), b = xml.IndexOf("  </Triggers>", StringComparison.Ordinal) + "  </Triggers>".Length;
        return xml[a..b];
    }

    static void Golden(string name, TaskTrigger t, string expected) => T.Eq("golden " + name, Lf(expected).TrimStart('\n'), TriggersSection(t));

    public static void WriterGoldenTriggers()
    {
        Golden("TimeTrigger", new TimeTrigger { StartBoundary = Start, Repetition = new Repetition(TimeSpan.FromMinutes(90), TimeSpan.FromHours(8), true), RandomDelay = TimeSpan.FromMinutes(10) }, """
              <Triggers>
                <TimeTrigger>
                  <StartBoundary>2026-10-01T18:00:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <Repetition>
                    <Interval>PT1H30M</Interval>
                    <Duration>PT8H</Duration>
                    <StopAtDurationEnd>true</StopAtDurationEnd>
                  </Repetition>
                  <RandomDelay>PT10M</RandomDelay>
                </TimeTrigger>
              </Triggers>
            """);
        Golden("DailyTrigger", new DailyTrigger(3) { StartBoundary = Start, EndBoundary = new DateTime(2027, 1, 1), Enabled = false, ExecutionTimeLimit = TimeSpan.FromHours(1), Id = "d1" }, """
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-10-01T18:00:00</StartBoundary>
                  <EndBoundary>2027-01-01T00:00:00</EndBoundary>
                  <Enabled>false</Enabled>
                  <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
                  <Id>d1</Id>
                  <ScheduleByDay>
                    <DaysInterval>3</DaysInterval>
                  </ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
            """);
        Golden("WeeklyTrigger", new WeeklyTrigger(1, WeekDays.Weekdays) { StartBoundary = Start }, """
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-10-01T18:00:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByWeek>
                    <DaysOfWeek>
                      <Monday />
                      <Tuesday />
                      <Wednesday />
                      <Thursday />
                      <Friday />
                    </DaysOfWeek>
                    <WeeksInterval>1</WeeksInterval>
                  </ScheduleByWeek>
                </CalendarTrigger>
              </Triggers>
            """);
        Golden("MonthlyTrigger", new MonthlyTrigger(ValueList.Of(1, 15), true, MonthSet.January | MonthSet.July) { StartBoundary = Start }, """
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-10-01T18:00:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByMonth>
                    <DaysOfMonth>
                      <Day>1</Day>
                      <Day>15</Day>
                      <Day>Last</Day>
                    </DaysOfMonth>
                    <Months>
                      <January />
                      <July />
                    </Months>
                  </ScheduleByMonth>
                </CalendarTrigger>
              </Triggers>
            """);
        Golden("MonthlyDowTrigger", new MonthlyDowTrigger(ValueList.Of(WeekOfMonth.Second, WeekOfMonth.Last), WeekDays.Friday | WeekDays.Monday, MonthSet.All) { StartBoundary = Start }, """
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-10-01T18:00:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByMonthDayOfWeek>
                    <Weeks>
                      <Week>2</Week>
                      <Week>Last</Week>
                    </Weeks>
                    <DaysOfWeek>
                      <Monday />
                      <Friday />
                    </DaysOfWeek>
                    <Months>
                      <January />
                      <February />
                      <March />
                      <April />
                      <May />
                      <June />
                      <July />
                      <August />
                      <September />
                      <October />
                      <November />
                      <December />
                    </Months>
                  </ScheduleByMonthDayOfWeek>
                </CalendarTrigger>
              </Triggers>
            """);
        Golden("LogonTrigger (no StartBoundary)", new LogonTrigger(@"TESTPC\tester", TimeSpan.FromSeconds(30)), """
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>TESTPC\tester</UserId>
                  <Delay>PT30S</Delay>
                </LogonTrigger>
              </Triggers>
            """);
        Golden("UnsupportedTrigger", new UnsupportedTrigger("BootTrigger", "<BootTrigger><Delay>PT1M</Delay></BootTrigger>"), """
              <Triggers>
                <BootTrigger>
                  <Delay>PT1M</Delay>
                </BootTrigger>
              </Triggers>
            """);
    }

    public static void WriterGoldenDocument()
    {
        string xml = Lf(TaskXmlWriter.Write(ModelOf(new DailyTrigger(1) { StartBoundary = Start })));
        string expected = Lf("""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Cadence</Author>
                <Source>Cadence</Source>
                <Description>Daily. Managed by Cadence.</Description>
                <Documentation>cadence/1
            {"id":"x"}</Documentation>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-10-01T18:00:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay>
                    <DaysInterval>1</DaysInterval>
                  </ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>TESTPC\tester</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT35M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>%SystemRoot%\System32\conhost.exe</Command>
                  <Arguments>--headless cmd.exe /c exit 0</Arguments>
                </Exec>
              </Actions>
            </Task>
            """);
        T.Eq("golden full document", expected, xml);
        T.True("declares UTF-16 with a CRLF after it", TaskXmlWriter.Write(ModelOf()).StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task version=\"1.4\"", StringComparison.Ordinal));
        T.True("no StartBoundary written for a default LogonTrigger", !TaskXmlWriter.Write(ModelOf(new LogonTrigger(null, null))).Contains("StartBoundary", StringComparison.Ordinal));
        T.True("no Triggers element for an empty list", !TaskXmlWriter.Write(ModelOf()).Contains("<Triggers", StringComparison.Ordinal));
        T.Eq("model with no triggers reads back with none", 0, TaskXmlReader.Read(TaskXmlWriter.Write(ModelOf())).Triggers.Count);
        T.Eq("unlimited ExecutionTimeLimit is written as PT0S", true, TaskXmlWriter.Write(ModelOf() with { Settings = new SettingsModel { ExecutionTimeLimit = null } }).Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.Ordinal));
    }

    // ===== round trips ================================================================================

    static void RoundTrip(string name, TaskDefinitionModel m)
    {
        string xml = TaskXmlWriter.Write(m);
        TaskDefinitionModel back;
        try { back = TaskXmlReader.Read(xml); }
        catch (TaskXmlException ex) { T.True("round trip reads " + name, false, ex.Message); return; }
        T.True("projection round trip " + name, Projection.Canonical(back) == Projection.Canonical(m));
        T.True("fingerprint round trip " + name, Fingerprint.Of(back) == Fingerprint.Of(m));
        T.True("model round trip " + name, back.Equals(m), "models differ");
        T.True("write is stable " + name, TaskXmlWriter.Write(back) == xml);
    }

    public static void RoundTripTemplates()
    {
        var ctx = new MapContext(@"TESTPC\tester");
        foreach (var t in Templates.All)
        {
            var c = CronCompiler.Compile(new ScheduleSpec(new ValueList<string>(t.Cron.Split('\n')), t.SuggestedSkip, new DateOnly(2026, 10, 1)), ctx);
            if (!c.Success) { T.True("template compiles " + t.Id, false); continue; }
            RoundTrip("template " + t.Id, ModelOf(c.Triggers.ToArray()));
        }
    }

    public static void RoundTripCron36Rows()
    {
        string[][] rows =
        [
            ["0 8 * * 1-5"], ["30 17 * * MON-FRI"], ["0 * * * *"], ["*/15 * * * *"], ["* * * * *"], ["0 9-17 * * 1-5"], ["1-59/2 * * * *"],
            ["*/7 * * * *"], ["5,20,40 */2 * * *"], ["0-58 * * * 1"], ["0 0 L * *"], ["0 17 * * 5L"], ["0 9 13 * 5"], ["0 0 * * 0,7"],
            ["0 0 29 2 *"], ["0 9 * 1,7 *"], ["0 7 * * *", "30 19 * * *"], ["@logon"], ["0 0 0 * * *"], ["0 9 * * 1#2"], ["0 9 1,15 * *"],
            ["0 9 * 1-3 1-5"], ["0 6 * * 1", "0 18 * * 5", "@logon"], ["0 */4 * * 6,0"], ["30 8 1 */3 *"],
        ];
        var ctx = new MapContext(@"TESTPC\tester");
        int ok = 0;
        foreach (var lines in rows)
        {
            var c = CronCompiler.Compile(new ScheduleSpec(new ValueList<string>(lines), HolidayCalendar.None, new DateOnly(2026, 10, 1)), ctx);
            if (!c.Success) { T.True("compiles " + string.Join(" | ", lines), false); continue; }
            ok++;
            RoundTrip(string.Join(" | ", lines), ModelOf(c.Triggers.ToArray()));
        }
        T.True("every sample row compiled", ok == rows.Length);
    }

    public static void RoundTripForeignShapes()
    {
        RoundTrip("all trigger fields", ModelOf(
            new TimeTrigger { StartBoundary = Start, EndBoundary = Start.AddDays(30), Enabled = false, Repetition = new Repetition(TimeSpan.FromMinutes(15), null, false), ExecutionTimeLimit = TimeSpan.FromMinutes(5), RandomDelay = TimeSpan.FromSeconds(30), Id = "x" },
            new WeeklyTrigger(2, WeekDays.Weekend) { StartBoundary = Start, Repetition = new Repetition(TimeSpan.FromHours(1), TimeSpan.FromHours(8), true) },
            new LogonTrigger(null, null),
            new UnsupportedTrigger("EventTrigger", "<EventTrigger><Subscription>x</Subscription></EventTrigger>")));
        var all = ModelOf(new TimeTrigger { StartBoundary = Start });
        RoundTrip("every setting non-default", all with
        {
            Principal = new PrincipalModel(null, "S4U", "HighestAvailable"),
            Settings = new SettingsModel
            {
                Enabled = false, Hidden = true, MultipleInstancesPolicy = "Parallel", StartWhenAvailable = true, DisallowStartIfOnBatteries = false,
                StopIfGoingOnBatteries = false, ExecutionTimeLimit = null, WakeToRun = true, AllowStartOnDemand = false, AllowHardTerminate = false,
                RunOnlyIfNetworkAvailable = true, RunOnlyIfIdle = true, Priority = 3, DeleteExpiredTaskAfter = TimeSpan.FromDays(7),
                RestartCount = 3, RestartInterval = TimeSpan.FromMinutes(2),
                OtherElements = ValueList.Of("<IdleSettings><Duration>PT10M</Duration><StopOnIdleEnd>true</StopOnIdleEnd></IdleSettings>", "<NetworkSettings><Name>Home</Name></NetworkSettings>"),
            },
            ExecActions = ValueList.Of(new ExecActionModel("a.exe", null, null), new ExecActionModel("b.exe", "x", @"C:\work")),
            OtherActionsXml = ValueList.Of("<ShowMessage><Body>b</Body><Title>t</Title></ShowMessage>"),
        });
        RoundTrip("special characters in text", all with
        {
            RegistrationInfo = new RegistrationInfoModel("A & B <x>", "d \"q\" 'a'", "line1\nline2\n{\"k\":\"<&>\"}", "Cadence", @"\Cadence\Job & Co", null),
            ExecActions = ValueList.Of(new ExecActionModel("c:\\p q\\x.exe", "-Command \"$a='<&>'; %USERNAME%\" caf\u00e9 \u00fc", null)),
        });
        var doc = TaskXmlReader.Read(TaskXmlWriter.Write(all with { RegistrationInfo = all.RegistrationInfo with { Documentation = "cadence/1\r\nsecond\rthird\n" } }));
        T.Eq("Documentation keeps CR, LF and CRLF through the writer", "cadence/1\r\nsecond\rthird\n", doc.RegistrationInfo.Documentation);

        // XML as authored with a Z offset, a Boot trigger and extras survives write/read unchanged.
        var foreign = TaskXmlReader.Read(Doc("<TimeTrigger><StartBoundary>2026-10-20T02:30:00Z</StartBoundary></TimeTrigger><BootTrigger><Delay>PT5M</Delay></BootTrigger>", "<NetworkSettings><Name>Home</Name></NetworkSettings>"));
        RoundTrip("foreign xml with offset", foreign);
        T.True("offset survives write/read", TaskXmlReader.Read(TaskXmlWriter.Write(foreign)).Triggers[0].StartBoundaryOffset is not null);
    }

    public static void ReadingTheWritersOwnOutputIsSchemaOrderIndependent()
    {
        // Same task, wildly different element order in every container: same fingerprint.
        string a = Doc("<TimeTrigger><StartBoundary>2026-10-01T09:00:00</StartBoundary><Repetition><Interval>PT1H</Interval><Duration>PT8H</Duration></Repetition></TimeTrigger>",
            "<StartWhenAvailable>true</StartWhenAvailable><Priority>5</Priority><Hidden>true</Hidden>");
        string b = Doc("<TimeTrigger><Repetition><Duration>PT8H</Duration><Interval>PT1H</Interval></Repetition><StartBoundary>2026-10-01T09:00:00</StartBoundary></TimeTrigger>",
            "<Hidden>true</Hidden><Priority>5</Priority><StartWhenAvailable>true</StartWhenAvailable>");
        T.Eq("element order does not matter", Fp(a), Fp(b));
        string x = Doc("", "<NetworkSettings><Id>1</Id><Name>n</Name></NetworkSettings>");
        string y = Doc("", "<NetworkSettings><Name>n</Name><Id>1</Id></NetworkSettings>");
        T.Eq("children of preserved elements are sorted", Fp(x), Fp(y));
    }

    public static void FingerprintGolden()
    {
        // Freezes the projection format: if this changes, every stored Cadence task would look drifted.
        var m = TaskXmlReader.Read(Normalized);
        string json = Projection.Canonical(m);
        T.Eq("projection of the captured task", """{"triggers":[{"type":"Time","start":"2026-09-30T22:34:22","end":null,"enabled":true,"repetition":{"interval":"PT1M","duration":"PT3M","stopAtDurationEnd":false},"randomDelay":null,"executionTimeLimit":null}],"principal":{"logonType":"InteractiveToken","runLevel":"LeastPrivilege"},"settings":{"enabled":true,"hidden":false,"multipleInstances":"IgnoreNew","startWhenAvailable":true,"disallowStartIfOnBatteries":false,"stopIfGoingOnBatteries":false,"executionTimeLimit":"PT5M","wakeToRun":false,"allowStartOnDemand":true,"allowHardTerminate":true,"runOnlyIfNetworkAvailable":false,"runOnlyIfIdle":false,"priority":7,"deleteExpiredTaskAfter":null,"restartCount":null,"restartInterval":null,"other":[]},"actions":[{"command":"%SystemRoot%\\System32\\conhost.exe","arguments":"--headless cmd.exe /c exit 0","workingDirectory":null}],"otherActions":[]}""", json);
        T.True("fingerprint changes when the projection does", Fingerprint.Of(m) != Fingerprint.Of(m with { ExecActions = ValueList.Of(new ExecActionModel("x", null, null)) }));
        T.Eq("fingerprint is deterministic", Fingerprint.Of(m), Fingerprint.Of(TaskXmlReader.Read(Normalized)));
    }

    // [CP-A] The golden above only covers a TimeTrigger. Cadence writes calendar and logon triggers, so their projection format is
    // frozen too (hand-built model, independent of the wrapper text and the composer).
    public static void FingerprintGoldenCalendarTriggers()
    {
        var start = new DateTime(2026, 10, 1, 18, 0, 0);
        var m = new TaskDefinitionModel(
            new RegistrationInfoModel("Cadence", "d", "doc", "Cadence", null, null),
            ValueList.Of<TaskTrigger>(
                new DailyTrigger(1) { StartBoundary = start },
                new WeeklyTrigger(1, WeekDays.Weekdays) { StartBoundary = start },
                new MonthlyTrigger(ValueList.Of(15, 1), true, MonthSet.January | MonthSet.July)
                    { StartBoundary = start, Repetition = new Repetition(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), false) },
                new MonthlyDowTrigger(ValueList.Of(WeekOfMonth.Last, WeekOfMonth.First), WeekDays.Monday | WeekDays.Friday, MonthSet.All) { StartBoundary = start },
                new LogonTrigger(@"TESTPC\tester", null)),
            new PrincipalModel(@"TESTPC\tester", "InteractiveToken", "LeastPrivilege"),
            new SettingsModel { StartWhenAvailable = true, DisallowStartIfOnBatteries = false, StopIfGoingOnBatteries = false, ExecutionTimeLimit = TimeSpan.FromMinutes(35) },
            ValueList.Of(new ExecActionModel("cmd.exe", "/d /c echo hi", @"C:\w")),
            ValueList<string>.Empty);
        T.Eq("projection of calendar and logon triggers", CalendarGolden, Projection.Canonical(m));
    }

    const string CalendarGolden = """{"triggers":[{"type":"Daily","start":"2026-10-01T18:00:00","end":null,"enabled":true,"repetition":null,"randomDelay":null,"executionTimeLimit":null,"daysInterval":1},{"type":"Weekly","start":"2026-10-01T18:00:00","end":null,"enabled":true,"repetition":null,"randomDelay":null,"executionTimeLimit":null,"weeksInterval":1,"days":["Monday","Tuesday","Wednesday","Thursday","Friday"]},{"type":"Monthly","start":"2026-10-01T18:00:00","end":null,"enabled":true,"repetition":{"interval":"PT30M","duration":"PT8H","stopAtDurationEnd":false},"randomDelay":null,"executionTimeLimit":null,"days":[1,15],"lastDay":true,"months":["January","July"]},{"type":"MonthlyDow","start":"2026-10-01T18:00:00","end":null,"enabled":true,"repetition":null,"randomDelay":null,"executionTimeLimit":null,"weeks":["First","Last"],"days":["Monday","Friday"],"months":["January","February","March","April","May","June","July","August","September","October","November","December"]},{"type":"Logon","start":"0001-01-01T00:00:00","end":null,"enabled":true,"repetition":null,"randomDelay":null,"executionTimeLimit":null,"hasUser":true,"delay":null}],"principal":{"logonType":"InteractiveToken","runLevel":"LeastPrivilege"},"settings":{"enabled":true,"hidden":false,"multipleInstances":"IgnoreNew","startWhenAvailable":true,"disallowStartIfOnBatteries":false,"stopIfGoingOnBatteries":false,"executionTimeLimit":"PT35M","wakeToRun":false,"allowStartOnDemand":true,"allowHardTerminate":true,"runOnlyIfNetworkAvailable":false,"runOnlyIfIdle":false,"priority":7,"deleteExpiredTaskAfter":null,"restartCount":null,"restartInterval":null,"other":[]},"actions":[{"command":"cmd.exe","arguments":"/d /c echo hi","workingDirectory":"C:\\w"}],"otherActions":[]}""";
}
