using System.Globalization;
using System.Text.Json;
using Cadence.Agent.Audit;
using Cadence.Core.Planning;
using Cadence.Core.History;
using Cadence.Scheduler;
using Cadence.TestHarness;
using static Cadence.IntegrationTests.Mcp.McpKit;

namespace Cadence.IntegrationTests.Mcp;

/// <summary>
/// Phase 22: <c>cadence_set_job_enabled</c>, <c>cadence_run_job_now</c> and <c>cadence_delete_job</c> through the REAL <c>Cadence.exe --mcp --folder CadenceTest</c> on real tasks in
/// \CadenceTest\ (harmless <c>cmd.exe</c> programs; the jobs are pinned to yesterday's date so only the explicit run-now starts anything). A real run goes through
/// <c>E2E.RunAndWaitUnlessBlocked</c>: when Windows refuses the launch (Defender, PLAN 10.2 Q6) that is reported as a KNOWN-ISSUE and never worked around.
/// </summary>
public static class McpWriteToolTests
{
    static string HistoryDir(string id) => Path.Combine(ApplyFx.LocalDataDir, "runs", id);

    public static void EnableDisableRunNowAndDeleteOnRealTasks() => Scenario(e =>
    {
        string foreignName = "Foreign bystander";
        e.Gw.Register(foreignName, Fx.Xml(Fx.FarTrigger));
        string foreignHash = TaskHash(e, foreignName);
        string cron = Yesterday();

        using McpTestClient c = Start();
        try
        {
            McpReply made = Call(c, "cadence_save_job", Create("Agent run job", cron));
            T.True("create: " + made.Text, !made.IsError);
            string id = made.Str("job", "jobId");
            string rev0 = made.Str("job", "revision");

            // ---- set enabled: off, unchanged, run refused while off, on ----
            McpReply off = Call(c, "cadence_set_job_enabled", Enable(id, false, rev0));
            T.True("disable: " + off.Text, !off.IsError && off.Str("outcome") == "updated");
            Fx.Info("SAMPLE cadence_set_job_enabled (real exe): " + Abbrev(off.Line, 900));
            T.Eq("disable: the view says disabled", false, off.Structured.GetProperty("job").GetProperty("enabled").GetBoolean());
            ClassifiedTask live = e.Jobs.Reclassify("Agent run job")!;
            T.True("disable: the stored job is disabled, in sync, and the task is disabled in Task Scheduler", live is { Sync: SyncStatus.InSync, Job.Enabled: false } && e.Gw.Get("Agent run job") is { Enabled: false });
            T.Eq("disable: updatedBy is the agent", "agent:Cadence Test Agent", live.Metadata!.UpdatedBy);
            T.Eq("disable: the revision in the answer is the live one", JobRevision.Of(live), off.Str("job", "revision"));
            string disabledHash = TaskHash(e, "Agent run job");

            McpReply same = Call(c, "cadence_set_job_enabled", Enable(id, false, "000000000000"));
            T.True("disable again: unchanged (decided before the revision)", !same.IsError && same.Str("outcome") == "unchanged", same.Text);
            T.Eq("disable again: the task is byte-identical", disabledHash, TaskHash(e, "Agent run job"));

            McpReply refusedRun = Call(c, "cadence_run_job_now", Id(id));
            T.True("run now while disabled: refused with the way out", refusedRun.IsError && refusedRun.Code == "not_allowed_in_this_state" && refusedRun.Message.Contains("Turn it on first with cadence_set_job_enabled", StringComparison.Ordinal), refusedRun.Text);

            McpReply on = Call(c, "cadence_set_job_enabled", Enable(id, true));
            T.True("enable: " + on.Text, !on.IsError && on.Str("outcome") == "updated" && on.Structured.GetProperty("job").GetProperty("enabled").GetBoolean());
            T.True("enable: enabled in Task Scheduler", e.Gw.Get("Agent run job") is { Enabled: true });

            // ---- run now: a real run, read back through the read tools ----
            ClassifiedTask toRun = e.Jobs.Reclassify("Agent run job")!;
            McpReply? started = null;
            IReadOnlyList<RunRecord>? recs = e.RunAndWaitUnlessBlocked(toRun, 1, TimeSpan.FromSeconds(90), "MCP write tools: run now",
                start: () => started = Call(c, "cadence_run_job_now", Id(id)));
            T.True("run now: answered started", started is { IsError: false } && started.Structured.GetProperty("started").GetBoolean() && started.Str("jobId") == id, started?.Text);
            Fx.Info("SAMPLE cadence_run_job_now (real exe): " + Abbrev(started!.Line, 700));
            T.Eq("run now: checkAfterSeconds", 5, started.Structured.GetProperty("checkAfterSeconds").GetInt32());
            McpReply again = Call(c, "cadence_run_job_now", Id(id));
            T.True("run now again within 30 s: rate_limited", again.IsError && again.Code == "rate_limited", again.Text);

            if (recs is null)
            {
                T.KnownIssue("MCP write tools: the real run, its record and its output were NOT verified",
                    "Windows refused to start the wrapper (LastTaskResult 0x80070005, no wrapper process, no run record): Windows Defender, see the README Safety notes (Checkpoint B). Not worked around on purpose.");
                e.WaitIdle("Agent run job", TimeSpan.FromSeconds(10));
                JsonElement blocked = Call(c, "cadence_get_job", Id(id)).Structured;
                T.Eq("blocked: get_job shows a didNotReport row", "didNotReport", blocked.GetProperty("recentRuns")[0].GetProperty("status").GetString());
                T.Eq("blocked: with the plain message", Cadence.Core.TaskXml.TaskResultCodes.AccessDeniedLaunch, blocked.GetProperty("recentRuns")[0].GetProperty("message").GetString());
            }
            else
            {
                e.WaitIdle("Agent run job", TimeSpan.FromSeconds(30));
                McpReply got = Call(c, "cadence_get_job", Id(id));
                JsonElement run = got.Structured.GetProperty("recentRuns")[0];
                T.Eq("run now: the run is in get_job", recs[0].RunId, run.GetProperty("runId").GetString());
                T.Eq("run now: status ok, exit code 0", "ok|0", run.GetProperty("status").GetString() + "|" + run.GetProperty("exitCode").GetInt32().ToString(CultureInfo.InvariantCulture));
                McpReply output = Call(c, "cadence_get_run_output", "{\"jobId\":\"" + id + "\",\"runId\":\"" + recs[0].RunId + "\"}");
                T.True("run now: the output is readable", !output.IsError && output.Text.Contains("agent-ok", StringComparison.Ordinal), output.Text);
            }

            // ---- a job that is running right now is refused (started by the owner's side, not the agent, so the per-job bucket is untouched) ----
            McpReply slow = Call(c, "cadence_save_job", Create("Agent slow job", cron, arguments: "/d /c \\\"ping -n 8 127.0.0.1 >nul\\\""));
            T.True("create the slow job: " + slow.Text, !slow.IsError);
            string slowId = slow.Str("job", "jobId");
            e.Gw.Run("Agent slow job");
            if (Fx.WaitFor(() => e.Gw.Get("Agent slow job") is { State: TaskState.Running }, TimeSpan.FromSeconds(20)))
            {
                McpReply busy = Call(c, "cadence_run_job_now", Id(slowId));
                T.True("run now while running: refused, 'already running'", busy.IsError && busy.Code == "not_allowed_in_this_state" && busy.Message.Contains("already running", StringComparison.Ordinal), busy.Text);
                e.WaitIdle("Agent slow job", TimeSpan.FromSeconds(40));
            }
            else
            {
                Fx.Info("the slow job did not start within 20 s (a launch refused by Windows is the known cause): the 'already running' refusal is covered by the unit tests only in this run");
                e.WaitIdle("Agent slow job", TimeSpan.FromSeconds(10));
            }

            // ---- delete: wrong name refused; right name deletes the task and ONLY its history; again = alreadyGone ----
            Directory.CreateDirectory(HistoryDir(id));
            File.WriteAllText(Path.Combine(HistoryDir(id), "marker.txt"), "history of the job that is about to be deleted");
            string slowHash = TaskHash(e, "Agent slow job");
            T.True("fixture: the slow job has a history folder too (it must survive the other delete)", Directory.CreateDirectory(HistoryDir(slowId)).Exists);

            McpReply wrong = Call(c, "cadence_delete_job", Delete(id, "Agent run"));
            T.True("delete with a wrong name: refused and names the real job", wrong.IsError && wrong.Code == "invalid_argument" && wrong.Message.StartsWith("jobId " + id + " is the job 'Agent run job', not 'Agent run'.", StringComparison.Ordinal), wrong.Text);
            T.True("delete with a wrong name: task and history still there", e.Gw.Get("Agent run job") is not null && Directory.Exists(HistoryDir(id)));
            T.True("delete with a wildcard name: refused too", Call(c, "cadence_delete_job", Delete(id, "*")).IsError && e.Gw.Get("Agent run job") is not null);

            McpReply del = Call(c, "cadence_delete_job", Delete(id, "Agent run job"));
            T.True("delete: " + del.Text, !del.IsError && del.Structured.GetProperty("deleted").GetBoolean() && del.Structured.GetProperty("historyRemoved").GetBoolean());
            Fx.Info("SAMPLE cadence_delete_job (real exe): " + Abbrev(del.Line, 500));
            T.True("delete: the task is gone from Task Scheduler", e.Gw.Get("Agent run job") is null);
            T.True("delete: its run-history folder is gone", !Directory.Exists(HistoryDir(id)));
            T.True("delete: the other job, its history and the foreign task are untouched", TaskHash(e, "Agent slow job") == slowHash && Directory.Exists(HistoryDir(slowId)) && TaskHash(e, foreignName) == foreignHash);
            McpReply gone = Call(c, "cadence_delete_job", Delete(id, "Agent run job"));
            T.True("delete again: alreadyGone, not an error", !gone.IsError && gone.Structured.GetProperty("alreadyGone").GetBoolean() && !gone.Structured.GetProperty("deleted").GetBoolean(), gone.Text);
            T.True("get_job of the deleted job: not_found", Call(c, "cadence_get_job", Id(id)) is { IsError: true } nf && nf.Code == "not_found");

            McpReply del2 = Call(c, "cadence_delete_job", Delete(slowId, "Agent slow job"));
            T.True("the second job deletes too", !del2.IsError && e.Gw.Get("Agent slow job") is null && !Directory.Exists(HistoryDir(slowId)));
            T.Eq("the foreign task is byte-identical", foreignHash, TaskHash(e, foreignName));
            T.Eq("no job is left", 0, e.Jobs.Load(false).Count);

            Finish(c, "write tools session");
        }
        finally { c.Kill(); }

        // ---- the audit file ----
        var log = new FileAuditLog(Folder);
        IReadOnlyList<AuditEntry> lines = [.. log.Tail(1000)];
        foreach ((string tool, string outcome, string code) in new[]
        {
            ("cadence_set_job_enabled", "ok", "disabled"), ("cadence_set_job_enabled", "ok", "unchanged"), ("cadence_set_job_enabled", "ok", "enabled"),
            ("cadence_run_job_now", "refused", "not_allowed_in_this_state"), ("cadence_run_job_now", "ok", "started"), ("cadence_run_job_now", "refused", "rate_limited"),
            ("cadence_delete_job", "refused", "invalid_argument"), ("cadence_delete_job", "ok", "deleted"), ("cadence_delete_job", "ok", "alreadyGone"),
        })
            T.True("audit has " + tool + " " + outcome + "/" + code, lines.Any(x => x.Tool == tool && x.Outcome == outcome && x.Code == code));
        T.True("every write line names the client", lines.Where(x => x.Tool is "cadence_set_job_enabled" or "cadence_run_job_now" or "cadence_delete_job").All(x => x.Client == "Cadence Test Agent"));
        T.True("the lines of a job carry its id and name", lines.Any(x => x.Tool == "cadence_delete_job" && x.Code == "deleted" && x.JobId is { Length: 8 } && x.JobName == "Agent run job"));
        string raw = File.ReadAllText(log.FilePath);
        T.True("the audit file holds no command line", !raw.Contains("agent-ok", StringComparison.Ordinal) && !raw.Contains("ping -n", StringComparison.Ordinal));
    });

    static string Abbrev(string s, int max) => s.Length <= max ? s : s[..max] + " ...(" + (s.Length - max).ToString(CultureInfo.InvariantCulture) + " more chars)";
}
