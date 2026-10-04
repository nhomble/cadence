using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Core.Cron;
using Cadence.Core.History;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.Core.Text;
using Cadence.History;
using Cadence.Scheduler;

namespace Cadence.Agent.Tools;

/// <summary>
/// The six read-only tools (PLAN 12.3.1). They list the managed folder only (<c>ManagedJobs.Load(includeForeign: false)</c>): foreign tasks have no
/// job id, so they can neither be listed nor fetched. Nothing here writes to Task Scheduler, the registry or the disk, and the preview never
/// touches the gateway at all.
/// </summary>
internal sealed class ReadTools
{
    /// <summary>How many lines of the run journal are searched for a run id (the wrapper keeps at most 4,000).</summary>
    const int JournalLines = 5000;

    /// <summary>
    /// [CP-E] <c>agents.howToResume</c> of <c>cadence_get_status</c>. It used to end at "if its permissions allow that tool", which read like an invitation; it now
    /// repeats the rule of the server instructions (resume only when the user clearly asked).
    /// </summary>
    public const string HowToResumeWithTool = "The owner resumes agents in the Cadence app or with Cadence.exe --agents resume. An agent may call cadence_resume_agents only when the user clearly asked it to (and its permissions allow that tool).";

    static readonly string[] Streams = ["stdout", "stderr"];
    static readonly string[] Calendars = ["none", "usFederal", "nyse"];

    readonly AgentEnvironment _env;
    readonly ManagedJobs _jobs;
    readonly HistoryService _history;
    readonly AgentPolicyGate _gate;
    readonly string[] _templateIds = [.. Templates.All.Select(t => t.Id)];

    public ReadTools(AgentEnvironment env)
    {
        _env = env;
        _jobs = new ManagedJobs(env.Gateway, env.Clock);
        _history = new HistoryService(env.Runs);
        _gate = new AgentPolicyGate(env.Policy, env.Zone);
    }

    DateTimeOffset Now => TimeZoneInfo.ConvertTime(_env.Clock(), _env.Zone);

    static ToolResult Ok(JsonObject node) => new(false, node.ToJsonString(), node);

    public static ToolResult Error(string code, string message, string? hint = null)
    {
        var err = new JsonObject { ["code"] = code, ["message"] = message };
        if (hint is not null) err["hint"] = hint;
        return new ToolResult(true, message, new JsonObject { ["error"] = err });
    }

    /// <summary>The managed jobs of the folder (foreign tasks are not returned). One folder listing per call.</summary>
    IReadOnlyList<ClassifiedTask> LoadManaged() => _jobs.Load(includeForeign: false);

    static ClassifiedTask? Find(IReadOnlyList<ClassifiedTask> all, string id) =>
        all.Where(c => c.Job is not null && string.Equals(c.Job.Id, id, StringComparison.Ordinal)).OrderBy(c => c.DuplicateOf is not null).FirstOrDefault();

    static ToolResult NotFound(string id) =>
        Error("not_found", "No Cadence job has the id " + id + ". List the jobs with cadence_list_jobs.");

    // ---- cadence_get_status ----

    public ToolResult Status(JsonElement arguments, CallContext context)
    {
        ArgReader.Open(arguments);
        IReadOnlyList<ClassifiedTask> managed = LoadManaged();
        // The policy is read again on every call (nothing cached); an unreadable setting reads as suspended with its reason (fail closed).
        AgentPolicy policy = _gate.Read();
        var agents = new JsonObject
        {
            ["suspended"] = policy.Suspended,
            ["suspendedAt"] = policy.Suspended && policy.SuspendedAt is { } at ? JobViews.Iso(at, _env.Zone) : null,
            ["suspendedBy"] = policy.Suspended ? policy.SuspendedBy : null,
            ["changedBy"] = policy.ChangedBy,
            ["changedAt"] = policy.ChangedAt is { } ca ? JobViews.Iso(ca, _env.Zone) : null,
            ["changedReason"] = policy.ChangedReason,
            ["howToResume"] = _env.Switch is null
                ? "The owner resumes agents in the Cadence app or with Cadence.exe --agents resume."
                : HowToResumeWithTool,
            ["minIntervalMinutes"] = policy.MinIntervalMinutes,
            ["maxAgentJobs"] = policy.MaxAgentJobs,
            ["agentJobs"] = AgentPolicyGate.CountAgentJobs(managed),
            ["resumedOutsideCadence"] = policy.ResumedOutsideCadence,
            ["readProblem"] = policy.ReadProblem,
        };
        return Ok(new JsonObject
        {
            ["cadenceVersion"] = _env.AppVersion,
            ["protocolVersion"] = context.ProtocolVersion,
            ["folder"] = "\\" + _env.Gateway.Options.FolderName,
            ["timeZone"] = _env.Zone.Id,
            ["now"] = JobViews.Iso(Now, _env.Zone),
            ["you"] = context.Client.DisplayName,
            ["agents"] = agents,
            ["jobs"] = managed.Count,
        });
    }

    // ---- cadence_list_jobs ----

    public ToolResult ListJobs(JsonElement arguments)
    {
        ArgReader r = ArgReader.Open(arguments, "nameContains");
        string? filter = r.String("nameContains", max: 100);
        DateTimeOffset now = Now;

        var rows = new JsonArray();
        IEnumerable<ClassifiedTask> all = LoadManaged().OrderBy(c => JobViews.DisplayName(c), StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Observed.Name, StringComparer.Ordinal);
        foreach (ClassifiedTask c in all)
        {
            if (!string.IsNullOrEmpty(filter) && JobViews.DisplayName(c).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add(JobViews.ListItem(c, _history.Load(c, RunHistory.DefaultWindow), now, _env.Zone));
        }
        return Ok(new JsonObject
        {
            ["folder"] = "\\" + _env.Gateway.Options.FolderName,
            ["count"] = rows.Count,
            ["jobs"] = rows,
        });
    }

    // ---- cadence_get_job ----

    public ToolResult GetJob(JsonElement arguments)
    {
        ArgReader r = ArgReader.Open(arguments, "jobId", "recentRuns");
        string id = r.String("jobId", required: true, pattern: ArgReader.JobIdPattern(), help: ArgReader.JobIdHelp)!;
        int recent = r.Int("recentRuns", 0, 50, 10);

        ClassifiedTask? c = Find(LoadManaged(), id);
        if (c is null) return NotFound(id);

        DateTimeOffset now = Now;
        JobHistory h = _history.Load(c, RunHistory.DefaultWindow);
        JsonObject view = JobViews.Full(c, h, now, _env.Zone);
        view["stats"] = JobViews.Stats(h);
        view["recentRuns"] = JobViews.RecentRuns(h, recent, now, _env.Zone);
        return Ok(view);
    }

    // ---- cadence_get_run_output ----

    public ToolResult GetRunOutput(JsonElement arguments)
    {
        ArgReader r = ArgReader.Open(arguments, "jobId", "runId", "stream", "maxChars");
        string id = r.String("jobId", required: true, pattern: ArgReader.JobIdPattern(), help: ArgReader.JobIdHelp)!;
        string runId = r.String("runId", required: true, pattern: ArgReader.RunIdPattern(), help: ArgReader.RunIdHelp)!;
        string stream = r.Choice("stream", Streams) ?? "stdout";
        int maxChars = r.Int("maxChars", 1, 65536, 8000);
        bool stderr = stream == "stderr";

        ClassifiedTask? c = Find(LoadManaged(), id);
        if (c is null) return NotFound(id);

        // The run must be one of THIS job's records: a run id of another job (or an invented one) is not found.
        RunRecord? record = _env.Runs.ReadTail(id, c.Observed.Path, JournalLines).FirstOrDefault(x => string.Equals(x.RunId, runId, StringComparison.Ordinal));
        if (record is null)
            return Error("not_found", "Job " + id + " has no run with the id " + runId + ". Take a runId from recentRuns in cadence_get_job.");

        string? text = _env.Runs.ReadOutput(id, runId, stderr);
        var result = new JsonObject { ["jobId"] = id, ["runId"] = runId, ["stream"] = stream };
        if (string.IsNullOrEmpty(text))
        {
            long recorded = stderr ? record.ErrBytes : record.OutBytes;
            result["text"] = "";
            result["truncated"] = false;
            result["totalChars"] = 0;
            result["note"] = recorded > 0
                ? "The " + stream + " of this run is no longer stored (Cadence prunes old output)."
                : "This run captured no output (visible jobs and skipped runs don't).";
            return Ok(result);
        }

        int total = text.Length;
        bool truncated = total > maxChars;
        if (truncated)
        {
            int start = total - maxChars;
            if (char.IsLowSurrogate(text[start])) start++;   // never start in the middle of a surrogate pair
            string tail = text[start..];
            text = "[truncated: showing the last " + tail.Length.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture)
                + " characters; ask for a larger maxChars (up to 65536) to see more]\n" + tail;
        }
        result["text"] = text;
        result["truncated"] = truncated;
        result["totalChars"] = total;
        return Ok(result);
    }

    // ---- cadence_preview_schedule ----

    public ToolResult PreviewSchedule(JsonElement arguments)
    {
        ArgReader r = ArgReader.Open(arguments, "cron", "templateId", "skipHolidays", "count");
        IReadOnlyList<string>? cron = r.StringArray("cron", 1, CronCompiler.MaxLines, 1, 200);
        string? templateId = r.Choice("templateId", _templateIds);
        string skipText = r.Choice("skipHolidays", Calendars) ?? "none";
        int count = r.Int("count", 1, 20, 8);
        if (cron is not null && templateId is not null)
            throw new ArgumentProblem("Give either cron or templateId, not both.", "Use cron for your own schedule, or templateId (see cadence_list_templates) for a ready-made one.");
        if (cron is null && templateId is null)
            throw new ArgumentProblem("Give cron (an array of cron lines) or templateId; neither was given.", "Example: {\"cron\":[\"0 18 * * 1-5\"]}. List the ready-made schedules with cadence_list_templates.");

        if (templateId is not null) cron = [Templates.All.First(t => t.Id == templateId).Cron];
        HolidayCalendar skip = skipText switch { "usFederal" => HolidayCalendar.UsFederal, "nyse" => HolidayCalendar.Nyse, _ => HolidayCalendar.None };

        DateTimeOffset now = Now;
        var spec = new ScheduleSpec(new ValueList<string>(cron!), skip, DateOnly.FromDateTime(now.DateTime));
        CompiledSchedule compiled = CronCompiler.Compile(spec, new MapContext("YOU"));

        var lines = new JsonArray();
        foreach (string line in cron!) lines.Add(line);

        var diags = new JsonArray();
        foreach (CronDiagnostic d in compiled.Diagnostics)
            diags.Add(new JsonObject { ["severity"] = d.Severity.ToString().ToLowerInvariant(), ["line"] = d.Line, ["field"] = d.Field, ["message"] = d.Message });

        var result = new JsonObject { ["valid"] = compiled.Success, ["cron"] = lines, ["skipHolidays"] = JobViews.CalendarName(skip) };
        if (!compiled.Success)
        {
            result["description"] = null;
            result["nextRuns"] = new JsonArray();
            result["diagnostics"] = diags;
            if (compiled.ClosestAlternative is { } alt)
            {
                var a = new JsonArray();
                foreach (string line in alt) a.Add(line);
                result["closestAlternative"] = a;
            }
            else result["closestAlternative"] = null;
            result["closestAlternativeExplanation"] = compiled.AlternativeExplanation;
            result["minimumGapMinutes"] = null;
            result["agentsMaySave"] = false;
            result["agentsMaySaveReason"] = "The schedule is not valid; fix the errors in diagnostics first.";
            return Ok(result);
        }

        var runs = new JsonArray();
        foreach (Occurrence o in NextRuns.Next(compiled.Triggers, skip, now, count, _env.Zone)) runs.Add(JobViews.NextRunItem(o, now, _env.Zone));

        // The owner's limit is read live (it can change between two calls); a policy that cannot be read still reports the default limit here.
        int minInterval = _gate.Read().MinIntervalMinutes;
        // [CP-D] The same judgement as the save tool: the 14-day gap, or a smaller one found later in the year (a schedule dense on some days only).
        TimeSpan? gap = ScheduleDensity.GapForLimit(compiled.Triggers, now, _env.Zone, TimeSpan.FromMinutes(minInterval));
        int? gapMinutes = gap is { } g ? AgentPolicyGate.GapMinutes(g) : null;
        bool mayAgentsSave = !AgentPolicyGate.IsTooFrequent(gap, minInterval);

        result["description"] = EnglishRenderer.Describe(compiled, skip);
        result["nextRuns"] = runs;
        result["diagnostics"] = diags;
        result["closestAlternative"] = null;
        result["minimumGapMinutes"] = gapMinutes;
        result["agentsMaySave"] = mayAgentsSave;
        result["agentsMaySaveReason"] = mayAgentsSave ? null
            : "This schedule runs as often as every " + gapMinutes!.Value.ToString(CultureInfo.InvariantCulture) + " minute(s); agents may schedule at most every "
              + minInterval.ToString(CultureInfo.InvariantCulture) + " minutes. Use a slower schedule, or ask the owner to change the limit (Cadence.exe --agents config --min-interval N).";
        if (runs.Count == 0)
            result["note"] = "This schedule has no clock times (it runs when the user signs in), so there are no next runs to list.";
        return Ok(result);
    }

    // ---- cadence_list_templates ----

    public ToolResult ListTemplates(JsonElement arguments)
    {
        ArgReader.Open(arguments);
        DateOnly today = DateOnly.FromDateTime(Now.DateTime);
        var rows = new JsonArray();
        foreach (Template t in Templates.All)
        {
            var spec = new ScheduleSpec(new ValueList<string>([t.Cron]), HolidayCalendar.None, today);
            rows.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["title"] = t.Title,
                ["cron"] = new JsonArray { t.Cron },
                ["description"] = EnglishRenderer.Describe(spec),
                ["suggestedName"] = t.SuggestedName,
                ["suggestedSkipHolidays"] = JobViews.CalendarName(t.SuggestedSkip),
            });
        }
        return Ok(new JsonObject { ["templates"] = rows });
    }
}
