using System.Text;
using System.Text.Json;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.App.Cli;
using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// Phase 20 (PLAN 12.6, [CP-R1], [CP-R2]): <c>--agents</c> parsing for every form and error, and the commands over an in-memory policy, an in-memory audit
/// log and a fake gateway. The real registry is covered by the integration tests.
/// </summary>
public static class AgentsCommandsTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 2, 0, TimeSpan.Zero);

    // ---- parsing ----

    static CliCommand Ok(params string[] args)
    {
        CliParseResult r = CliParser.Parse(args);
        if (r.Command is not { } c) throw new InvalidOperationException("expected a command for '" + string.Join(' ', args) + "' but got: " + r.Error);
        return c;
    }

    static string Err(params string[] args)
    {
        CliParseResult r = CliParser.Parse(args);
        return r.Error ?? "(parsed)";
    }

    public static void EveryFormParses()
    {
        CliCommand c = Ok("--agents", "status");
        T.True("status", c.Action == CliAction.Agents && c.Verb == "status" && !c.Json && c.Folder == "Cadence");
        c = Ok("--agents", "status", "--json", "--folder", "CadenceTest");
        T.True("status --json --folder", c.Verb == "status" && c.Json && c.Folder == "CadenceTest");
        c = Ok("--agents", "suspend", "--folder", "CadenceTest");
        T.True("suspend", c.Verb == "suspend" && !c.Yes);
        c = Ok("--agents", "resume", "--folder", "CadenceTest");
        T.True("resume is a plain command ([OWNER-Q14]: no --yes, no window)", c.Verb == "resume" && !c.Yes);
        c = Ok("--agents", "resume", "--yes", "--folder", "CadenceTest");
        T.True("an old --yes is accepted and ignored by resume", c.Verb == "resume");
        c = Ok("--agents", "config", "--min-interval", "15", "--max-jobs", "3");
        T.True("config both", c.Verb == "config" && c.MinInterval == 15 && c.MaxJobs == 3);
        c = Ok("--agents", "config", "--min-interval=1440");
        T.True("config min only, inline value", c.MinInterval == 1440 && c.MaxJobs is null);
        c = Ok("--agents", "config", "--max-jobs", "0");
        T.True("config max only, zero is valid", c.MaxJobs == 0 && c.MinInterval is null);
        c = Ok("--agents", "log");
        T.True("log default", c.Verb == "log" && c.Max == 20 && !c.Json);
        c = Ok("--agents", "log", "--max", "500", "--json");
        T.True("log --max --json", c.Max == 500 && c.Json);
        c = Ok("--agents", "lock", "a1b2c3d4", "--yes");
        T.True("lock", c.Verb == "lock" && c.Target == "a1b2c3d4" && c.Yes);
        c = Ok("--agents", "unlock", "a1b2c3d4", "--yes", "--folder", "CadenceTest");
        T.True("unlock", c.Verb == "unlock" && c.Target == "a1b2c3d4");
        c = Ok("--AGENTS", "STATUS");
        T.True("case-insensitive", c.Verb == "status");
        c = Ok("--agents", "status", "--folder", "cadencetest");
        T.Eq("folder is normalised", "CadenceTest", c.Folder);
    }

    public static void EveryErrorIsAUsageError()
    {
        T.True("no verb", Err("--agents").Contains("needs one of: status, suspend, resume, config, log, lock, unlock", StringComparison.Ordinal));
        T.True("unknown verb", Err("--agents", "enable").Contains("unknown command 'enable'", StringComparison.Ordinal));
        T.True("resume needs no --yes", Err("--agents", "resume") == "(parsed)");
        T.True("lock without --yes is refused", Err("--agents", "lock", "a1b2c3d4").Contains("add --yes", StringComparison.Ordinal));
        T.True("unlock without --yes is refused", Err("--agents", "unlock", "a1b2c3d4").Contains("add --yes", StringComparison.Ordinal));
        T.True("lock needs a job id", Err("--agents", "lock", "--yes").Contains("needs a job id", StringComparison.Ordinal));
        T.True("unlock needs a job id", Err("--agents", "unlock", "--yes").Contains("needs a job id", StringComparison.Ordinal));
        T.True("lock takes one job id", Err("--agents", "lock", "a1b2c3d4", "x", "--yes").Contains("Unexpected argument 'x'", StringComparison.Ordinal));
        T.True("status takes no argument", Err("--agents", "status", "x").Contains("Unexpected argument 'x'", StringComparison.Ordinal));
        T.True("config needs a limit", Err("--agents", "config").Contains("needs --min-interval N and/or --max-jobs N", StringComparison.Ordinal));
        T.True("--min-interval 0 is refused", Err("--agents", "config", "--min-interval", "0").Contains("from 1 to 1440", StringComparison.Ordinal));
        T.True("--min-interval 1441 is refused", Err("--agents", "config", "--min-interval", "1441").Contains("from 1 to 1440", StringComparison.Ordinal));
        T.True("--min-interval text is refused", Err("--agents", "config", "--min-interval", "five").Contains("from 1 to 1440", StringComparison.Ordinal));
        T.True("--min-interval negative is refused", Err("--agents", "config", "--min-interval", "-5").Contains("from 1 to 1440", StringComparison.Ordinal));
        T.True("--min-interval needs a value", Err("--agents", "config", "--min-interval").Contains("needs a number of minutes", StringComparison.Ordinal));
        T.True("--max-jobs 501 is refused", Err("--agents", "config", "--max-jobs", "501").Contains("from 0 to 500", StringComparison.Ordinal));
        T.True("--max-jobs -1 is refused", Err("--agents", "config", "--max-jobs", "-1").Contains("from 0 to 500", StringComparison.Ordinal));
        T.True("--max-jobs needs a value", Err("--agents", "config", "--max-jobs").Contains("needs a number", StringComparison.Ordinal));
        T.True("--min-interval twice", Err("--agents", "config", "--min-interval", "5", "--min-interval", "6").Contains("given twice", StringComparison.Ordinal));
        T.True("--max-jobs twice", Err("--agents", "config", "--max-jobs", "5", "--max-jobs", "6").Contains("given twice", StringComparison.Ordinal));
        T.True("limits only with config", Err("--agents", "status", "--min-interval", "5").Contains("only valid with --agents config", StringComparison.Ordinal));
        T.True("limits are not valid with other switches", Err("--probe", "--max-jobs", "5").Contains("only valid with --agents config", StringComparison.Ordinal));
        T.True("--max only with log", Err("--agents", "status", "--max", "5").Contains("only valid with --agents log", StringComparison.Ordinal));
        T.True("--max range for log", Err("--agents", "log", "--max", "501").Contains("from 1 to 500", StringComparison.Ordinal));
        T.True("--max 0 for log", Err("--agents", "log", "--max", "0").Contains("from 1 to 500", StringComparison.Ordinal));
        T.True("--json only with status and log", Err("--agents", "suspend", "--json").Contains("only valid with --agents status and --agents log", StringComparison.Ordinal));
        T.True("--yes is not valid with suspend", Err("--agents", "suspend", "--yes").Contains("--yes is only valid", StringComparison.Ordinal));
        T.True("--yes is not valid with status", Err("--agents", "status", "--yes").Contains("--yes is only valid", StringComparison.Ordinal));
        T.True("--yes is not valid with config", Err("--agents", "config", "--max-jobs", "1", "--yes").Contains("--yes is only valid", StringComparison.Ordinal));
        T.True("--yes is not valid with log", Err("--agents", "log", "--yes").Contains("--yes is only valid", StringComparison.Ordinal));
        T.True("--demo is not a switch", Err("--agents", "status", "--demo").Contains("Unknown switch", StringComparison.Ordinal));
        T.True("--all does not apply", Err("--agents", "status", "--all").Contains("does not apply", StringComparison.Ordinal));
        T.True("--out is not a switch", Err("--agents", "status", "--out", "x.png").Contains("Unknown switch", StringComparison.Ordinal));
        T.True("a bad folder is refused", Err("--agents", "status", "--folder", "Microsoft").Contains("Cadence or CadenceTest", StringComparison.Ordinal));
        T.True("only one command", Err("--agents", "status", "--probe").Contains("Only one command per run", StringComparison.Ordinal));
        T.True("--agents=status is not a form", Err("--agents=status").Contains("Unknown switch", StringComparison.Ordinal));
        // The other switches still refuse the new flags and keep their own rules.
        T.True("--yes still only with the writing switches", Err("--history", "a1b2c3d4", "--yes").Contains("--yes is only valid", StringComparison.Ordinal));
        T.True("--max still works with --history", Ok("--history", "a1b2c3d4", "--max", "5").Max == 5);
        T.True("--json still works with --history", Ok("--history", "a1b2c3d4", "--json").Json);
    }

    public static void TheUsageTextListsEveryAgentsForm()
    {
        string u = CliParser.Usage();
        foreach (string form in new[] { "--agents status", "--agents suspend", "--agents resume", "--agents config", "--min-interval", "--max-jobs", "--agents log", "--agents lock <jobId> --yes", "--agents unlock <jobId> --yes" })
            T.True("usage lists " + form, u.Contains(form, StringComparison.Ordinal));
        T.Eq("name of --agents", "--agents", CliParser.Name(CliAction.Agents));
    }

    // ---- the commands ----

    sealed class Rig : IDisposable
    {
        public readonly MemoryAgentPolicyStore Store = new();
        public readonly MemoryAuditLog Audit = new();
        public readonly FakeGateway Gateway = new("CadenceTest");
        public readonly StringBuilder Out = new();
        public readonly string RunsRoot = Path.Combine(Path.GetTempPath(), "cadence-agentscmd-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset Clock = Now;
        public string? Ancestors = "cmd.exe < svchost.exe";

        public (int Rc, string Text) Run(params string[] args)
        {
            CliCommand cmd = Ok(args);
            Out.Clear();
            var cli = new CliEnv(Gateway, new RunRecordStore(RunsRoot), () => Clock, TimeZoneInfo.Utc, t => Out.Append(t));
            var env = new AgentsEnv(cli, Store, Audit, () => Ancestors, "sess0001");
            int rc = AgentsCommands.Execute(cmd, env);
            return (rc, Out.ToString());
        }

        public void Dispose()
        {
            try { if (Directory.Exists(RunsRoot)) Directory.Delete(RunsRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
        }
    }

    public static void StatusText()
    {
        using var rig = new Rig();
        var (rc, text) = rig.Run("--agents", "status", "--folder", "CadenceTest");
        T.Eq("exit", 0, rc);
        T.True("names the folder", text.StartsWith("AI agents on this PC (\\CadenceTest)\r\n", StringComparison.Ordinal));
        T.True("allowed", text.Contains("State:             allowed.", StringComparison.Ordinal));
        T.True("default limits marked", text.Contains("Shortest schedule: every 5 minutes (default)", StringComparison.Ordinal) && text.Contains("Most agent jobs:   20 (default)", StringComparison.Ordinal));
        T.True("agent job count", text.Contains("Agent jobs now:    0", StringComparison.Ordinal));
        T.True("audit log path", text.Contains("Audit log:         (memory)", StringComparison.Ordinal));
        T.True("no warning when nothing is wrong", !text.Contains("Warning", StringComparison.Ordinal) && !text.Contains("Note:", StringComparison.Ordinal));
        T.True("CRLF only", !text.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal));

        rig.Store.SetSuspended(true, "cli", Now);
        rig.Store.SetLimits(15, 3);
        (rc, text) = rig.Run("--agents", "status", "--folder", "CadenceTest");
        T.True("suspended since, from the command line", text.Contains("SUSPENDED since Thu 1 Oct 2026 14:02 (from the command line)", StringComparison.Ordinal));
        T.True("limits are shown without the default mark", text.Contains("every 15 minutes\r\n", StringComparison.Ordinal) && text.Contains("Most agent jobs:   3\r\n", StringComparison.Ordinal));
        T.True("last switch", text.Contains("Last switch:       Thu 1 Oct 2026 14:02 by the command line", StringComparison.Ordinal));

        rig.Store.Fault = "the registry key Software\\CadenceTest\\Agents could not be read (IOException)";
        (rc, text) = rig.Run("--agents", "status", "--folder", "CadenceTest");
        T.True("failed closed says so", text.Contains("SUSPENDED (safety): the agent settings could not be read", StringComparison.Ordinal) && text.Contains("so agents are refused", StringComparison.Ordinal));
        rig.Store.Fault = null;

        rig.Store.SetSuspended(true, "app", Now);
        rig.Store.ClearSuspendedOutsideCadence();
        (rc, text) = rig.Run("--agents", "status", "--folder", "CadenceTest");
        T.True("an outside resume is a warning", text.Contains("Warning:           AI agents were turned back on outside Cadence", StringComparison.Ordinal) && text.Contains("allowed.", StringComparison.Ordinal));

        rig.Store.SetSuspended(false, "cli", Now.AddMinutes(1), "cmd.exe < svchost.exe");
        (rc, text) = rig.Run("--agents", "status", "--folder", "CadenceTest");
        T.True("a Cadence resume shows no warning and its source", !text.Contains("Warning", StringComparison.Ordinal) && text.Contains("(from cmd.exe < svchost.exe)", StringComparison.Ordinal));
    }

    public static void StatusJson()
    {
        using var rig = new Rig();
        rig.Gateway.Seed("Agent job", ComposeAs(PlanSamples.Job("Agent job", "agentjb1", "0 8 * * *"), "Agent job", Actor.Agent("Claude Code")));
        rig.Gateway.Seed("Owner job", ComposeAs(PlanSamples.Job("Owner job", "ownerjb1", "0 8 * * *"), "Owner job", Actor.App));
        var (rc, text) = rig.Run("--agents", "status", "--json", "--folder", "CadenceTest");
        T.Eq("exit", 0, rc);
        T.True("one line of JSON", text.EndsWith("\r\n", StringComparison.Ordinal) && text.TrimEnd().IndexOf('\n', StringComparison.Ordinal) < 0);
        using JsonDocument d = JsonDocument.Parse(text);
        JsonElement r = d.RootElement;
        T.Eq("folder", "\\CadenceTest", r.GetProperty("folder").GetString());
        T.Eq("suspended", false, r.GetProperty("suspended").GetBoolean());
        T.True("no since, no by", r.GetProperty("suspendedAt").ValueKind == JsonValueKind.Null && r.GetProperty("suspendedBy").ValueKind == JsonValueKind.Null);
        T.Eq("min interval", 5, r.GetProperty("minIntervalMinutes").GetInt32());
        T.Eq("max jobs", 20, r.GetProperty("maxAgentJobs").GetInt32());
        T.Eq("one agent job", 1, r.GetProperty("agentJobs").GetInt32());
        T.Eq("resumedOutsideCadence", false, r.GetProperty("resumedOutsideCadence").GetBoolean());
        T.Eq("failedClosed", false, r.GetProperty("failedClosed").GetBoolean());
        T.Eq("audit log", "(memory)", r.GetProperty("auditLog").GetString());

        rig.Store.SetSuspended(true, "app", Now);
        (_, text) = rig.Run("--agents", "status", "--json", "--folder", "CadenceTest");
        using JsonDocument d2 = JsonDocument.Parse(text);
        T.True("suspended with since and by", d2.RootElement.GetProperty("suspended").GetBoolean() && d2.RootElement.GetProperty("suspendedAt").GetString() == "2026-10-01T14:02:00+00:00" && d2.RootElement.GetProperty("suspendedBy").GetString() == "app");
        rig.Store.Fault = "boom\nnewline";
        (_, text) = rig.Run("--agents", "status", "--json", "--folder", "CadenceTest");
        using JsonDocument d3 = JsonDocument.Parse(text);
        T.True("failed closed in JSON, one line even with a newline in the reason", d3.RootElement.GetProperty("failedClosed").GetBoolean() && text.TrimEnd().IndexOf('\n', StringComparison.Ordinal) < 0);
    }

    public static void SuspendIsAPlainSwitchThatIsAudited()
    {
        using var rig = new Rig();
        var (rc, text) = rig.Run("--agents", "suspend", "--folder", "CadenceTest");
        T.Eq("exit", 0, rc);
        T.True("suspended, by the cli, at the clock", rig.Store.Read() is { Suspended: true, SuspendedBy: "cli", ChangedBy: "cli" } p && p.SuspendedAt == Now);
        T.True("tells what it means and how to resume", text.Contains("suspended on this PC", StringComparison.Ordinal) && text.Contains("Cadence.exe --agents resume --folder CadenceTest", StringComparison.Ordinal) && !text.Contains("--yes", StringComparison.Ordinal));
        AuditEntry e = rig.Audit.All.Single();
        T.True("one audit line: suspended, tool cli, with the parent process names", e.Outcome == "suspended" && e.Tool == "cli" && e.Client == "the command line" && e.Detail.Contains("from cmd.exe < svchost.exe", StringComparison.Ordinal) && e.Session == "sess0001" && e.At == Now);

        rig.Clock = Now.AddMinutes(9);
        (rc, text) = rig.Run("--agents", "suspend", "--folder", "CadenceTest");
        T.True("suspending again changes nothing and says so", rc == 0 && text.Contains("already suspended", StringComparison.Ordinal) && rig.Store.Read().SuspendedAt == Now && rig.Audit.All.Count == 1);

        var noParent = new Rig { Ancestors = null };
        noParent.Run("--agents", "suspend");
        T.True("an unknown parent is just left out", noParent.Audit.All.Single().Detail == "Suspended from the command line");
        noParent.Dispose();
    }

    public static void ResumeIsAPlainAuditedCommand()
    {
        // [OWNER-Q14] No confirmation window: resume is a distinct, attributed, audited command (agents have the same action as a distinct tool).
        using var rig = new Rig();
        rig.Store.SetSuspended(true, "app", Now);
        rig.Clock = Now.AddMinutes(5);

        var (rc, text) = rig.Run("--agents", "resume", "--folder", "CadenceTest");
        T.Eq("exit 0", 0, rc);
        AgentPolicy p = rig.Store.Read();
        T.True("resumed", !p.Suspended && p.ChangedBy == "cli" && p.ChangedAt == Now.AddMinutes(5));
        T.Eq("the ancestor process names are recorded ([CP-R2])", "cmd.exe < svchost.exe", p.ChangedFrom);
        T.True("a resume through Cadence is not 'outside Cadence'", !p.ResumedOutsideCadence);
        T.True("tells the owner", text.Contains("may change your schedules again", StringComparison.Ordinal));
        AuditEntry resumed = rig.Audit.All.Single();
        T.True("audited as resumed with the parent names", resumed.Outcome == "resumed" && resumed.Tool == "cli" && resumed.Detail.Contains("from cmd.exe < svchost.exe", StringComparison.Ordinal));

        // An agent that suspended the switch is shown as such, and the owner can resume it the same way.
        rig.Store.SetSuspended(true, "agent:Claude Code", Now.AddMinutes(10), null, "stopping while I check something");
        (_, text) = rig.Run("--agents", "status", "--folder", "CadenceTest");
        T.True("status names the agent and marks it as an AI agent", text.Contains("(by AI agent Claude Code)", StringComparison.Ordinal) && text.Contains("reason: stopping while I check something", StringComparison.Ordinal));
        (_, text) = rig.Run("--agents", "resume");
        T.True("the owner resumes it", !rig.Store.Read().Suspended);
    }

    public static void ResumeWithoutAnythingToResumeChangesNothing()
    {
        using var rig = new Rig();
        var (rc, text) = rig.Run("--agents", "resume", "--folder", "CadenceTest");
        T.True("not suspended: exit 0, nothing to resume, nothing audited", rc == 0 && text.Contains("not suspended", StringComparison.Ordinal) && rig.Audit.All.Count == 0);

        // An unreadable setting (fail closed) is repaired by a resume, which writes a valid value.
        var fixedStore = new MemoryAgentPolicyStore(AgentPolicy.Unreadable("Suspended is 2 (it must be 0 or 1)"));
        var cli = new CliEnv(rig.Gateway, new RunRecordStore(rig.RunsRoot), () => Now, TimeZoneInfo.Utc, t => rig.Out.Append(t));
        var env = new AgentsEnv(cli, fixedStore, rig.Audit, () => null, "sess0001");
        int rc2 = AgentsCommands.Execute(Ok("--agents", "resume", "--folder", "CadenceTest"), env);
        T.True("a failed-closed setting is repaired by resume", rc2 == 0 && !fixedStore.Read().Suspended);
    }

    public static void ConfigChangesTheLimitsAndIsAudited()
    {
        using var rig = new Rig();
        var (rc, text) = rig.Run("--agents", "config", "--min-interval", "15", "--max-jobs", "3", "--folder", "CadenceTest");
        T.Eq("exit", 0, rc);
        T.True("both set", rig.Store.Read() is { MinIntervalMinutes: 15, MaxAgentJobs: 3 });
        T.True("says what changed", text.Contains("shortest schedule every 5 minutes -> every 15 minutes", StringComparison.Ordinal) && text.Contains("most agent jobs 20 -> 3", StringComparison.Ordinal));
        AuditEntry e = rig.Audit.All.Single();
        T.True("audited as ok / limits_changed", e.Outcome == "ok" && e.Code == "limits_changed" && e.Tool == "cli" && e.Detail.Contains("20 -> 3", StringComparison.Ordinal));
        rig.Run("--agents", "config", "--max-jobs", "0");
        T.True("one limit leaves the other", rig.Store.Read() is { MinIntervalMinutes: 15, MaxAgentJobs: 0 });
    }

    public static void LogPrintsTheNewestLinesWithControlCharactersEscaped()
    {
        using var rig = new Rig();
        var (rc, text) = rig.Run("--agents", "log", "--folder", "CadenceTest");
        T.True("an empty log says so", rc == 0 && text.Contains("No agent log entries yet", StringComparison.Ordinal));
        (_, text) = rig.Run("--agents", "log", "--json");
        T.Eq("an empty log prints nothing in JSON", "", text);

        for (int i = 0; i < 30; i++)
            rig.Audit.Append(new AuditEntry(Now.AddMinutes(i), "s", "Claude Code", null, "cadence_save_job", "x7k2m9qa", "Backup notes", i % 2 == 0 ? "ok" : "refused", i % 2 == 0 ? null : "stale_revision", "entry " + i));
        (rc, text) = rig.Run("--agents", "log");
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        T.Eq("the default is the newest 20", 20, lines.Length);
        T.True("the newest is last", lines[^1].Contains("entry 29", StringComparison.Ordinal) && lines[0].Contains("entry 10", StringComparison.Ordinal));
        T.True("a line has time, outcome, tool, client, job, code and detail", lines[^1].StartsWith("2026-10-01 14:31:00  refused", StringComparison.Ordinal) && lines[^1].Contains("cadence_save_job", StringComparison.Ordinal) && lines[^1].Contains("Claude Code", StringComparison.Ordinal)
            && lines[^1].Contains("job x7k2m9qa \"Backup notes\"", StringComparison.Ordinal) && lines[^1].Contains("[stale_revision]", StringComparison.Ordinal) && lines[^1].Contains("- entry 29", StringComparison.Ordinal));
        (_, text) = rig.Run("--agents", "log", "--max", "3");
        T.Eq("--max 3", 3, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        (_, text) = rig.Run("--agents", "log", "--max", "2", "--json");
        string[] json = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        T.Eq("--json prints one JSON object per line", 2, json.Length);
        foreach (string l in json) { using JsonDocument d = JsonDocument.Parse(l); T.True("each parses with v:1", d.RootElement.GetProperty("v").GetInt32() == 1); }
        T.True("the lines end with CRLF only", !text.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal));

        // A hand-made log with control characters in a field: the text output shows escapes, not actions.
        var evil = new Rig();
        evil.Audit.Append(new AuditEntry(Now, "s", "Evil", null, "t", null, null, "ok", null, "ok"));
        // MemoryAuditLog cleans on append, so make a log that returns raw control characters to prove the printer escapes them too.
        var raw = new RawLog([new AuditEntry(Now, "s", "cli\u001b[2Jent", null, "tool\u0007", "id\n1", "na\rme", "ok\t", "co\u0000de", "de\ntail" + (char)0x2028 + "x")]);
        var cli = new CliEnv(evil.Gateway, new RunRecordStore(evil.RunsRoot), () => Now, TimeZoneInfo.Utc, t => evil.Out.Append(t));
        AgentsCommands.Execute(Ok("--agents", "log"), new AgentsEnv(cli, evil.Store, raw, () => null));
        string printed = evil.Out.ToString();
        T.True("no raw escape, bell, NUL, tab or line break inside the entry's line", !printed.Contains('\u001b', StringComparison.Ordinal) && !printed.Contains('\u0007', StringComparison.Ordinal) && !printed.Contains('\0', StringComparison.Ordinal) && !printed.Contains('\t', StringComparison.Ordinal) && !printed.Contains((char)0x2028, StringComparison.Ordinal));
        T.Eq("exactly one line for the one entry", 1, printed.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        T.True("the escapes are visible text", printed.Contains("\\u001b", StringComparison.Ordinal) && printed.Contains("\\n", StringComparison.Ordinal) && printed.Contains("\\u2028", StringComparison.Ordinal));
        evil.Dispose();
    }

    sealed class RawLog(IReadOnlyList<AuditEntry> entries) : IAuditLog
    {
        public string FilePath => "(raw)";
        public void Append(AuditEntry entry) { }
        public IReadOnlyList<AuditEntry> Tail(int max) => entries;
    }

    // ---- lock / unlock ----

    static void SeedJob(Rig rig, string id, string name, bool locked = false, Actor? creator = null)
    {
        JobDefinition job = PlanSamples.Job(name, id, "0 8 * * *") with { AgentLock = locked };
        rig.Gateway.Seed(name, ComposeAs(job, name, creator ?? Actor.App));
    }

    /// <summary>The task XML for the test folder (the gateway of the rig is \CadenceTest, so the composed wrapper path must match or the Classifier sees drift).</summary>
    static string ComposeAs(JobDefinition job, string name, Actor actor) =>
        TaskComposer.ComposeXml(job, new ComposeContext("CadenceTest", "CadenceTest", ComposeSamples.User, name, ComposeSamples.Now, null, ValueList<string>.Empty) with { Actor = actor });

    public static void LockAndUnlockAreSavesByTheCommandLine()
    {
        using var rig = new Rig();
        SeedJob(rig, "lockjob1", "Lock me");
        string before = rig.Gateway.XmlOf("Lock me")!;
        T.True("starts unlocked", PlanSamples.Meta(before).Job.AgentLock == false);

        var (rc, text) = rig.Run("--agents", "lock", "lockjob1", "--yes", "--folder", "CadenceTest");
        T.Eq("lock: exit", 0, rc);
        T.True("lock: says so", text.Contains("is now locked", StringComparison.Ordinal));
        CadenceMetadata m = PlanSamples.Meta(rig.Gateway.XmlOf("Lock me")!);
        T.True("agentLock is stored", m.Job.AgentLock);
        T.Eq("updatedBy is cli", "cli", m.UpdatedBy);
        T.Eq("createdBy is kept (the app)", "app", m.CreatedBy);
        T.Eq("exactly one write", 1, rig.Gateway.Writes.Count(w => w.StartsWith("register", StringComparison.Ordinal)));
        T.True("the audit line names the job", rig.Audit.All.Single() is { Outcome: "ok", Code: "locked", JobId: "lockjob1", JobName: "Lock me", Tool: "cli" });
        T.True("the lock does not change the schedule or the program", PlanSamples.Meta(before).Job with { AgentLock = true } == m.Job);

        int writes = rig.Gateway.Writes.Count;
        string xml = rig.Gateway.XmlOf("Lock me")!;
        (rc, text) = rig.Run("--agents", "lock", "lockjob1", "--yes");
        T.True("locking a locked job is a no-op", rc == 0 && text.Contains("already locked", StringComparison.Ordinal));
        T.True("a no-op writes nothing (no register, same XML, no audit line)", rig.Gateway.Writes.Count == writes && rig.Gateway.XmlOf("Lock me") == xml && rig.Audit.All.Count == 1);

        (rc, text) = rig.Run("--agents", "unlock", "lockjob1", "--yes");
        T.True("unlock: exit 0 and says so", rc == 0 && text.Contains("is now unlocked", StringComparison.Ordinal));
        T.True("agentLock is cleared", !PlanSamples.Meta(rig.Gateway.XmlOf("Lock me")!).Job.AgentLock);
        T.True("and the field is omitted again", !PlanSamples.Read(rig.Gateway.XmlOf("Lock me")!).RegistrationInfo.Documentation!.Contains("agentLock", StringComparison.Ordinal));
        writes = rig.Gateway.Writes.Count;
        (rc, text) = rig.Run("--agents", "unlock", "lockjob1", "--yes");
        T.True("unlocking an unlocked job is a no-op", rc == 0 && text.Contains("already unlocked", StringComparison.Ordinal) && rig.Gateway.Writes.Count == writes);
    }

    public static void LockRefusesWhatItCannotFindOrMayNotTouch()
    {
        using var rig = new Rig();
        SeedJob(rig, "lockjob1", "Lock me");
        rig.Gateway.Seed("Not ours", PlanSamples.Plain());
        var (rc, text) = rig.Run("--agents", "lock", "nosuchid", "--yes");
        T.True("no such job: exit 1", rc == 1 && text.Contains("No job with id nosuchid", StringComparison.Ordinal));
        (rc, text) = rig.Run("--agents", "lock", "BAD ID", "--yes");
        T.True("not a job id: exit 1 and says how to list them", rc == 1 && text.Contains("is not a job id", StringComparison.Ordinal));
        T.True("a foreign task has no id and is never touched", rig.Gateway.Writes.Count == 0);

        // A job someone edited in Task Scheduler (drifted) is not saved over.
        string drifted = PlanSamples.Edit(rig.Gateway.XmlOf("Lock me")!, m => m with { Settings = m.Settings with { Enabled = !m.Settings.Enabled } });
        rig.Gateway.Seed("Lock me", drifted);
        (rc, text) = rig.Run("--agents", "lock", "lockjob1", "--yes");
        T.True("a drifted job is refused (a save would overwrite the human's edit)", rc == 1 && text.StartsWith("ERROR: Job lockjob1 was changed outside Cadence", StringComparison.Ordinal));
        T.True("nothing was written, nothing audited", rig.Gateway.Writes.Count == 0 && rig.Audit.All.Count == 0 && rig.Gateway.XmlOf("Lock me") == drifted);
    }

    public static void ARegistryFailureIsAnErrorNotACrash()
    {
        using var rig = new Rig();
        var failing = new ThrowingStore();
        var cli = new CliEnv(rig.Gateway, new RunRecordStore(rig.RunsRoot), () => Now, TimeZoneInfo.Utc, t => rig.Out.Append(t));
        var env = new AgentsEnv(cli, failing, rig.Audit, () => null);
        int rc = AgentsCommands.Execute(Ok("--agents", "suspend"), env);
        T.True("exit 1 with a plain message", rc == 1 && rig.Out.ToString().StartsWith("ERROR: Cadence could not change the agent settings (UnauthorizedAccessException)", StringComparison.Ordinal));
        T.Eq("nothing audited when nothing changed", 0, rig.Audit.All.Count);

        rig.Out.Clear();
        rig.Audit.ThrowOnAppend = new IOException("disk full");
        rc = AgentsCommands.Execute(Ok("--agents", "suspend"), new AgentsEnv(cli, rig.Store, rig.Audit, () => null));
        T.True("an audit failure is a warning, the suspension still happened", rc == 0 && rig.Store.Read().Suspended && rig.Out.ToString().Contains("WARNING: The agent log could not be written", StringComparison.Ordinal));
    }

    sealed class ThrowingStore : IAgentPolicyStore
    {
        public AgentPolicy Read() => AgentPolicy.Defaults;
        public void SetSuspended(bool suspended, string by, DateTimeOffset at, string? changedFrom = null, string? reason = null) => throw new UnauthorizedAccessException("denied");
        public void SetLimits(int? minIntervalMinutes, int? maxAgentJobs) => throw new UnauthorizedAccessException("denied");
    }

    public static void ConfigAndStatusUseTheRealStoreRange()
    {
        // The parse rules and the store rules are the same numbers (no off-by-one between the CLI and the registry).
        T.True("CLI accepts the store's lowest and highest", Ok("--agents", "config", "--min-interval", "1", "--max-jobs", "0").MinInterval == 1 && Ok("--agents", "config", "--min-interval", "1440", "--max-jobs", "500").MaxJobs == 500);
        T.True("CLI refuses one beyond", Err("--agents", "config", "--min-interval", "1441").Length > 0 && Err("--agents", "config", "--max-jobs", "501").Length > 0 && Err("--agents", "config", "--min-interval", "0").Length > 0);
    }
}
