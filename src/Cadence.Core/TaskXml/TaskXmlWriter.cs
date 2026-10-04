using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Cadence.Core.Model;

namespace Cadence.Core.TaskXml;

/// <summary>Writes Task Scheduler XML: UTF-16 declaration, version 1.4, schema element order, booleans as true/false.</summary>
public static class TaskXmlWriter
{
    static readonly XNamespace Ns = XmlCanon.Ns;

    static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static readonly string[] MonthNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    sealed class Utf16StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.Unicode;
    }

    public static string Write(TaskDefinitionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Entitize,   // keeps \n and \r inside text (Documentation) intact
            OmitXmlDeclaration = true,
            Encoding = Encoding.Unicode,
            ConformanceLevel = ConformanceLevel.Document,
        };
        var sw = new Utf16StringWriter();
        sw.Write("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
        using (var w = XmlWriter.Create(sw, settings))
        {
            w.WriteStartElement("Task", Ns.NamespaceName);
            w.WriteAttributeString("version", "1.4");
            WriteRegistration(w, model.RegistrationInfo);
            if (model.Triggers.Count > 0)
            {
                w.WriteStartElement("Triggers");
                foreach (var t in model.Triggers) WriteTrigger(w, t);
                w.WriteEndElement();
            }
            WritePrincipal(w, model.Principal);
            WriteSettings(w, model.Settings);
            WriteActions(w, model);
            w.WriteEndElement();
        }
        return sw.ToString();
    }

    // ----- registration, principal, settings, actions -----------------------------------------------

    static void WriteRegistration(XmlWriter w, RegistrationInfoModel r)
    {
        w.WriteStartElement("RegistrationInfo");
        Opt(w, "Author", r.Author);
        Opt(w, "Source", r.Source);
        Opt(w, "Date", r.Date);
        Opt(w, "Description", r.Description);
        Opt(w, "Documentation", r.Documentation);
        Opt(w, "URI", r.Uri);
        w.WriteEndElement();
    }

    static void WritePrincipal(XmlWriter w, PrincipalModel p)
    {
        w.WriteStartElement("Principals");
        w.WriteStartElement("Principal");
        w.WriteAttributeString("id", "Author");
        Opt(w, "UserId", p.UserId);
        El(w, "LogonType", p.LogonType);
        El(w, "RunLevel", p.RunLevel);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    static void WriteSettings(XmlWriter w, SettingsModel s)
    {
        var d = new SettingsModel();
        w.WriteStartElement("Settings");
        El(w, "MultipleInstancesPolicy", s.MultipleInstancesPolicy);
        El(w, "DisallowStartIfOnBatteries", Bool(s.DisallowStartIfOnBatteries));
        El(w, "StopIfGoingOnBatteries", Bool(s.StopIfGoingOnBatteries));
        El(w, "StartWhenAvailable", Bool(s.StartWhenAvailable));
        El(w, "ExecutionTimeLimit", IsoDuration.Format(s.ExecutionTimeLimit ?? TimeSpan.Zero));
        El(w, "Enabled", Bool(s.Enabled));
        if (s.Hidden != d.Hidden) El(w, "Hidden", Bool(s.Hidden));
        if (s.WakeToRun != d.WakeToRun) El(w, "WakeToRun", Bool(s.WakeToRun));
        if (s.AllowStartOnDemand != d.AllowStartOnDemand) El(w, "AllowStartOnDemand", Bool(s.AllowStartOnDemand));
        if (s.AllowHardTerminate != d.AllowHardTerminate) El(w, "AllowHardTerminate", Bool(s.AllowHardTerminate));
        if (s.RunOnlyIfNetworkAvailable != d.RunOnlyIfNetworkAvailable) El(w, "RunOnlyIfNetworkAvailable", Bool(s.RunOnlyIfNetworkAvailable));
        if (s.RunOnlyIfIdle != d.RunOnlyIfIdle) El(w, "RunOnlyIfIdle", Bool(s.RunOnlyIfIdle));
        if (s.Priority != d.Priority) El(w, "Priority", Int(s.Priority));
        if (s.DeleteExpiredTaskAfter is { } del) El(w, "DeleteExpiredTaskAfter", IsoDuration.Format(del));
        if (s.RestartInterval is { } ri && s.RestartCount is { } rc)
        {
            w.WriteStartElement("RestartOnFailure");
            El(w, "Interval", IsoDuration.Format(ri));
            El(w, "Count", Int(rc));
            w.WriteEndElement();
        }
        foreach (var other in s.OtherElements) XmlCanon.Import(other).WriteTo(w);
        w.WriteEndElement();
    }

    static void WriteActions(XmlWriter w, TaskDefinitionModel m)
    {
        w.WriteStartElement("Actions");
        w.WriteAttributeString("Context", "Author");
        foreach (var a in m.ExecActions)
        {
            w.WriteStartElement("Exec");
            El(w, "Command", a.Command);
            Opt(w, "Arguments", a.Arguments);
            Opt(w, "WorkingDirectory", a.WorkingDirectory);
            w.WriteEndElement();
        }
        foreach (var other in m.OtherActionsXml) XmlCanon.Import(other).WriteTo(w);
        w.WriteEndElement();
    }

    // ----- triggers ---------------------------------------------------------------------------------

    static void WriteTrigger(XmlWriter w, TaskTrigger t)
    {
        switch (t)
        {
            case UnsupportedTrigger u:
                XmlCanon.Import(u.RawXml).WriteTo(w);
                return;
            case TimeTrigger:
                w.WriteStartElement("TimeTrigger");
                WriteBase(w, t, randomDelay: true);
                w.WriteEndElement();
                return;
            case LogonTrigger l:
                w.WriteStartElement("LogonTrigger");
                WriteBase(w, t, randomDelay: false);
                Opt(w, "UserId", l.UserId);
                if (l.Delay is { } delay) El(w, "Delay", IsoDuration.Format(delay));
                w.WriteEndElement();
                return;
        }

        w.WriteStartElement("CalendarTrigger");
        WriteBase(w, t, randomDelay: true);
        switch (t)
        {
            case DailyTrigger d:
                w.WriteStartElement("ScheduleByDay");
                El(w, "DaysInterval", Int(d.DaysInterval));
                w.WriteEndElement();
                break;
            case WeeklyTrigger wk:
                w.WriteStartElement("ScheduleByWeek");
                WriteDays(w, wk.Days);
                El(w, "WeeksInterval", Int(wk.WeeksInterval));
                w.WriteEndElement();
                break;
            case MonthlyTrigger mo:
                w.WriteStartElement("ScheduleByMonth");
                w.WriteStartElement("DaysOfMonth");
                foreach (int day in mo.Days) El(w, "Day", Int(day));
                if (mo.LastDay) El(w, "Day", "Last");
                w.WriteEndElement();
                WriteMonths(w, mo.Months);
                w.WriteEndElement();
                break;
            case MonthlyDowTrigger md:
                w.WriteStartElement("ScheduleByMonthDayOfWeek");
                w.WriteStartElement("Weeks");
                foreach (var wk2 in md.Weeks) El(w, "Week", wk2 == WeekOfMonth.Last ? "Last" : Int((int)wk2));
                w.WriteEndElement();
                WriteDays(w, md.Days);
                WriteMonths(w, md.Months);
                w.WriteEndElement();
                break;
            default:
                throw new TaskXmlException("Cannot write a trigger of type " + t.GetType().Name + ".");
        }
        w.WriteEndElement();
    }

    static void WriteBase(XmlWriter w, TaskTrigger t, bool randomDelay)
    {
        // A LogonTrigger's StartBoundary stays at the default DateTime and must not be written.
        if (t.StartBoundaryOffset is { } off) El(w, "StartBoundary", off.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        else if (t.StartBoundary != default) El(w, "StartBoundary", Stamp(t.StartBoundary));
        if (t.EndBoundary is { } end) El(w, "EndBoundary", Stamp(end));
        El(w, "Enabled", Bool(t.Enabled));
        if (t.Repetition is { } r)
        {
            w.WriteStartElement("Repetition");
            El(w, "Interval", IsoDuration.Format(r.Interval));
            if (r.Duration is { } dur) El(w, "Duration", IsoDuration.Format(dur));
            if (r.StopAtDurationEnd) El(w, "StopAtDurationEnd", "true");
            w.WriteEndElement();
        }
        if (t.ExecutionTimeLimit is { } etl) El(w, "ExecutionTimeLimit", IsoDuration.Format(etl));
        Opt(w, "Id", t.Id);
        if (randomDelay && t.RandomDelay is { } rd) El(w, "RandomDelay", IsoDuration.Format(rd));
    }

    static void WriteDays(XmlWriter w, WeekDays days)
    {
        w.WriteStartElement("DaysOfWeek");
        for (int i = 0; i < 7; i++)
            if (((int)days & (1 << i)) != 0) { w.WriteStartElement(DayNames[i]); w.WriteEndElement(); }
        w.WriteEndElement();
    }

    static void WriteMonths(XmlWriter w, MonthSet months)
    {
        w.WriteStartElement("Months");
        for (int i = 0; i < 12; i++)
            if (((int)months & (1 << i)) != 0) { w.WriteStartElement(MonthNames[i]); w.WriteEndElement(); }
        w.WriteEndElement();
    }

    // ----- helpers ----------------------------------------------------------------------------------

    static string Stamp(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    static string Bool(bool b) => b ? "true" : "false";

    static string Int(int n) => n.ToString(CultureInfo.InvariantCulture);

    static void El(XmlWriter w, string name, string value) => w.WriteElementString(name, value);

    static void Opt(XmlWriter w, string name, string? value)
    {
        if (value is not null) w.WriteElementString(name, value);
    }
}
