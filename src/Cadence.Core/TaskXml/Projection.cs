using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cadence.Core.Model;

namespace Cadence.Core.TaskXml;

/// <summary>
/// Canonical JSON of everything that defines how a task behaves: all of the model except RegistrationInfo and
/// Principal.UserId (Task Scheduler rewrites it to a SID). Fixed property order; durations as canonical ISO strings;
/// DateTimes as yyyy-MM-ddTHH:mm:ss; triggers in document order; enums by name. Trigger Id and StartBoundaryOffset
/// are not part of it (the local StartBoundary is), and a LogonTrigger's user is reduced to present/absent because
/// Task Scheduler may rewrite it the same way as the principal's.
/// </summary>
public static class Projection
{
    static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static readonly string[] MonthNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public static string Canonical(TaskDefinitionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("triggers");
            foreach (var t in model.Triggers) WriteTrigger(w, t);
            w.WriteEndArray();

            w.WriteStartObject("principal");
            w.WriteString("logonType", model.Principal.LogonType);
            w.WriteString("runLevel", model.Principal.RunLevel);
            w.WriteEndObject();

            WriteSettings(w, model.Settings);

            w.WriteStartArray("actions");
            foreach (var a in model.ExecActions)
            {
                w.WriteStartObject();
                w.WriteString("command", a.Command);
                w.WriteString("arguments", a.Arguments);
                w.WriteString("workingDirectory", a.WorkingDirectory);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("otherActions");
            foreach (var x in model.OtherActionsXml) w.WriteStringValue(x);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static void WriteSettings(Utf8JsonWriter w, SettingsModel s)
    {
        w.WriteStartObject("settings");
        w.WriteBoolean("enabled", s.Enabled);
        w.WriteBoolean("hidden", s.Hidden);
        w.WriteString("multipleInstances", s.MultipleInstancesPolicy);
        w.WriteBoolean("startWhenAvailable", s.StartWhenAvailable);
        w.WriteBoolean("disallowStartIfOnBatteries", s.DisallowStartIfOnBatteries);
        w.WriteBoolean("stopIfGoingOnBatteries", s.StopIfGoingOnBatteries);
        Dur(w, "executionTimeLimit", s.ExecutionTimeLimit);
        w.WriteBoolean("wakeToRun", s.WakeToRun);
        w.WriteBoolean("allowStartOnDemand", s.AllowStartOnDemand);
        w.WriteBoolean("allowHardTerminate", s.AllowHardTerminate);
        w.WriteBoolean("runOnlyIfNetworkAvailable", s.RunOnlyIfNetworkAvailable);
        w.WriteBoolean("runOnlyIfIdle", s.RunOnlyIfIdle);
        w.WriteNumber("priority", s.Priority);
        Dur(w, "deleteExpiredTaskAfter", s.DeleteExpiredTaskAfter);
        if (s.RestartCount is { } c) w.WriteNumber("restartCount", c); else w.WriteNull("restartCount");
        Dur(w, "restartInterval", s.RestartInterval);
        w.WriteStartArray("other");
        foreach (var o in s.OtherElements.OrderBy(x => x, StringComparer.Ordinal)) w.WriteStringValue(o);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteTrigger(Utf8JsonWriter w, TaskTrigger t)
    {
        w.WriteStartObject();
        if (t is UnsupportedTrigger u)
        {
            w.WriteString("type", "Unsupported");
            w.WriteString("element", u.ElementName);
            w.WriteString("xml", u.RawXml);
            w.WriteEndObject();
            return;
        }

        w.WriteString("type", t switch
        {
            TimeTrigger => "Time",
            DailyTrigger => "Daily",
            WeeklyTrigger => "Weekly",
            MonthlyTrigger => "Monthly",
            MonthlyDowTrigger => "MonthlyDow",
            LogonTrigger => "Logon",
            _ => t.GetType().Name,
        });
        w.WriteString("start", Stamp(t.StartBoundary));
        if (t.EndBoundary is { } end) w.WriteString("end", Stamp(end)); else w.WriteNull("end");
        w.WriteBoolean("enabled", t.Enabled);
        if (t.Repetition is { } r)
        {
            w.WriteStartObject("repetition");
            w.WriteString("interval", IsoDuration.Format(r.Interval));
            Dur(w, "duration", r.Duration);
            w.WriteBoolean("stopAtDurationEnd", r.StopAtDurationEnd);
            w.WriteEndObject();
        }
        else w.WriteNull("repetition");
        Dur(w, "randomDelay", t is LogonTrigger ? null : t.RandomDelay);
        Dur(w, "executionTimeLimit", t.ExecutionTimeLimit);

        switch (t)
        {
            case DailyTrigger d:
                w.WriteNumber("daysInterval", d.DaysInterval);
                break;
            case WeeklyTrigger wk:
                w.WriteNumber("weeksInterval", wk.WeeksInterval);
                Days(w, "days", wk.Days);
                break;
            case MonthlyTrigger m:
                w.WriteStartArray("days");
                foreach (int day in m.Days.Distinct().Order()) w.WriteNumberValue(day);
                w.WriteEndArray();
                w.WriteBoolean("lastDay", m.LastDay);
                Months(w, "months", m.Months);
                break;
            case MonthlyDowTrigger md:
                w.WriteStartArray("weeks");
                foreach (var wk2 in md.Weeks.Distinct().Order()) w.WriteStringValue(wk2.ToString());
                w.WriteEndArray();
                Days(w, "days", md.Days);
                Months(w, "months", md.Months);
                break;
            case LogonTrigger l:
                w.WriteBoolean("hasUser", !string.IsNullOrEmpty(l.UserId));
                Dur(w, "delay", l.Delay);
                break;
        }
        w.WriteEndObject();
    }

    static void Days(Utf8JsonWriter w, string name, WeekDays days)
    {
        w.WriteStartArray(name);
        for (int i = 0; i < 7; i++)
            if (((int)days & (1 << i)) != 0) w.WriteStringValue(DayNames[i]);
        w.WriteEndArray();
    }

    static void Months(Utf8JsonWriter w, string name, MonthSet months)
    {
        w.WriteStartArray(name);
        for (int i = 0; i < 12; i++)
            if (((int)months & (1 << i)) != 0) w.WriteStringValue(MonthNames[i]);
        w.WriteEndArray();
    }

    static void Dur(Utf8JsonWriter w, string name, TimeSpan? d)
    {
        if (d is { } v) w.WriteString(name, IsoDuration.Format(v)); else w.WriteNull(name);
    }

    static string Stamp(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
