using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Cadence.Core.Model;

namespace Cadence.Core.TaskXml;

/// <summary>
/// Reads Task Scheduler XML (authored or as stored) into a model with the schema defaults applied.
/// Ignored: Task/@version, Principal/@id, Actions/@Context, UseUnifiedSchedulingEngine, default IdleSettings.
/// Preserved verbatim (canonical outer XML): unmodeled Settings children, non-Exec actions, and unsupported triggers.
/// </summary>
public static class TaskXmlReader
{
    static readonly XNamespace Ns = XmlCanon.Ns;

    static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static readonly string[] MonthNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public static TaskDefinitionModel Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException ex) { throw new TaskXmlException("The task XML is not well-formed: " + ex.Message, ex); }

        var root = doc.Root;
        if (root is null || root.Name != Ns + "Task")
            throw new TaskXmlException("This is not Task Scheduler XML: the root element must be <Task> in the namespace " + Ns.NamespaceName + ".");

        try
        {
            return new TaskDefinitionModel(
                ReadRegistration(root.Element(Ns + "RegistrationInfo")),
                new ValueList<TaskTrigger>((root.Element(Ns + "Triggers")?.Elements() ?? []).Select(ReadTrigger)),
                ReadPrincipal(root.Element(Ns + "Principals")?.Element(Ns + "Principal")),
                ReadSettings(root.Element(Ns + "Settings")),
                new ValueList<ExecActionModel>(ReadExecs(root.Element(Ns + "Actions"))),
                new ValueList<string>(ReadOtherActions(root.Element(Ns + "Actions"))));
        }
        catch (FormatException ex)
        {
            throw new TaskXmlException(ex.Message, ex);
        }
    }

    // ----- registration, principal ------------------------------------------------------------------

    static RegistrationInfoModel ReadRegistration(XElement? e) => new(
        Text(e, "Author"), Text(e, "Description"), Text(e, "Documentation"), Text(e, "Source"), Text(e, "URI"), Text(e, "Date"));

    static PrincipalModel ReadPrincipal(XElement? e) => new(
        Text(e, "UserId"), Text(e, "LogonType") ?? "InteractiveToken", Text(e, "RunLevel") ?? "LeastPrivilege");

    static string? Text(XElement? parent, string name) => parent?.Element(Ns + name)?.Value;

    // ----- settings ---------------------------------------------------------------------------------

    static SettingsModel ReadSettings(XElement? e)
    {
        var s = new SettingsModel();
        if (e is null) return s;
        var others = new List<string>();
        XElement? idle = null;
        foreach (var c in e.Elements())
        {
            string name = c.Name.Namespace == Ns ? c.Name.LocalName : "";
            switch (name)
            {
                case "Enabled": s = s with { Enabled = Bool(c) }; break;
                case "Hidden": s = s with { Hidden = Bool(c) }; break;
                case "MultipleInstancesPolicy": s = s with { MultipleInstancesPolicy = c.Value.Trim() }; break;
                case "StartWhenAvailable": s = s with { StartWhenAvailable = Bool(c) }; break;
                case "DisallowStartIfOnBatteries": s = s with { DisallowStartIfOnBatteries = Bool(c) }; break;
                case "StopIfGoingOnBatteries": s = s with { StopIfGoingOnBatteries = Bool(c) }; break;
                case "ExecutionTimeLimit":
                    {
                        var d = Duration(c);
                        s = s with { ExecutionTimeLimit = d == TimeSpan.Zero ? null : d };
                        break;
                    }
                case "WakeToRun": s = s with { WakeToRun = Bool(c) }; break;
                case "AllowStartOnDemand": s = s with { AllowStartOnDemand = Bool(c) }; break;
                case "AllowHardTerminate": s = s with { AllowHardTerminate = Bool(c) }; break;
                case "RunOnlyIfNetworkAvailable": s = s with { RunOnlyIfNetworkAvailable = Bool(c) }; break;
                case "RunOnlyIfIdle": s = s with { RunOnlyIfIdle = Bool(c) }; break;
                case "Priority": s = s with { Priority = Int(c) }; break;
                case "DeleteExpiredTaskAfter": s = s with { DeleteExpiredTaskAfter = Duration(c) }; break;
                case "RestartOnFailure":
                    {
                        var interval = c.Element(Ns + "Interval");
                        var count = c.Element(Ns + "Count");
                        if (interval is not null && count is not null)
                            s = s with { RestartInterval = Duration(interval), RestartCount = Int(count) };
                        break;
                    }
                case "UseUnifiedSchedulingEngine": break;   // added by Task Scheduler
                case "IdleSettings": idle = c; break;
                default: others.Add(XmlCanon.Outer(c, trigger: false)); break;
            }
        }
        // IdleSettings equal to the defaults (StopOnIdleEnd true, RestartOnIdle false) is noise that Task Scheduler adds.
        // Duration/WaitTimeout only matter when the task runs only if idle.
        if (idle is not null && !(IdleIsDefault(idle) && !s.RunOnlyIfIdle))
            others.Add(XmlCanon.Outer(idle, trigger: false));
        others.Sort(StringComparer.Ordinal);
        return s with { OtherElements = new ValueList<string>(others) };
    }

    static bool IdleIsDefault(XElement idle)
    {
        foreach (var c in idle.Elements())
        {
            switch (c.Name.LocalName)
            {
                case "StopOnIdleEnd": if (!Bool(c)) return false; break;
                case "RestartOnIdle": if (Bool(c)) return false; break;
                case "Duration" or "WaitTimeout": break;
                default: return false;
            }
        }
        return true;
    }

    // ----- actions ----------------------------------------------------------------------------------

    static IEnumerable<ExecActionModel> ReadExecs(XElement? actions)
    {
        foreach (var a in actions?.Elements(Ns + "Exec") ?? [])
        {
            string command = Text(a, "Command") ?? throw new TaskXmlException("An <Exec> action has no <Command>.");
            yield return new ExecActionModel(command, NullIfEmpty(Text(a, "Arguments")), NullIfEmpty(Text(a, "WorkingDirectory")));
        }
    }

    static IEnumerable<string> ReadOtherActions(XElement? actions) =>
        (actions?.Elements() ?? []).Where(a => a.Name != Ns + "Exec").Select(a => XmlCanon.Outer(a, trigger: false));

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ----- triggers ---------------------------------------------------------------------------------

    static TaskTrigger ReadTrigger(XElement e)
    {
        if (e.Name.Namespace != Ns) return Unsupported(e);
        switch (e.Name.LocalName)
        {
            case "TimeTrigger":
                return ApplyBase(new TimeTrigger(), e);
            case "LogonTrigger":
                {
                    var delay = e.Element(Ns + "Delay");
                    return ApplyBase(new LogonTrigger(NullIfEmpty(Text(e, "UserId")), delay is null ? null : Duration(delay)), e);
                }
            case "CalendarTrigger":
                return ReadCalendar(e);
            default:
                return Unsupported(e);
        }
    }

    static TaskTrigger Unsupported(XElement e) =>
        ApplyBase(new UnsupportedTrigger(e.Name.LocalName, XmlCanon.Outer(e, trigger: true)), e);

    static TaskTrigger ReadCalendar(XElement e)
    {
        if (e.Element(Ns + "ScheduleByDay") is { } day)
            return ApplyBase(new DailyTrigger(Positive(day.Element(Ns + "DaysInterval"), "DaysInterval")), e);
        if (e.Element(Ns + "ScheduleByWeek") is { } week)
            return ApplyBase(new WeeklyTrigger(Positive(week.Element(Ns + "WeeksInterval"), "WeeksInterval"), ReadDays(week.Element(Ns + "DaysOfWeek"))), e);
        if (e.Element(Ns + "ScheduleByMonth") is { } month)
        {
            var days = new List<int>();
            bool last = false;
            foreach (var d in month.Element(Ns + "DaysOfMonth")?.Elements(Ns + "Day") ?? [])
            {
                string v = d.Value.Trim();
                if (v == "Last") { last = true; continue; }
                int n = ParseInt(v, "Day");
                if (n < 1 || n > 31) throw new TaskXmlException($"A monthly trigger lists day {n}; days must be 1 to 31 or Last.");
                days.Add(n);
            }
            return ApplyBase(new MonthlyTrigger(new ValueList<int>(days), last, ReadMonths(month.Element(Ns + "Months"))), e);
        }
        if (e.Element(Ns + "ScheduleByMonthDayOfWeek") is { } dow)
        {
            var weeks = new List<WeekOfMonth>();
            foreach (var w in dow.Element(Ns + "Weeks")?.Elements(Ns + "Week") ?? [])
            {
                string v = w.Value.Trim();
                if (v == "Last") { weeks.Add(WeekOfMonth.Last); continue; }
                int n = ParseInt(v, "Week");
                if (n < 1 || n > 4) throw new TaskXmlException($"A monthly day-of-week trigger lists week {n}; weeks must be 1 to 4 or Last.");
                weeks.Add((WeekOfMonth)n);
            }
            return ApplyBase(new MonthlyDowTrigger(new ValueList<WeekOfMonth>(weeks), ReadDays(dow.Element(Ns + "DaysOfWeek")), ReadMonths(dow.Element(Ns + "Months"))), e);
        }
        return Unsupported(e);
    }

    static WeekDays ReadDays(XElement? e)
    {
        var d = WeekDays.None;
        foreach (var c in e?.Elements() ?? [])
        {
            int i = Array.IndexOf(DayNames, c.Name.LocalName);
            if (i < 0) throw new TaskXmlException($"Unknown day of week '{c.Name.LocalName}'.");
            d |= (WeekDays)(1 << i);
        }
        return d;
    }

    static MonthSet ReadMonths(XElement? e)
    {
        if (e is null) return MonthSet.All;
        var m = MonthSet.None;
        foreach (var c in e.Elements())
        {
            int i = Array.IndexOf(MonthNames, c.Name.LocalName);
            if (i < 0) throw new TaskXmlException($"Unknown month '{c.Name.LocalName}'.");
            m |= (MonthSet)(1 << i);
        }
        return m;
    }

    static TaskTrigger ApplyBase(TaskTrigger t, XElement e)
    {
        var start = e.Element(Ns + "StartBoundary");
        var end = e.Element(Ns + "EndBoundary");
        var rep = e.Element(Ns + "Repetition");
        var etl = e.Element(Ns + "ExecutionTimeLimit");
        var rnd = e.Element(Ns + "RandomDelay");
        var enabled = e.Element(Ns + "Enabled");
        t = t with
        {
            Enabled = enabled is null || Bool(enabled),
            Repetition = ReadRepetition(rep),
            ExecutionTimeLimit = etl is null ? null : Duration(etl),
            RandomDelay = rnd is null ? null : Duration(rnd),
            Id = NullIfEmpty(Text(e, "Id")),
        };
        if (start is not null)
        {
            var (local, off) = Boundary(start.Value, "StartBoundary");
            t = t with { StartBoundary = local, StartBoundaryOffset = off };
        }
        if (end is not null)
            t = t with { EndBoundary = Boundary(end.Value, "EndBoundary").Local };
        return t;
    }

    static Repetition? ReadRepetition(XElement? e)
    {
        if (e is null) return null;
        var interval = e.Element(Ns + "Interval");
        if (interval is null) return null;
        var iv = Duration(interval);
        if (iv == TimeSpan.Zero) return null;
        var dur = e.Element(Ns + "Duration");
        TimeSpan? d = dur is null ? null : Duration(dur);
        if (d == TimeSpan.Zero) d = null;   // PT0S means "indefinitely"
        var stop = e.Element(Ns + "StopAtDurationEnd");
        return new Repetition(iv, d, stop is not null && Bool(stop));
    }

    /// <summary>A value without an offset is a local wall-clock time; with an offset or Z, Local is the same instant in the machine's zone.</summary>
    static (DateTime Local, DateTimeOffset? Offset) Boundary(string text, string field)
    {
        string v = text.Trim();
        int t = v.IndexOf('T', StringComparison.Ordinal);
        bool hasOffset = v.EndsWith('Z') || (t >= 0 && v.AsSpan(t).IndexOfAny('+', '-') >= 0);
        if (hasOffset)
        {
            if (!DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                throw new TaskXmlException($"<{field}> '{v}' is not a valid date and time.");
            return (DateTime.SpecifyKind(dto.LocalDateTime, DateTimeKind.Unspecified), dto);
        }
        if (!DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            throw new TaskXmlException($"<{field}> '{v}' is not a valid date and time.");
        return (DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), null);
    }

    // ----- primitives -------------------------------------------------------------------------------

    static bool Bool(XElement e) => e.Value.Trim() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        var v => throw new TaskXmlException($"<{e.Name.LocalName}> must be true or false, but it is '{v}'."),
    };

    static int ParseInt(string v, string field) =>
        int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)
            ? n
            : throw new TaskXmlException($"<{field}> must be a whole number, but it is '{v}'.");

    static int Int(XElement e) => ParseInt(e.Value.Trim(), e.Name.LocalName);

    static int Positive(XElement? e, string field)
    {
        if (e is null) return 1;
        int n = ParseInt(e.Value.Trim(), field);
        if (n < 1) throw new TaskXmlException($"<{field}> must be at least 1.");
        return n;
    }

    static TimeSpan Duration(XElement e) =>
        IsoDuration.TryParse(e.Value, out var d)
            ? d
            : throw new TaskXmlException($"<{e.Name.LocalName}> '{e.Value.Trim()}' is not a supported duration (use whole hours, minutes and seconds like PT1H30M, or days like P1D).");
}
