using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cadence.Core.Holidays;
using Cadence.Core.Model;

namespace Cadence.Core.Metadata;

public enum MetadataProblem { None, Missing, NotCadence, NewerSchema, Malformed }

/// <summary>
/// Reads and writes the `cadence/1` text stored in a task's RegistrationInfo/Documentation (PLAN 3.9):
/// line 1 is the marker, then <c>\n</c>, then single-line JSON. Reading splits at the first <c>\n</c> and trims a trailing <c>\r</c>.
/// </summary>
public static class MetadataCodec
{
    public const string Marker = "cadence/1";
    public const int CurrentSchema = 1;
    /// <summary>Cap for the encoded text (the holiday list is about 4 KB; the rest is env vars and the command).</summary>
    public const int MaxEncodedLength = 16 * 1024;

    // Relaxed escaping keeps quotes as \" and non-ASCII readable; XmlWriter entitizes whatever the XML needs. Raw CR/LF never
    // occur in the JSON text (they are escaped), so the single-line property holds for any user text.
    static readonly MetadataJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    const string DateFormat = "yyyy-MM-dd";
    const string StampFormat = "yyyy-MM-dd'T'HH:mm:sszzz";

    /// <summary>Whole seconds written to <c>run.timeoutSec</c>: 0 for no timeout, otherwise the timeout rounded up (at least 1).</summary>
    public static int TimeoutSeconds(RunSpec run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Timeout is not { } t || t <= TimeSpan.Zero) return 0;
        double s = Math.Ceiling(t.TotalSeconds);
        return s >= int.MaxValue ? int.MaxValue : (int)s;
    }

    public static string Encode(CadenceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Schema != CurrentSchema) throw new ArgumentException("only schema 1 can be written", nameof(metadata));
        string text = Marker + "\n" + JsonSerializer.Serialize(ToDto(metadata), Context.MetadataDto);
        if (text.Length > MaxEncodedLength)
            throw new InvalidOperationException("The job's metadata is " + text.Length.ToString(CultureInfo.InvariantCulture) + " characters; the limit is " + MaxEncodedLength.ToString(CultureInfo.InvariantCulture) + ".");
        return text;
    }

    public static MetadataProblem TryDecode(string? documentation, out CadenceMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(documentation)) return MetadataProblem.Missing;

        int nl = documentation.IndexOf('\n', StringComparison.Ordinal);
        string first = (nl < 0 ? documentation : documentation[..nl]).TrimEnd('\r').Trim();
        if (!first.StartsWith("cadence/", StringComparison.Ordinal)) return MetadataProblem.NotCadence;

        string version = first["cadence/".Length..];
        if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out int marker) || marker < 1) return MetadataProblem.Malformed;
        if (marker > CurrentSchema) return MetadataProblem.NewerSchema;
        if (nl < 0 || documentation.Length > MaxEncodedLength + 2) return MetadataProblem.Malformed;

        string json = documentation[(nl + 1)..].Trim();
        try
        {
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return MetadataProblem.Malformed;
                if (!doc.RootElement.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out int schemaNo) || schemaNo < 1)
                    return MetadataProblem.Malformed;
                if (schemaNo > CurrentSchema) return MetadataProblem.NewerSchema;
            }
            var dto = JsonSerializer.Deserialize(json, Context.MetadataDto);
            if (dto is null) return MetadataProblem.Malformed;
            metadata = FromDto(dto);
            return metadata is null ? MetadataProblem.Malformed : MetadataProblem.None;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            metadata = null;
            return MetadataProblem.Malformed;
        }
    }

    // ---- model -> DTO ----

    /// <summary>
    /// [CP-A] Largest <c>run.timeoutSec</c> (24 days). The wrapper computes <c>[int]timeoutSec*1000</c> milliseconds; above
    /// about 24.8 days that overflows, PowerShell widens it to a double, <c>WaitForExit</c> throws, and the job keeps running
    /// while its record says launchError.
    /// </summary>
    public const int MaxTimeoutSeconds = 24 * 24 * 3600;

    // [CP-A] Encode refuses what the decoder (or the wrapper) would reject, so a save can never produce a task that then reads as
    // CorruptMetadata or fails at run time: name length, blank program, cron count, timeout range, and environment names that
    // Windows PowerShell 5.1's ConvertFrom-Json or the Win32 environment block can't hold.
    static void Validate(CadenceMetadata m)
    {
        JobDefinition job = m.Job;
        RunSpec run = job.Run;
        if (job.Name is null || job.Name.Length is < 1 or > 100) throw new ArgumentException("the job name must be 1 to 100 characters", nameof(m));
        if (job.Schedule.CronLines.Count < 1) throw new ArgumentException("the schedule has no cron lines", nameof(m));
        if (string.IsNullOrWhiteSpace(run.Exe)) throw new ArgumentException("the program to run is empty", nameof(m));
        if (TimeoutSeconds(run) > MaxTimeoutSeconds) throw new ArgumentException("the timeout can be at most 24 days", nameof(m));
        // NUL would silently cut the string at CreateProcess / the environment block. Line breaks are legal (JSON escapes them).
        static bool Bad(string? s) => s is not null && s.Contains('\0', StringComparison.Ordinal);
        if (Bad(run.Exe) || Bad(run.Args) || Bad(run.WorkingDirectory))
            throw new ArgumentException("the program, arguments and working folder can't contain NUL characters", nameof(m));
        foreach (var kv in run.Env)
        {
            if (string.IsNullOrEmpty(kv.Key) || kv.Key.Contains('=', StringComparison.Ordinal) || Bad(kv.Key) || Bad(kv.Value))
                throw new ArgumentException("environment variable names can't be empty or contain '=', and names and values can't contain NUL characters", nameof(m));
        }
    }

    static MetadataDto ToDto(CadenceMetadata m)
    {
        Validate(m);
        JobDefinition job = m.Job;
        RunSpec run = job.Run;

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // Windows environment names are case-insensitive
        foreach (var kv in run.Env)
            if (!env.TryAdd(kv.Key, kv.Value))
                throw new ArgumentException("environment variable '" + kv.Key + "' is listed twice", nameof(m));

        SkipDto? skip = null;
        if (m.Skip is { } s)
        {
            var dates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Holiday h in s.Dates)
                if (!dates.TryAdd(h.Date.ToString(DateFormat, CultureInfo.InvariantCulture), h.Name))
                    throw new ArgumentException("holiday date " + h.Date.ToString(DateFormat, CultureInfo.InvariantCulture) + " is listed twice", nameof(m));
            skip = new SkipDto { Cal = CalToken(s.Calendar), Until = s.Until.ToString(DateFormat, CultureInfo.InvariantCulture), Dates = dates };
        }

        return new MetadataDto
        {
            Schema = m.Schema,
            Gen = m.Gen,
            Wv = m.Wv,
            Id = job.Id,
            Name = job.Name,
            Enabled = job.Enabled,
            Schedule = new ScheduleDto
            {
                Cron = job.Schedule.CronLines,
                SkipHolidays = CalToken(job.Schedule.SkipHolidays),
                Anchor = job.Schedule.Anchor.ToString(DateFormat, CultureInfo.InvariantCulture),
            },
            Run = new RunDto
            {
                Kind = "runProgram",
                Exe = run.Exe,
                Args = run.Args,
                Cwd = run.WorkingDirectory,
                Env = env,
                TimeoutSec = TimeoutSeconds(run),
                Hidden = run.Hidden,
                Capture = run.Hidden,
                Encoding = run.Encoding == OutputEncoding.Utf8 ? "utf8" : "oem",
                Source = run.SourcePath,
                Skip = skip,
            },
            Settings = new SettingsDto
            {
                StartWhenAvailable = job.Settings.StartWhenAvailable,
                RunOnBatteries = job.Settings.RunOnBatteries,
                StopOnBatteries = job.Settings.StopOnBatteries,
                WakeToRun = job.Settings.WakeToRun,
            },
            Fp = m.Fp,
            Created = m.Created.ToString(StampFormat, CultureInfo.InvariantCulture),
            Updated = m.Updated.ToString(StampFormat, CultureInfo.InvariantCulture),
            CreatedBy = m.CreatedBy,
            UpdatedBy = m.UpdatedBy,
            AgentLock = job.AgentLock ? true : null,
            Prev = m.Prev,
        };
    }

    // ---- DTO -> model (null = malformed) ----

    static CadenceMetadata? FromDto(MetadataDto d)
    {
        if (d.Schema != CurrentSchema || !JobId.IsValid(d.Id)) return null;
        if (d.Name.Length is < 1 or > 100) return null;
        if (d.Schedule.Cron.Count < 1) return null;
        RunDto r = d.Run;
        if (r.Kind != "runProgram" || r.Capture != r.Hidden || r.TimeoutSec < 0) return null;
        if (r.Encoding is not ("oem" or "utf8")) return null;
        if (!TryCal(d.Schedule.SkipHolidays, true, out HolidayCalendar skipHolidays)) return null;
        if (!DateOnly.TryParseExact(d.Schedule.Anchor, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly anchor)) return null;
        if (!DateTimeOffset.TryParseExact(d.Created, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset created)) return null;
        if (!DateTimeOffset.TryParseExact(d.Updated, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset updated)) return null;

        SkipList? skip = null;
        if (r.Skip is { } s)
        {
            if (!TryCal(s.Cal, false, out HolidayCalendar cal)) return null;
            if (!DateOnly.TryParseExact(s.Until, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly until)) return null;
            var dates = new List<Holiday>(s.Dates.Count);
            foreach (var kv in s.Dates)
            {
                if (!DateOnly.TryParseExact(kv.Key, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)) return null;
                dates.Add(new Holiday(date, kv.Value));
            }
            skip = new SkipList(cal, until, new ValueList<Holiday>(dates));
        }

        var job = new JobDefinition
        {
            Id = d.Id,
            Name = d.Name,
            Enabled = d.Enabled,
            AgentLock = d.AgentLock == true,
            Schedule = new ScheduleSpec(d.Schedule.Cron, skipHolidays, anchor),
            Run = new RunSpec
            {
                Kind = ActionKind.RunProgram,
                Exe = r.Exe,
                Args = r.Args,
                WorkingDirectory = r.Cwd,
                Env = new ValueList<KeyValuePair<string, string>>(r.Env),
                Timeout = r.TimeoutSec > 0 ? TimeSpan.FromSeconds(r.TimeoutSec) : null,
                Hidden = r.Hidden,
                Encoding = r.Encoding == "utf8" ? OutputEncoding.Utf8 : OutputEncoding.Oem,
                SourcePath = r.Source,
            },
            Settings = new JobSettings
            {
                StartWhenAvailable = d.Settings.StartWhenAvailable,
                RunOnBatteries = d.Settings.RunOnBatteries,
                StopOnBatteries = d.Settings.StopOnBatteries,
                WakeToRun = d.Settings.WakeToRun,
            },
        };
        return new CadenceMetadata(d.Schema, d.Gen, d.Wv, job, skip, d.Fp, created, updated, d.Prev, d.CreatedBy, d.UpdatedBy);
    }

    /// <summary>Tokens of 3.9: none | usFederal | nyse.</summary>
    public static string CalToken(HolidayCalendar cal) => cal switch
    {
        HolidayCalendar.None => "none",
        HolidayCalendar.UsFederal => "usFederal",
        HolidayCalendar.Nyse => "nyse",
        _ => throw new ArgumentOutOfRangeException(nameof(cal)),
    };

    static bool TryCal(string token, bool allowNone, out HolidayCalendar cal)
    {
        cal = token switch
        {
            "none" when allowNone => HolidayCalendar.None,
            "usFederal" => HolidayCalendar.UsFederal,
            "nyse" => HolidayCalendar.Nyse,
            _ => (HolidayCalendar)(-1),
        };
        return (int)cal >= 0;
    }
}
