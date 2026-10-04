using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Core.Cron;
using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.Core.Text;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;

namespace Cadence.Agent.Tools;

/// <summary>
/// <c>cadence_save_job</c> (PLAN 12.3.2): creates a job (no <c>jobId</c>) or changes one (with <c>jobId</c> and <c>expectedRevision</c>), through the
/// <see cref="WritePipeline"/> only. The rules, in the order of the plan: arguments (invalid_argument), suspension, rate limit, live lookup with the state and lock
/// checks (all in <see cref="WritePipeline.Begin"/>), then this tool's own rules (program, schedule, density, name, cap), the plan, and the write with the second
/// suspension check inside the writer lock. An identical save reports <c>unchanged</c> BEFORE the revision is looked at, so a retry of a save that went through is a
/// success and never a duplicate or a stale error. The job's lock, settings, anchor and source path are always the live ones: an agent cannot send or change them.
/// </summary>
internal sealed class SaveJobTool
{
    const string Tool = ToolCatalog.SaveJob;
    const int DefaultTimeoutMinutes = 60;   // an agent default, deliberately stricter than the app's "none"

    static readonly string[] Calendars = ["none", "usFederal", "nyse"];
    static readonly string[] Encodings = ["oem", "utf8"];
    readonly string[] _templateIds = [.. Templates.All.Select(t => t.Id)];

    readonly WritePipeline _w;
    readonly AgentEnvironment _env;

    public SaveJobTool(WritePipeline pipeline)
    {
        _w = pipeline;
        _env = pipeline.Env;
    }

    sealed record ScheduleInput(IReadOnlyList<string>? Cron, string? TemplateId, string? SkipHolidays)
    {
        public bool SetsLines => Cron is not null || TemplateId is not null;
    }

    sealed record ProgramInput(string? Path, string? Arguments, string? WorkingFolder, IReadOnlyList<KeyValuePair<string, string>>? Environment, int? TimeoutMinutes, bool? Hidden, string? Encoding)
    {
        public ProgramRules.Given AsGiven() => new(Path, Arguments, WorkingFolder, Environment);
    }

    sealed record Request(string? JobId, string? ExpectedRevision, string? Name, ScheduleInput? Schedule, ProgramInput? Program, bool? Enabled);

    public ToolResult Call(JsonElement arguments, CallContext context)
    {
        Request req;
        try { req = Parse(arguments); }
        catch (ArgumentProblem p) { return _w.Invalid(Tool, context, p); }

        WriteGate gate = _w.Begin(Tool, context, req.JobId);
        if (gate.Refusal is { } refused) return refused;
        try { return req.JobId is null ? Create(gate, req) : Update(gate, req); }
        catch (Exception ex) when (ex is SchedulerException or IOException) { return _w.Fault(gate, ex); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _w.AuditInternal(gate, ex);
            throw;
        }
    }

    // ---- gate 1: the arguments ----

    Request Parse(JsonElement arguments)
    {
        ArgReader r = ArgReader.Open(arguments, "jobId", "expectedRevision", "name", "schedule", "program", "enabled");
        string? jobId = r.String("jobId", pattern: ArgReader.JobIdPattern(), help: ArgReader.JobIdHelp);
        string? revision = r.String("expectedRevision", pattern: ArgReader.RevisionPattern(), help: "must be 12 lowercase hex characters, the revision from cadence_get_job");
        string? name = r.String("name", min: 1, max: 100);
        bool? enabled = r.Bool("enabled");

        ScheduleInput? schedule = null;
        if (r.Child("schedule", "cron", "templateId", "skipHolidays") is { } s)
        {
            IReadOnlyList<string>? cron = s.StringArray("cron", 1, CronCompiler.MaxLines, 1, 200);
            string? templateId = s.Choice("templateId", _templateIds);
            string? skip = s.Choice("skipHolidays", Calendars);
            if (cron is not null && templateId is not null)
                throw new ArgumentProblem("schedule: give either cron or templateId, not both.", "Use cron for your own schedule, or templateId (see cadence_list_templates) for a ready-made one.");
            schedule = new ScheduleInput(cron, templateId, skip);
        }

        ProgramInput? program = null;
        if (r.Child("program", "path", "arguments", "workingFolder", "environment", "timeoutMinutes", "hidden", "outputEncoding") is { } p)
        {
            program = new ProgramInput(
                p.String("path", min: 4, max: 1024),
                p.String("arguments", max: AgentInputRules.MaxArgumentsLength),
                p.String("workingFolder", max: 1024),
                p.StringMap("environment"),
                p.OptionalInt("timeoutMinutes", 0, MetadataCodec.MaxTimeoutSeconds / 60),
                p.Bool("hidden"),
                p.Choice("outputEncoding", Encodings));
        }

        if (jobId is null)
        {
            if (revision is not null)
                throw new ArgumentProblem("expectedRevision is only for changing a job (together with jobId).", "To create a job, leave out jobId and expectedRevision.");
            if (name is null) throw new ArgumentProblem("name is required when creating a job.", "Give the job a display name, for example \"Backup notes\".");
            if (schedule is null || (!schedule.SetsLines)) throw new ArgumentProblem("schedule is required when creating a job: give schedule.cron or schedule.templateId.", "Example: {\"schedule\":{\"cron\":[\"0 18 * * 1-5\"]}}. Check it first with cadence_preview_schedule.");
            if (program?.Path is null) throw new ArgumentProblem("program.path is required when creating a job.", "Give the full path of an existing .exe, for example C:\\Windows\\System32\\cmd.exe.");
        }
        else if (revision is null)
        {
            throw new ArgumentProblem("expectedRevision is required when changing a job.", "Read the job with cadence_get_job and pass its revision as expectedRevision.");
        }
        if (name is not null && name.Trim().Length == 0) throw new ArgumentProblem("name must contain at least one visible character.");
        return new Request(jobId, revision, name?.Trim(), schedule, program, enabled);
    }

    // ---- create ----

    ToolResult Create(WriteGate gate, Request req)
    {
        string name = req.Name!;
        gate.JobName = name;
        AgentPolicy policy = gate.Policy!;
        ProgramInput prog = req.Program!;

        if (NameProblem(name) is { } badName) return _w.Refuse(gate, "invalid_argument", badName);

        var run = new RunSpec
        {
            Exe = prog.Path!,
            Args = prog.Arguments ?? "",
            WorkingDirectory = prog.WorkingFolder ?? ProgramRules.FolderOf(prog.Path!),
            Env = new ValueList<KeyValuePair<string, string>>(prog.Environment ?? []),
            Timeout = Timeout(prog.TimeoutMinutes ?? DefaultTimeoutMinutes),
            Hidden = prog.Hidden ?? true,
            Encoding = prog.Encoding == "utf8" ? OutputEncoding.Utf8 : OutputEncoding.Oem,
        };
        if (ProgramRules.Check(prog.AsGiven(), run, _env.Files, _env.OwnExePath) is { } rejected) return _w.Refuse(gate, "program_rejected", rejected);

        ScheduleInput sch = req.Schedule!;
        ValueList<string> lines = LinesOf(sch)!;
        HolidayCalendar skip = Calendar(sch.SkipHolidays);
        DateTimeOffset now = _w.Now;
        var spec = new ScheduleSpec(lines, skip, DateOnly.FromDateTime(now.DateTime));
        if (CheckSchedule(gate, spec, policy, setsSchedule: true, out CompiledSchedule? compiled) is { } scheduleRefusal) return scheduleRefusal;

        var job = new JobDefinition
        {
            Id = JobId.New(),
            Name = name,
            Schedule = spec,
            Run = run,
            Enabled = req.Enabled ?? true,
        };

        // A job with this name already exists: the same request is a retry (success), a different one is a name clash. Before the cap, so a retry at the cap succeeds.
        ClassifiedTask? same = gate.Managed.Where(c => c.Job is not null && c.DuplicateOf is null && SameName(c.Job.Name, name)).FirstOrDefault();
        if (same is not null)
        {
            if (Equivalent(same.Job!, job))
            {
                gate.JobId = same.Job!.Id;
                var node = new JsonObject
                {
                    ["outcome"] = "unchanged",
                    ["message"] = "A job with this name and these settings already exists; nothing was created.",
                    ["note"] = "A job with this name and these settings already exists; nothing was created.",
                    ["job"] = _w.View(same),
                };
                return _w.Unchanged(gate, Warn(node, compiled!, run), "A job with this name and these settings already exists");
            }
            gate.JobId = same.Job!.Id;
            return _w.Refuse(gate, "name_taken", "Another job is already named '" + AuditText.Clean(same.Job.Name, 100) + "' (jobId " + same.Job.Id + "). Choose a different name, or change that job: call cadence_get_job with its id and then cadence_save_job with jobId and expectedRevision.");
        }

        if (_w.CreateRefusal(gate) is { } capped) return _w.Refuse(gate, capped.Code, capped.Message);

        PlannedChange plan;
        try { plan = gate.Jobs!.PlanSave(job, null); }
        catch (ComposeException ex) { return ComposeRefusal(gate, ex); }
        catch (PlanRefusedException ex) { return PlanRefusal(gate, ex); }

        gate.JobId = job.Id;
        return _w.Commit(gate, plan,
            (apply, after) => Shape(plan, apply, after, compiled!, run),
            extraGuard: () => NameGuard(gate, name));
    }

    /// <summary>
    /// Inside the writer lock: the name is checked once more against the live folder, so two agents creating the same name at once can't both succeed; and
    /// [CP-D] the cap on agent-created jobs is counted again there too, so two agents (two server processes) creating at once can't both pass a count of 19 of 20.
    /// </summary>
    PolicyRefusal? NameGuard(WriteGate gate, string name)
    {
        IReadOnlyList<ClassifiedTask> live = gate.Jobs!.Load(includeForeign: false);
        ClassifiedTask? clash = live.FirstOrDefault(c => c.Job is not null && c.DuplicateOf is null && SameName(c.Job.Name, name));
        if (clash is not null)
            return new PolicyRefusal("name_taken", "Another job is already named '" + AuditText.Clean(clash.Job!.Name, 100) + "' (jobId " + clash.Job.Id + "). Nothing was created.");
        return AgentPolicyGate.CheckCreate(AgentPolicyGate.CountAgentJobs(live), gate.Policy!);
    }

    // ---- update ----

    ToolResult Update(WriteGate gate, Request req)
    {
        ClassifiedTask live = gate.Live!;
        JobDefinition cur = live.Job!;
        AgentPolicy policy = gate.Policy!;
        string name = req.Name ?? cur.Name;
        ProgramInput? prog = req.Program;

        if (req.Name is not null && NameProblem(name) is { } badName) return _w.Refuse(gate, "invalid_argument", badName);
        if (req.Name is not null && !string.Equals(name, cur.Name, StringComparison.Ordinal))
        {
            ClassifiedTask? clash = gate.Managed.FirstOrDefault(c => c.Job is not null && c.DuplicateOf is null && c.Job.Id != cur.Id && SameName(c.Job.Name, name));
            if (clash is not null)
                return _w.Refuse(gate, "name_taken", "Another job is already named '" + AuditText.Clean(clash.Job!.Name, 100) + "' (jobId " + clash.Job.Id + "). Choose a different name.");
        }

        RunSpec run = cur.Run;
        if (prog is not null)
        {
            run = cur.Run with
            {
                Exe = prog.Path ?? cur.Run.Exe,
                Args = prog.Arguments ?? cur.Run.Args,
                WorkingDirectory = prog.WorkingFolder ?? cur.Run.WorkingDirectory,
                Env = prog.Environment is { } env ? new ValueList<KeyValuePair<string, string>>(env) : cur.Run.Env,
                Timeout = prog.TimeoutMinutes is { } t ? Timeout(t) : cur.Run.Timeout,
                Hidden = prog.Hidden ?? cur.Run.Hidden,
                Encoding = prog.Encoding is { } e ? (e == "utf8" ? OutputEncoding.Utf8 : OutputEncoding.Oem) : cur.Run.Encoding,
            };
            if (ProgramRules.Check(prog.AsGiven(), run, _env.Files, _env.OwnExePath) is { } rejected) return _w.Refuse(gate, "program_rejected", rejected);
        }

        ScheduleInput? sch = req.Schedule;
        ScheduleSpec spec = cur.Schedule;
        bool setsSchedule = sch is { SetsLines: true };
        if (sch is not null)
            spec = cur.Schedule with { CronLines = LinesOf(sch) ?? cur.Schedule.CronLines, SkipHolidays = sch.SkipHolidays is null ? cur.Schedule.SkipHolidays : Calendar(sch.SkipHolidays) };
        if (CheckSchedule(gate, spec, policy, setsSchedule, out CompiledSchedule? compiled) is { } scheduleRefusal) return scheduleRefusal;

        // Lock, settings, anchor and source path are the job's own (with): an agent can't send or change them. The Scheduler layer enforces the lock again.
        JobDefinition edited = cur with { Name = name, Schedule = spec, Run = run, Enabled = req.Enabled ?? cur.Enabled };

        PlannedChange plan;
        try { plan = gate.Jobs!.PlanSave(edited, live.Observed.Name, req.ExpectedRevision); }
        catch (ComposeException ex) { return ComposeRefusal(gate, ex); }
        catch (PlanRefusedException ex) { return PlanRefusal(gate, ex); }

        if (plan.Kind == ChangeKind.Update && plan.NewXml is null)
        {
            var node = new JsonObject
            {
                ["outcome"] = "unchanged",
                ["message"] = "Nothing to change: the job already has these settings.",
                ["changes"] = plan.Summary,
                ["job"] = _w.View(live),
            };
            return _w.Unchanged(gate, Warn(node, compiled!, run), "Nothing to change");
        }
        return _w.Commit(gate, plan, (apply, after) => Shape(plan, apply, after, compiled!, run));
    }

    // ---- shared ----

    ToolResult? CheckSchedule(WriteGate gate, ScheduleSpec spec, AgentPolicy policy, bool setsSchedule, out CompiledSchedule? compiled) =>
        _w.CheckSchedule(gate, spec, policy, setsSchedule, out compiled);

    ToolResult ComposeRefusal(WriteGate gate, ComposeException ex) =>
        ex.Diagnostics.Count > 0
            ? _w.Refuse(gate, "schedule_invalid", ex.Message + " Check it with cadence_preview_schedule.")
            : _w.Refuse(gate, "invalid_argument", ex.Message);

    ToolResult PlanRefusal(WriteGate gate, PlanRefusedException ex) => _w.RefusePlan(gate, ex);

    /// <summary>The success result (12.3.2): outcome, the Applier's message, the plan's summary, the verified JobView and the warnings.</summary>
    JsonObject Shape(PlannedChange plan, ApplyResult apply, ClassifiedTask? after, CompiledSchedule compiled, RunSpec run)
    {
        string outcome = plan.Kind switch
        {
            ChangeKind.Create => "created",
            ChangeKind.Rename => "renamed",
            ChangeKind.Upgrade => "refreshed",
            _ => "updated",
        };
        var node = new JsonObject
        {
            ["outcome"] = outcome,
            ["message"] = apply.Message,
            ["changes"] = plan.Summary,
            ["job"] = after is { Job: not null } ? _w.View(after) : null,
        };
        return Warn(node, compiled, run);
    }

    JsonObject Warn(JsonObject node, CompiledSchedule compiled, RunSpec run)
    {
        var warnings = new JsonArray();
        foreach (string w in ProgramRules.Warnings(run)) warnings.Add(w);
        if (compiled.Triggers.Any(t => t is LogonTrigger)) warnings.Add("@logon runs the job only when the user signs in, not at a clock time.");
        foreach (CronDiagnostic d in compiled.Diagnostics)
            if (d.Severity != CronSeverity.Error && !d.Message.Contains("represented exactly with", StringComparison.Ordinal))
                warnings.Add(d.Message);
        if (warnings.Count > 0) node["warnings"] = warnings;
        return node;
    }

    static string? NameProblem(string name)
    {
        if (name.Length is < 1 or > 100) return "name must be 1 to 100 characters.";
        // TaskNaming turns a name with nothing usable in it into the task name "job"; such a name would be indistinguishable from any other.
        bool anything = name.Any(char.IsLetterOrDigit);
        if (!anything) return "name must contain at least one letter or digit, not only punctuation.";
        return null;
    }

    static bool SameName(string a, string b) => string.Equals(NameKey(a), NameKey(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// [CP-E] What two names are compared by: Unicode compatibility form (NFKC: "e" + combining accent equals "é", fullwidth letters equal ASCII), invisible format
    /// characters (zero-width space, bidi marks) and control characters dropped, any run of whitespace (also a no-break space) as one space, trimmed. Before, an
    /// agent could create or rename a job to "Owner&#160;Backup" (no-break space) or "Owner&#8203; Backup" next to the owner's "Owner Backup": a second job that looks
    /// exactly the same in the list. Look-alike letters from other scripts (a Cyrillic "е") are not folded; that is a known limit.
    /// </summary>
    internal static string NameKey(string name)
    {
        string n;
        try { n = name.Normalize(System.Text.NormalizationForm.FormKC); }
        catch (ArgumentException) { n = name; }   // a lone surrogate: compare the text as it is
        var sb = new System.Text.StringBuilder(n.Length);
        bool space = false;
        foreach (char c in n)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    static TimeSpan? Timeout(int minutes) => minutes <= 0 ? null : TimeSpan.FromMinutes(minutes);

    static HolidayCalendar Calendar(string? name) => name switch
    {
        "usFederal" => HolidayCalendar.UsFederal,
        "nyse" => HolidayCalendar.Nyse,
        _ => HolidayCalendar.None,
    };

    ValueList<string>? LinesOf(ScheduleInput s)
    {
        // [CP-E] Each line is stored in its plain form (trimmed, one space between fields): "  0   3 * * *  " is the same schedule as "0 3 * * *", so
        // it must not be written as a change of the schedule, nor shown back to the owner with the agent's stray spaces.
        if (s.Cron is { } cron) return new ValueList<string>([.. cron.Select(CronText)]);
        if (s.TemplateId is { } id) return new ValueList<string>([Templates.All.First(t => t.Id == id).Cron]);
        return null;
    }

    /// <summary>A cron line with its fields separated by single spaces and no spaces around it (whitespace only separates cron fields).</summary>
    internal static string CronText(string line) =>
        string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Same request as the existing job (a retried create): name, cron lines, holidays, program, arguments, folder, environment, timeout, window, encoding, enabled.</summary>
    static bool Equivalent(JobDefinition existing, JobDefinition wanted)
    {
        if (existing.Enabled != wanted.Enabled) return false;
        if (existing.Schedule.SkipHolidays != wanted.Schedule.SkipHolidays) return false;
        if (!existing.Schedule.CronLines.SequenceEqual(wanted.Schedule.CronLines, StringComparer.Ordinal)) return false;
        RunSpec a = existing.Run, b = wanted.Run;
        if (a.Exe != b.Exe || a.Args != b.Args || a.WorkingDirectory != b.WorkingDirectory || a.Timeout != b.Timeout || a.Hidden != b.Hidden || a.Encoding != b.Encoding) return false;
        if (a.Env.Count != b.Env.Count) return false;
        var left = a.Env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var right = b.Env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < left.Count; i++)
            if (!string.Equals(left[i].Key, right[i].Key, StringComparison.OrdinalIgnoreCase) || left[i].Value != right[i].Value) return false;
        return true;
    }
}
