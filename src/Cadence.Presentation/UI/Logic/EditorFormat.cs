using System.Globalization;
using System.IO;
using Cadence.Core.Execution;
using Cadence.Core.History;
using Cadence.Core.Holidays;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;

namespace Cadence.App.UI.Logic;

/// <summary>One row of the editor's recent-runs list (PLAN 7.2 C), as plain strings.</summary>
public sealed record RunRowText(string Title, BadgeKind Tone, string When, string Duration, string? Note, bool IsSkipped);

/// <summary>The expanded details of one run: what the click on a row shows (the output itself is loaded separately).</summary>
public sealed record RunDetailText(string Exit, string Started, string? Message, bool MessageIsInformational);

/// <summary>The three lines of the program card: what runs, where it lives, and how it is run ("Runs with py.exe · from the shortcut Stock ingestion"; empty when there is nothing to add).</summary>
public sealed record ProgramCardText(string Title, string Folder, string Via);

/// <summary>A timeout choice of the Advanced section. <see cref="Minutes"/> is null for "No limit"; <see cref="IsCustom"/> asks for a number.</summary>
public sealed record TimeoutChoice(string Label, int? Minutes, bool IsCustom);

/// <summary>Pure text and rules for the editor that are not about the schedule (PLAN 7.2 B and C, Phase 13 [CP-A]). No WPF, no clock.</summary>
public static class EditorFormat
{
    public static readonly IReadOnlyList<TimeoutChoice> TimeoutChoices =
    [
        new("No limit", null, false),
        new("5 minutes", 5, false),
        new("15 minutes", 15, false),
        new("1 hour", 60, false),
        new("4 hours", 240, false),
        new("Custom…", null, true),
    ];

    static readonly string[] ScriptExtensions = [".py", ".js", ".ps1", ".bat", ".cmd", ".vbs", ".wsf"];

    // ---- working folder ----

    /// <summary>
    /// The folder a job should start in when the user leaves the working folder empty: the folder of the first rooted script path among the
    /// arguments (<c>py.exe -u "C:\x\ingest.py"</c> starts in <c>C:\x</c>), else the program's own folder. Null when neither is a rooted path
    /// (a bare <c>node.exe</c> has no folder to start in). Without this the job would start in C:\Windows\System32.
    /// </summary>
    public static string? DefaultWorkingFolder(string exe, string args)
    {
        if (ScriptOf(exe ?? "", args ?? "") is { } script) return FolderOf(script);
        string e = (exe ?? "").Trim().Trim('"');
        return IsRooted(e) ? FolderOf(e) : null;
    }

    /// <summary>The script a job runs, when it runs one through an interpreter: the batch file inside a <c>cmd.exe /d /s /c ""x.bat" ..."</c> line, else the first rooted script among the arguments. Null for a plain program.</summary>
    public static string? ScriptOf(string exe, string args)
    {
        string a = (args ?? "").Trim();
        if (FileName(exe).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) && a.StartsWith("/d /s /c \"\"", StringComparison.OrdinalIgnoreCase))
        {
            int close = a.IndexOf('"', 11);
            if (close > 11) return a[11..close];
        }
        foreach (string arg in CommandLine.Split(a))
        {
            if (IsRooted(arg) && ScriptExtensions.Contains(Extension(arg), StringComparer.OrdinalIgnoreCase)) return arg;
        }
        return null;
    }

    /// <summary>
    /// What the program card shows: the script's name when a script runs (the thing the user cares about), else the program's name; its folder; and a
    /// quieter third line ("Runs with py.exe · from the shortcut Stock ingestion"). <paramref name="sourcePath"/> is the dropped file.
    /// </summary>
    public static ProgramCardText ProgramCard(string exe, string args, string? sourcePath)
    {
        string? script = ScriptOf(exe, args);
        string title = FileName(script ?? exe);
        string folder = FolderPart(script ?? exe);
        var via = new List<string>();
        if (script is not null)
        {
            string runner = FileName(exe);
            via.Add(runner.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ? "Runs with Windows batch (cmd.exe)" : "Runs with " + runner);
        }
        if (sourcePath is not null && Extension(sourcePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            via.Add("from the shortcut " + Path.GetFileNameWithoutExtension(FileName(sourcePath)));
        string text = string.Join(" · ", via);
        if (text.Length > 0) text = char.ToUpperInvariant(text[0]) + text[1..];
        return new ProgramCardText(title, folder, text);
    }

    static bool IsRooted(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/')) return true;
        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    static string Extension(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        int dot = path.LastIndexOf('.');
        return dot > slash && dot >= 0 ? path[dot..] : "";
    }

    static string? FolderOf(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        if (slash < 0) return null;
        string folder = path[..slash];
        if (folder.Length == 2 && folder[1] == ':') folder += "\\";   // C:\file.exe -> C:\
        return folder.Length == 0 ? null : folder;
    }

    /// <summary>The file name of a program path ("C:\Tools\yt-dlp.exe" gives "yt-dlp.exe"), also for %VAR% paths.</summary>
    public static string FileName(string path)
    {
        string p = (path ?? "").Trim().Trim('"');
        int slash = p.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? p[(slash + 1)..] : p;
    }

    /// <summary>The folder part of a program path for display ("C:\Tools"); empty for a bare name.</summary>
    public static string FolderPart(string path)
    {
        string p = (path ?? "").Trim().Trim('"');
        int slash = p.LastIndexOfAny(['\\', '/']);
        return slash > 0 ? p[..slash] : "";
    }

    // ---- times ----

    /// <summary>Accepts "8", "8:00", "08:00", "17:30", "8am", "5:30 pm". Minutes are 0-59, hours 0-23 (1-12 with am/pm).</summary>
    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = default;
        string t = (text ?? "").Trim().ToLowerInvariant().Replace('.', ':');
        if (t.Length == 0) return false;
        int meridiem = 0;   // 1 = am, 2 = pm
        if (t.EndsWith("am", StringComparison.Ordinal)) { meridiem = 1; t = t[..^2].TrimEnd(); }
        else if (t.EndsWith("pm", StringComparison.Ordinal)) { meridiem = 2; t = t[..^2].TrimEnd(); }
        string[] parts = t.Split(':');
        if (parts.Length is < 1 or > 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour) || parts[0].Length > 2) return false;
        int minute = 0;
        if (parts.Length == 2 && (parts[1].Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute))) return false;
        if (minute > 59) return false;
        if (meridiem != 0)
        {
            if (hour is < 1 or > 12) return false;
            hour = hour % 12 + (meridiem == 2 ? 12 : 0);
        }
        if (hour > 23) return false;
        time = new TimeOnly(hour, minute);
        return true;
    }

    /// <summary>"8:00", "17:30".</summary>
    public static string TimeText(TimeOnly t) => t.ToString("H:mm", CultureInfo.InvariantCulture);

    // ---- preview ----

    /// <summary>"today", "tomorrow", "in 3 days" (up to two weeks), else empty. Calendar days in <paramref name="tz"/>.</summary>
    public static string RelativeDay(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo tz)
    {
        DateTime a = TimeZoneInfo.ConvertTime(at, tz).DateTime;
        DateTime n = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        int days = (a.Date - n.Date).Days;
        if (days == 0) return "today";
        if (days == 1) return "tomorrow";
        if (days is > 1 and <= 14) return "in " + days.ToString(CultureInfo.InvariantCulture) + " days";
        return "";
    }

    /// <summary>The list shown in the Skip holidays tooltip: "Thu 26 Nov  Thanksgiving Day", one per line, for the year.</summary>
    public static string HolidayList(HolidayCalendar calendar, int year)
    {
        if (calendar == HolidayCalendar.None) return "Every scheduled run happens, holidays included.";
        string title = calendar == HolidayCalendar.Nyse ? "NYSE market holidays " : "US federal holidays ";
        IEnumerable<string> lines = HolidayRules.For(calendar, year)
            .Select(h => h.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture) + "   " + h.Name);
        return title + year.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", lines);
    }

    // ---- runs ----

    /// <summary>"Today 18:00:02", "Yesterday 18:00:01", then "Thu 19 Nov 18:00" (the year only when it differs).</summary>
    public static string PastWhen(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo tz)
    {
        DateTime w = TimeZoneInfo.ConvertTime(when, tz).DateTime;
        DateTime n = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        int days = (n.Date - w.Date).Days;
        if (days == 0) return "Today " + w.ToString("H:mm:ss", CultureInfo.InvariantCulture);
        if (days == 1) return "Yesterday " + w.ToString("H:mm:ss", CultureInfo.InvariantCulture);
        return w.ToString(w.Year == n.Year ? "ddd d MMM H:mm" : "ddd d MMM yyyy H:mm", CultureInfo.InvariantCulture);
    }

    public static BadgeKind ToneOf(RunStatus status) => status switch
    {
        RunStatus.Ok => BadgeKind.Ok,
        RunStatus.Failed or RunStatus.LaunchError => BadgeKind.Failed,
        RunStatus.TimedOut => BadgeKind.TimedOut,
        RunStatus.Skipped => BadgeKind.Skipped,
        RunStatus.Running => BadgeKind.Running,
        RunStatus.NotReported => BadgeKind.NotReported,
        _ => BadgeKind.None,
    };

    public static RunRowText Row(RunView run, DateTimeOffset now, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(run);
        string title = run.Status switch
        {
            RunStatus.Ok => "Ok",
            RunStatus.Failed => run.ExitCode is { } c ? "Failed · exit " + c.ToString(CultureInfo.InvariantCulture) : "Failed",
            RunStatus.TimedOut => "Timed out",
            RunStatus.LaunchError => "Couldn't start",
            RunStatus.Skipped => string.IsNullOrWhiteSpace(run.Message) ? "Skipped" : "Skipped · " + run.Message,
            RunStatus.Running => "Running…",
            RunStatus.NotReported => ListFormat.LaunchFailureCode(run) is { } launch
                ? TaskResultCodes.ListLabel(launch) ?? ListFormat.DidntReportTitle : ListFormat.DidntReportTitle,
            _ => run.Status.ToString(),
        };
        string duration = run.Duration is { } d ? Cadence.App.Cli.CliFormat.Duration(d) : "";
        // The note under the row: an error text, but not the holiday name that is already in the title.
        string? message = run.Message;
        string? note = run.Status is RunStatus.Skipped or RunStatus.Ok ? null
            : run.Status == RunStatus.Running ? null
            : string.IsNullOrWhiteSpace(message) ? null : OneLine(message);
        return new RunRowText(title, ToneOf(run.Status), PastWhen(run.When, now, tz), duration, note, run.Status == RunStatus.Skipped);
    }

    public static RunDetailText Detail(RunView run, DateTimeOffset now, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(run);
        string exit = run.Status switch
        {
            RunStatus.Skipped => "Not run (skipped)",
            RunStatus.Running => "Still running",
            RunStatus.NotReported => "No exit code (the job never reported back)",
            RunStatus.LaunchError => "The program did not start",
            _ => run.ExitCode is { } c ? ExitText(c) : "No exit code",
        };
        string started = "Started " + PastWhen(run.When, now, tz) + (run.Duration is { } d ? " · ran for " + Cadence.App.Cli.CliFormat.Duration(d) : "");
        string? message = string.IsNullOrWhiteSpace(run.Message) ? null : run.Message;
        // Possibilities, labelled as such, beside the one code that has a well-known list of them. Never a diagnosis.
        if (message is not null && IsBlockedRun(run)) message += "\n" + TaskResultCodes.AccessDeniedPossibilities;
        return new RunDetailText(exit, started, message, run.MessageIsInformational);
    }

    /// <summary>What the line under Run now says once the run it started has ended: "Finished: Ok · 12 s", "Finished: Failed · exit 1", the blocked-launch sentence.</summary>
    public static string FinishedText(RunView run, RunRowText text)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(text);
        if (run.Status == RunStatus.NotReported)
            return ListFormat.LaunchFailureCode(run) is not null && !string.IsNullOrWhiteSpace(run.Message)
                ? run.Message + " The job did not run (details below)."
                : "Task Scheduler started it, but it didn't report back (details below).";
        if (run.Status == RunStatus.Skipped) return text.Title + ". Run now follows the holiday list too.";
        return "Finished: " + text.Title + (text.Duration.Length > 0 ? " · " + text.Duration : "");
    }

    /// <summary>True for the synthesized row of a launch Task Scheduler reported as access denied (0x80070005, no run record): it offers the neutral "Open Windows Security" shortcut.</summary>
    public static bool IsBlockedRun(RunView run) => ListFormat.LaunchFailureCode(run) == unchecked((int)TaskResultCodes.AccessDenied);

    /// <summary>True for the synthesized row of any launch Windows could not start (the <see cref="TaskResultCodes.IsLaunchFailure"/> set).</summary>
    public static bool IsLaunchFailureRun(RunView run) => ListFormat.LaunchFailureCode(run) is not null;

    /// <summary>"Exit code 3", or for HRESULT-like values "Exit code 0x80070005 · Access denied (0x80070005)".</summary>
    public static string ExitText(int code)
    {
        if (code is >= 0 and < 0x10000) return "Exit code " + code.ToString(CultureInfo.InvariantCulture);
        string hex = "0x" + ((uint)code).ToString("X8", CultureInfo.InvariantCulture);
        return "Exit code " + code.ToString(CultureInfo.InvariantCulture) + " · " + hex;
    }

    /// <summary>"12.4 s", or "-" when no Ok or Failed run exists.</summary>
    public static string AverageText(JobSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.AverageRuntime is { } a ? Cadence.App.Cli.CliFormat.Duration(a) : "-";
    }

    /// <summary>The big number: "96%" (rounded), or "-".</summary>
    public static string SuccessText(JobSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.SuccessRate is { } r ? Math.Round(r * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%" : "-";
    }

    /// <summary>"of the last 50 runs" / "of the 1 run so far".</summary>
    public static string SuccessDetail(JobSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.SuccessRate is null) return "no counted runs yet";
        return summary.SampleSize == 1 ? "of 1 run" : "of the last " + summary.SampleSize.ToString(CultureInfo.InvariantCulture) + " runs";
    }

    static string OneLine(string text)
    {
        string one = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return one.Length > 160 ? one[..157] + "…" : one;
    }

    /// <summary>The base name of a path without its extension (used for placeholders): "ingest" for "C:\x\ingest.py".</summary>
    public static string StemOf(string path)
    {
        string name = FileName(path);
        return Path.GetFileNameWithoutExtension(name);
    }
}
