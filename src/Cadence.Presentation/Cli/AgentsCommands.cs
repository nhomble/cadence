using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Core.Metadata;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;

namespace Cadence.App.Cli;

/// <summary>Everything <c>--agents</c> needs, injected so tests use an in-memory store and log and a fake gateway.</summary>
public sealed class AgentsEnv(CliEnv cli, IAgentPolicyStore store, IAuditLog audit, Func<string?> ancestors, string? session = null)
{
    public CliEnv Cli { get; } = cli;
    public IAgentPolicyStore Store { get; } = store;
    public IAuditLog Audit { get; } = audit;

    /// <summary>The ancestor process names of this process ("cmd.exe &lt; svchost.exe"), or null when unknown.</summary>
    public Func<string?> Ancestors { get; } = ancestors;

    public string Session { get; } = session ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}

/// <summary>
/// <c>--agents status|suspend|resume|config|log|lock|unlock</c> (PLAN 12.6). These commands are the owner's side of the agent controls: they are the
/// only code (besides the app) that holds the policy STORE (the limits). <c>suspend</c> and <c>resume</c> are plain, distinct commands ([OWNER-Q14]: agents can
/// do the same through two distinct MCP tools, so a permission layer can treat that one action separately); both record who and from where ([CP-R2]).
/// Every change is also an audit line.
/// </summary>
public static class AgentsCommands
{
    public static int Execute(CliCommand cmd, AgentsEnv env)
    {
        try
        {
            return cmd.Verb switch
            {
                "status" => Status(cmd.Json, env),
                "suspend" => Suspend(env),
                "resume" => Resume(env),
                "config" => Config(cmd.MinInterval, cmd.MaxJobs, env),
                "log" => Log(cmd.Max, cmd.Json, env),
                "lock" => Lock(cmd.Target!, locked: true, env),
                "unlock" => Lock(cmd.Target!, locked: false, env),
                _ => Fail(env, "Not an --agents command: " + cmd.Verb),
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Fail(env, "Cadence could not change the agent settings (" + ex.GetType().Name + "). Nothing was changed.");
        }
        catch (SchedulerException ex) { return Fail(env, "Task Scheduler: " + ex.Message); }
    }

    // ---- status ----

    static int Status(bool json, AgentsEnv env)
    {
        AgentPolicy p = new AgentPolicyGate(env.Store, env.Cli.Zone).Read();
        int? agentJobs = CountAgentJobs(env);
        if (json)
        {
            var o = new JsonObject
            {
                ["folder"] = env.Cli.FolderPath,
                ["suspended"] = p.Suspended,
                ["suspendedAt"] = p.Suspended && p.SuspendedAt is { } sa ? AgentPolicy.Iso(sa) : null,
                ["suspendedBy"] = p.Suspended ? p.SuspendedBy : null,
                ["failedClosed"] = p.FailedClosed,
                ["readProblem"] = p.ReadProblem,
                ["resumedOutsideCadence"] = p.ResumedOutsideCadence,
                ["changedBy"] = p.ChangedBy,
                ["changedAt"] = p.ChangedAt is { } ca ? AgentPolicy.Iso(ca) : null,
                ["changedFrom"] = p.ChangedFrom,
                ["minIntervalMinutes"] = p.MinIntervalMinutes,
                ["maxAgentJobs"] = p.MaxAgentJobs,
                ["agentJobs"] = agentJobs,
                ["auditLog"] = env.Audit.FilePath,
            };
            env.Cli.Raw(o.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\r\n");
            return 0;
        }

        env.Cli.Line("AI agents on this PC (" + env.Cli.FolderPath + ")");
        if (p.FailedClosed)
            env.Cli.Line("  State:             SUSPENDED (safety): the agent settings could not be read (" + Plain(p.ReadProblem) + "), so agents are refused.");
        else if (p.Suspended)
            env.Cli.Line("  State:             SUSPENDED" + SinceText(p, env.Cli.Zone) + ". Agents can read schedules but cannot change, run or delete them.");
        else env.Cli.Line("  State:             allowed. Agents can change schedules within the limits below.");
        if (p.ResumedOutsideCadence)
            env.Cli.Line("  Warning:           AI agents were turned back on outside Cadence (the switch was cleared without Cadence). Run --agents suspend if you did not do this.");
        if (!p.FailedClosed && p.ReadProblem is not null) env.Cli.Line("  Note:              " + Plain(p.ReadProblem));
        env.Cli.Line("  Shortest schedule: every " + Minutes(p.MinIntervalMinutes) + (p.MinIntervalMinutes == AgentPolicy.DefaultMinIntervalMinutes ? " (default)" : ""));
        env.Cli.Line("  Most agent jobs:   " + p.MaxAgentJobs.ToString(CultureInfo.InvariantCulture) + (p.MaxAgentJobs == AgentPolicy.DefaultMaxAgentJobs ? " (default)" : ""));
        env.Cli.Line("  Agent jobs now:    " + (agentJobs is { } n ? n.ToString(CultureInfo.InvariantCulture) : "unknown (Task Scheduler could not be read)"));
        if (p.ChangedAt is { } at)
            env.Cli.Line("  Last switch:       " + Local(at, env.Cli.Zone) + (p.ChangedBy is null ? "" : " by " + WhoText(p.ChangedBy))
                + (p.ChangedFrom is null ? "" : " (from " + Plain(p.ChangedFrom) + ")") + (p.ChangedReason is null ? "" : ", reason: " + Plain(p.ChangedReason)));
        env.Cli.Line("  Audit log:         " + env.Audit.FilePath);
        return 0;
    }

    static int? CountAgentJobs(AgentsEnv env)
    {
        try { return AgentPolicyGate.CountAgentJobs(new ManagedJobs(env.Cli.Gateway, env.Cli.Clock).Load(false)); }
        catch (SchedulerException) { return null; }
    }

    // ---- suspend / resume ----

    static int Suspend(AgentsEnv env)
    {
        AgentPolicy p = new AgentPolicyGate(env.Store, env.Cli.Zone).Read();
        if (p.Suspended && !p.FailedClosed)
        {
            env.Cli.Line("AI agents are already suspended" + SinceText(p, env.Cli.Zone) + ". Nothing changed.");
            return 0;
        }
        DateTimeOffset now = env.Cli.Clock();
        env.Store.SetSuspended(true, "cli", now);
        Audit(env, "suspended", "cli", null, null, null, From(env, "Suspended from the command line"));
        env.Cli.Line("AI agents are suspended on this PC (" + env.Cli.FolderPath + "). They can still read your schedules but cannot change, run or delete them, starting with their next request.");
        env.Cli.Line("Resume them with: Cadence.exe --agents resume" + FolderArg(env));
        return 0;
    }

    static int Resume(AgentsEnv env)
    {
        AgentPolicy p = new AgentPolicyGate(env.Store, env.Cli.Zone).Read();
        // A switch that could not be read counts as suspended; resuming then writes a valid value, which also repairs it.
        if (!p.Suspended)
        {
            env.Cli.Line("AI agents are not suspended. Nothing to resume.");
            return 0;
        }
        string? from = env.Ancestors();
        env.Store.SetSuspended(false, "cli", env.Cli.Clock(), from);
        Audit(env, "resumed", "cli", null, null, null, Join("Resumed from the command line", FromSuffix(from)));
        env.Cli.Line("AI agents may change your schedules again (" + env.Cli.FolderPath + "), starting with their next request.");
        return 0;
    }

    // ---- config ----

    static int Config(int? minInterval, int? maxJobs, AgentsEnv env)
    {
        AgentPolicy before = new AgentPolicyGate(env.Store, env.Cli.Zone).Read();
        env.Store.SetLimits(minInterval, maxJobs);
        var parts = new List<string>();
        if (minInterval is { } m) parts.Add("shortest schedule every " + Minutes(before.MinIntervalMinutes) + " -> every " + Minutes(m));
        if (maxJobs is { } j) parts.Add("most agent jobs " + before.MaxAgentJobs.ToString(CultureInfo.InvariantCulture) + " -> " + j.ToString(CultureInfo.InvariantCulture));
        string text = string.Join("; ", parts);
        Audit(env, "ok", "cli", null, null, "limits_changed", From(env, "Limits changed: " + text));
        env.Cli.Line("OK: " + text + " (" + env.Cli.FolderPath + ").");
        return 0;
    }

    // ---- log ----

    static int Log(int max, bool json, AgentsEnv env)
    {
        IReadOnlyList<AuditEntry> entries = env.Audit.Tail(max);
        if (entries.Count == 0)
        {
            if (!json) env.Cli.Line("No agent log entries yet (" + env.Audit.FilePath + ").");
            return 0;
        }
        foreach (AuditEntry e in entries)
        {
            if (json)
            {
                env.Cli.Raw(Encoding.UTF8.GetString(FileAuditLog.Serialize(e)).Replace("\n", "\r\n", StringComparison.Ordinal));
                continue;
            }
            var sb = new StringBuilder();
            sb.Append(e.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ")
              .Append(AuditText.Escape(e.Outcome).PadRight(12)).Append(' ').Append(AuditText.Escape(e.Tool).PadRight(22)).Append(' ').Append(AuditText.Escape(e.Client));
            if (e.JobId is not null) sb.Append("  job ").Append(AuditText.Escape(e.JobId));
            if (e.JobName is not null) sb.Append(" \"").Append(AuditText.Escape(e.JobName)).Append('"');
            if (e.Code is not null) sb.Append("  [").Append(AuditText.Escape(e.Code)).Append(']');
            if (e.Detail.Length > 0) sb.Append("  - ").Append(AuditText.Escape(e.Detail));
            if (e.Count > 1) sb.Append("  x").Append(e.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            env.Cli.Line(sb.ToString());
        }
        return 0;
    }

    // ---- lock / unlock ----

    static int Lock(string id, bool locked, AgentsEnv env)
    {
        if (!JobId.IsValid(id)) return Fail(env, "'" + id + "' is not a job id (8 characters of a-z and 0-9). List the ids with: Cadence.exe --probe" + FolderArg(env));
        var jobs = new ManagedJobs(env.Cli.Gateway, env.Cli.Clock, Actor.Cli);
        ClassifiedTask? c = jobs.Load(false).Where(x => x.Job is not null && x.Job.Id == id).OrderBy(x => x.DuplicateOf is not null).FirstOrDefault();
        if (c?.Job is not { } job) return Fail(env, "No job with id " + id + " in " + env.Cli.FolderPath + ".");

        // A save writes Cadence's version of the job: on a job somebody edited in Task Scheduler that would silently undo their edit.
        if (c.Ownership != Ownership.Managed || c.Sync == SyncStatus.Drifted)
            return Fail(env, "Job " + id + " was changed outside Cadence (or is a copy), so saving the lock would overwrite that change. Review it in the Cadence app first (Re-apply or Stop managing), then lock it.");

        string verb = locked ? "locked" : "unlocked";
        if (job.AgentLock == locked)
        {
            // Nothing to do: no save, no new "updated" stamp, nothing written to Task Scheduler.
            env.Cli.Line("Job " + id + " is already " + verb + " for AI agents. Nothing changed.");
            return 0;
        }

        // The normal save path: Planner -> Applier (live ownership check, writer lock, verify), attributed to the command line.
        ApplyResult r = new Applier(jobs).Save(job with { AgentLock = locked }, c.Observed.Name);
        if (!r.Success)
        {
            env.Cli.Line("ERROR: " + r.Message);
            return 1;
        }
        Audit(env, "ok", "cli", id, job.Name, locked ? "locked" : "unlocked", From(env, locked ? "Locked against AI agents" : "Unlocked for AI agents"));
        env.Cli.Line("OK: job " + id + " (" + job.Name + ") is now " + verb + (locked ? ": AI agents cannot change, run or delete it." : ": AI agents may change it again."));
        return 0;
    }

    // ---- helpers ----

    static void Audit(AgentsEnv env, string outcome, string tool, string? jobId, string? jobName, string? code, string detail)
    {
        string? warning = env.Audit.TryAppend(new AuditEntry(env.Cli.Clock(), env.Session, Actor.Cli.Describe(), null, tool, jobId, jobName, outcome, code, detail));
        if (warning is not null) env.Cli.Line("WARNING: " + warning);
    }

    static string? FromSuffix(string? ancestors) => string.IsNullOrWhiteSpace(ancestors) ? null : "from " + ancestors;

    static string From(AgentsEnv env, string text) => Join(text, FromSuffix(env.Ancestors()));

    static string Join(string text, string? suffix) => suffix is null ? text : text + " (" + suffix + ")";

    static string FolderArg(AgentsEnv env) => env.Cli.Gateway.Options.FolderName == Cadence.Scheduler.ManagedFolder.Product ? "" : " --folder " + env.Cli.Gateway.Options.FolderName;

    static string Minutes(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " minute" : " minutes");

    static string Local(DateTimeOffset t, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(t, zone).ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    static string SinceText(AgentPolicy p, TimeZoneInfo zone)
    {
        if (p.SuspendedAt is not { } at) return "";
        return " since " + Local(at, zone) + (p.SuspendedBy switch
        {
            "app" => " (from the Cadence app)",
            "cli" => " (from the command line)",
            null => "",
            var by => " (by " + WhoText(by) + ")",
        });
    }

    /// <summary>"the Cadence app", "the command line" or the agent's name (marked as an AI agent).</summary>
    static string WhoText(string token)
    {
        Actor a = Actor.Parse(token);
        return a.Kind == ActorKind.Agent ? "AI agent " + a.Describe() : a.Describe();
    }

    /// <summary>Text that came from the registry, shown without any control character.</summary>
    static string Plain(string? text) => AuditText.Escape(text);

    static int Fail(AgentsEnv env, string message)
    {
        env.Cli.Line("ERROR: " + message);
        return 1;
    }
}
