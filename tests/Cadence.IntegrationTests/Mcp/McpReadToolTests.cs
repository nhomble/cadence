using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests.Mcp;

/// <summary>
/// Phase 18: the read-only tools of the REAL <c>Cadence.exe --mcp --folder CadenceTest</c>, spawned and driven over pipes, against a real
/// \CadenceTest\ holding two managed jobs (made in-process through the Applier) and one raw foreign task. Jobs are dated 2099 and run only
/// when the test starts them on purpose (<c>cmd /c echo</c>). A run that Windows refuses to start (Defender, PLAN 10.2 Q6) is reported as
/// KNOWN-ISSUE through the existing detection and never worked around. The folder is cleaned before and after.
/// </summary>
public static class McpReadToolTests
{
    const string InitializeParams = "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cadence-tests\",\"title\":\"Cadence Test Agent\",\"version\":\"1\"}}";
    const string Marker = "MCP-READ-MARKER-5f3a9c";
    const string ForeignName = "foreign1";
    static readonly Regex Revision = new("\\A[0-9a-f]{12}\\z");

    sealed record Reply(bool IsError, JsonElement Structured, string Text, string Line);

    static Reply Call(McpTestClient c, string tool, string arguments = "{}")
    {
        JsonElement? r = c.Request("tools/call", "{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}", timeoutMs: 20000);
        if (r is null) throw new InvalidOperationException(tool + ": no answer from the server");
        JsonElement res = r.Value.GetProperty("result");
        string text = res.GetProperty("content")[0].GetProperty("text").GetString()!;
        return new Reply(res.GetProperty("isError").GetBoolean(), res.TryGetProperty("structuredContent", out JsonElement s) ? s.Clone() : default, text, r.Value.GetRawText());
    }

    static string Code(Reply r) => r.Structured.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>A hash of every task of \CadenceTest (name and XML), so "the folder did not change" is one comparison.</summary>
    static string FolderHash(E2E e) =>
        ApplyFx.Sha(string.Join("\n", e.Gw.ListManagedFolder().OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t.Name + "|" + t.Xml)));

    public static void ReadToolsOverRealPipesAgainstARealFolder() => E2E.Scenario(e =>
    {
        // ---- the world: two managed jobs, one foreign task ----
        JobDefinition runJob = E2E.Job("fine one", "cmd.exe", "/d /c echo " + Marker);
        JobDefinition second = Fx.Job("second job", ["0 9 * * 1"], skip: Cadence.Core.Model.HolidayCalendar.Nyse);
        ClassifiedTask a = e.Save(runJob);
        ClassifiedTask b = e.Save(second);
        e.Gw.Register(ForeignName, Fx.Xml(Fx.FarTrigger));
        string foreignHash = ApplyFx.Sha(e.Gw.Get(ForeignName)!.Xml);
        T.Eq("the foreign task is Foreign to Cadence", Ownership.Foreign, e.Jobs.Reclassify(ForeignName)!.Ownership);

        using var c = new McpTestClient(["--mcp", "--folder", "CadenceTest"]);
        try
        {
            T.True("initialize answered", c.Request("initialize", InitializeParams) is not null);
            c.Notify("notifications/initialized");

            // ---- tools/list ----
            JsonElement tools = c.Request("tools/list")!.Value.GetProperty("result").GetProperty("tools");
            T.Eq("twelve tools: six read-only, the four write tools ([Phases 21-22]) and the two control tools ([OWNER-Q14])", 12, tools.GetArrayLength());
            T.True("every read tool is read-only and closed-world", tools.EnumerateArray().Where(t => t.GetProperty("name").GetString() is not ("cadence_suspend_agents" or "cadence_resume_agents" or "cadence_save_job" or "cadence_set_job_enabled" or "cadence_run_job_now" or "cadence_delete_job")).All(t => t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() && !t.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean()));
            T.True("the two control tools are not read-only, are idempotent and closed-world", tools.EnumerateArray().Where(t => t.GetProperty("name").GetString() is "cadence_suspend_agents" or "cadence_resume_agents").All(t => !t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() && t.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean() && !t.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean()));

            // ---- list: exactly the two managed jobs, never the foreign task ----
            Reply list = Call(c, "cadence_list_jobs");
            T.True("list: not an error", !list.IsError);
            JsonElement[] rows = [.. list.Structured.GetProperty("jobs").EnumerateArray()];
            T.Eq("list: count", 2, list.Structured.GetProperty("count").GetInt32());
            T.Eq("list: two rows", 2, rows.Length);
            T.Eq("list: the two ids", string.Join(",", new[] { runJob.Id, second.Id }.Order(StringComparer.Ordinal)), string.Join(",", rows.Select(r => r.GetProperty("jobId").GetString()!).Order(StringComparer.Ordinal)));
            T.Eq("list: sorted by name", "fine one,second job", string.Join(",", rows.Select(r => r.GetProperty("name").GetString())));
            T.True("list: the foreign task is not there", !list.Line.Contains(ForeignName, StringComparison.Ordinal));
            T.Eq("list: folder", "\\CadenceTest", list.Structured.GetProperty("folder").GetString());
            JsonElement rowA = rows[0], rowB = rows[1];
            T.Eq("list: a is ok", "ok", rowA.GetProperty("state").GetString());
            T.Eq("list: b is disabled", false, rowB.GetProperty("enabled").GetBoolean());
            T.Eq("list: b sentence", "Every Monday at 9:00, except NYSE market holidays", rowB.GetProperty("schedule").GetString());
            T.Eq("list: no run yet", "none", rowA.GetProperty("lastResult").GetString());
            T.True("list: no next run for a job dated 2099, and none for a disabled one", rowA.GetProperty("nextRun").ValueKind == JsonValueKind.Null && rowB.GetProperty("nextRun").ValueKind == JsonValueKind.Null);
            T.Eq("list: attribution is unknown until Phase 19", "unknown", rowA.GetProperty("updated").GetProperty("by").GetString());
            Fx.Info("SAMPLE cadence_list_jobs (real exe): " + Abbrev(list.Text, 900));

            // ---- get_job ----
            Reply get = Call(c, "cadence_get_job", "{\"jobId\":\"" + runJob.Id + "\"}");
            T.True("get_job: not an error", !get.IsError);
            JsonElement j = get.Structured;
            T.Eq("get_job: jobId", runJob.Id, j.GetProperty("jobId").GetString());
            T.Eq("get_job: name", "fine one", j.GetProperty("name").GetString());
            T.Eq("get_job: task path", "\\CadenceTest\\fine one", j.GetProperty("taskPath").GetString());
            T.Eq("get_job: state", "ok", j.GetProperty("state").GetString());
            T.Eq("get_job: program path", "cmd.exe", j.GetProperty("program").GetProperty("path").GetString());
            T.Eq("get_job: arguments", "/d /c echo " + Marker, j.GetProperty("program").GetProperty("arguments").GetString());
            T.Eq("get_job: cron verbatim", "0 18 * * *", j.GetProperty("schedule").GetProperty("cron")[0].GetString());
            T.Eq("get_job: sentence", "Every day at 18:00", j.GetProperty("schedule").GetProperty("description").GetString());
            T.Eq("get_job: no next runs (the job is dated 2099, beyond the engine's 10-year horizon, so it cannot fire)", 0, j.GetProperty("nextRuns").GetArrayLength());
            string revision = j.GetProperty("revision").GetString()!;
            T.True("get_job: revision is 12 hex characters", Revision.IsMatch(revision));
            T.Eq("get_job: revision is stable", revision, Call(c, "cadence_get_job", "{\"jobId\":\"" + runJob.Id + "\"}").Structured.GetProperty("revision").GetString());
            T.Eq("get_job: the revision matches the live task", Cadence.Core.Planning.JobRevision.Of(e.Fresh(a)), revision);
            T.Eq("get_job: no runs yet", 0, j.GetProperty("recentRuns").GetArrayLength());
            Fx.Info("SAMPLE cadence_get_job (real exe, before any run): " + Abbrev(get.Text, 1400));

            // ---- foreign tasks and guesses: not found, never fetched ----
            foreach (string guess in new[] { ForeignName, "zzzzzzzz", "00000000", "aaaaaaaa" })
            {
                Reply g = Call(c, "cadence_get_job", "{\"jobId\":\"" + guess + "\"}");
                T.True("get_job '" + guess + "': not_found", g.IsError && Code(g) == "not_found");
                Reply o = Call(c, "cadence_get_run_output", "{\"jobId\":\"" + guess + "\",\"runId\":\"20260930-180002-117\"}");
                T.True("get_run_output '" + guess + "': not_found", o.IsError && Code(o) == "not_found");
            }
            T.True("get_job by the foreign task's name is refused", Call(c, "cadence_get_job", "{\"jobId\":\"" + ForeignName + " task\"}") is { IsError: true } byName && Code(byName) == "invalid_argument");
            T.True("get_job with an extra argument is refused", Call(c, "cadence_get_job", "{\"jobId\":\"" + runJob.Id + "\",\"x\":1}") is { IsError: true } extra && Code(extra) == "invalid_argument");
            T.True("a run that does not exist: not_found", Call(c, "cadence_get_run_output", "{\"jobId\":\"" + runJob.Id + "\",\"runId\":\"20260930-180002-117\"}") is { IsError: true } none && Code(none) == "not_found");

            // ---- run job a on purpose, then read its run and its output ----
            IReadOnlyList<RunRecord>? recs = e.RunAndWaitUnlessBlocked(a, 1, TimeSpan.FromSeconds(90), "MCP read tools: run");
            if (recs is null)
            {
                T.KnownIssue("MCP read tools: the run, its record and its output were NOT verified",
                    "Windows refused to start the wrapper (LastTaskResult 0x80070005, no wrapper process, no run record): Windows Defender, see the README Safety notes (Checkpoint B). Not worked around on purpose.");
                e.WaitIdle(a.Observed.Name, TimeSpan.FromSeconds(10));
                JsonElement blocked = Call(c, "cadence_get_job", "{\"jobId\":\"" + runJob.Id + "\"}").Structured;
                T.Eq("blocked: get_job shows a didNotReport row", "didNotReport", blocked.GetProperty("recentRuns")[0].GetProperty("status").GetString());
                T.Eq("blocked: with the plain message", Cadence.Core.TaskXml.TaskResultCodes.AccessDeniedLaunch, blocked.GetProperty("recentRuns")[0].GetProperty("message").GetString());
                T.Eq("blocked: lastResult comes from the records, not from the result code", "didNotReport", blocked.GetProperty("lastResult").GetProperty("status").GetString());
            }
            else
            {
                e.WaitIdle(a.Observed.Name, TimeSpan.FromSeconds(30));
                RunRecord rec = recs[0];
                Reply after = Call(c, "cadence_get_job", "{\"jobId\":\"" + runJob.Id + "\"}");
                JsonElement ja = after.Structured;
                JsonElement run = ja.GetProperty("recentRuns")[0];
                T.Eq("get_job: the run is in recentRuns", rec.RunId, run.GetProperty("runId").GetString());
                T.Eq("get_job: status ok", "ok", run.GetProperty("status").GetString());
                T.Eq("get_job: exit code 0", 0, run.GetProperty("exitCode").GetInt32());
                T.Eq("get_job: hasOutput", true, run.GetProperty("hasOutput").GetBoolean());
                T.Eq("get_job: lastResult ok", "ok", ja.GetProperty("lastResult").GetProperty("status").GetString());
                T.Eq("get_job: sampleSize 1", 1, ja.GetProperty("stats").GetProperty("sampleSize").GetInt32());
                T.Eq("get_job: success rate 1", 1.0, ja.GetProperty("stats").GetProperty("successRate").GetDouble());
                T.True("get_job: the revision changed or stayed (a run writes nothing to the task)", Revision.IsMatch(ja.GetProperty("revision").GetString()!));
                Fx.Info("SAMPLE cadence_get_job (real exe, after one run): " + Abbrev(after.Text, 1800));

                JsonElement rowAfter = Call(c, "cadence_list_jobs").Structured.GetProperty("jobs").EnumerateArray().First(r => r.GetProperty("jobId").GetString() == runJob.Id);
                T.Eq("list: lastResult ok", "ok", rowAfter.GetProperty("lastResult").GetString());

                Reply output = Call(c, "cadence_get_run_output", "{\"jobId\":\"" + runJob.Id + "\",\"runId\":\"" + rec.RunId + "\"}");
                T.True("get_run_output: not an error", !output.IsError);
                T.True("get_run_output: the marker text (" + Abbrev(output.Text, 200) + ")", output.Structured.GetProperty("text").GetString()!.Contains(Marker, StringComparison.Ordinal));
                T.Eq("get_run_output: not truncated", false, output.Structured.GetProperty("truncated").GetBoolean());
                T.Eq("get_run_output: total chars is what the program printed", e.Store.ReadOutput(runJob.Id, rec.RunId, false)!.Length, output.Structured.GetProperty("totalChars").GetInt32());
                Fx.Info("SAMPLE cadence_get_run_output (real exe): " + Abbrev(output.Text, 400));

                Reply cut = Call(c, "cadence_get_run_output", "{\"jobId\":\"" + runJob.Id + "\",\"runId\":\"" + rec.RunId + "\",\"maxChars\":5}");
                T.Eq("get_run_output maxChars 5: truncated", true, cut.Structured.GetProperty("truncated").GetBoolean());
                T.True("get_run_output maxChars 5: clear marker first (" + Abbrev(cut.Structured.GetProperty("text").GetString()!, 120) + ")", cut.Structured.GetProperty("text").GetString()!.StartsWith("[truncated: showing the last 5 of ", StringComparison.Ordinal));

                Reply err = Call(c, "cadence_get_run_output", "{\"jobId\":\"" + runJob.Id + "\",\"runId\":\"" + rec.RunId + "\",\"stream\":\"stderr\"}");
                T.Eq("get_run_output stderr: empty", "", err.Structured.GetProperty("text").GetString());
                T.True("get_run_output stderr: says no output was captured", err.Structured.GetProperty("note").GetString()!.Contains("captured no output", StringComparison.Ordinal));

                Reply cross = Call(c, "cadence_get_run_output", "{\"jobId\":\"" + second.Id + "\",\"runId\":\"" + rec.RunId + "\"}");
                T.True("get_run_output: another job's run id is not_found", cross.IsError && Code(cross) == "not_found");
                T.True("and the marker does not leak", !cross.Line.Contains(Marker, StringComparison.Ordinal));
            }

            // ---- preview, templates, status: nothing in \CadenceTest changes ----
            string before = FolderHash(e);
            Reply preview = Call(c, "cadence_preview_schedule", "{\"cron\":[\"0 18 * * 1-5\"],\"skipHolidays\":\"nyse\"}");
            T.True("preview: valid", !preview.IsError && preview.Structured.GetProperty("valid").GetBoolean());
            T.Eq("preview: sentence", "Every weekday at 18:00, except NYSE market holidays", preview.Structured.GetProperty("description").GetString());
            T.Eq("preview: eight runs", 8, preview.Structured.GetProperty("nextRuns").GetArrayLength());
            T.Eq("preview: a template", true, Call(c, "cadence_preview_schedule", "{\"templateId\":\"weekdays-6pm\"}").Structured.GetProperty("valid").GetBoolean());
            Reply bad = Call(c, "cadence_preview_schedule", "{\"cron\":[\"@reboot\"]}");
            T.True("preview: @reboot is a normal result with an alternative", !bad.IsError && !bad.Structured.GetProperty("valid").GetBoolean() && bad.Structured.GetProperty("closestAlternative")[0].GetString() == "@logon");
            T.True("preview: both cron and templateId is a tool error", Call(c, "cadence_preview_schedule", "{\"cron\":[\"0 9 * * *\"],\"templateId\":\"hourly\"}") is { IsError: true } both && Code(both) == "invalid_argument");
            Reply templates = Call(c, "cadence_list_templates");
            T.Eq("templates: nine", 9, templates.Structured.GetProperty("templates").GetArrayLength());
            Reply status = Call(c, "cadence_get_status");
            T.Eq("status: the two managed jobs (not the foreign task)", 2, status.Structured.GetProperty("jobs").GetInt32());
            T.Eq("status: folder", "\\CadenceTest", status.Structured.GetProperty("folder").GetString());
            T.Eq("status: you", "Cadence Test Agent", status.Structured.GetProperty("you").GetString());
            T.Eq("the folder is unchanged after preview, templates and status", before, FolderHash(e));

            bool exited = c.CloseStdinAndWait(5000);
            T.True("the server exits when stdin closes", exited);
            if (exited) T.Eq("exit code 0", 0, c.ExitCode);
            T.True("every stdout line is a JSON-RPC message", c.StdoutLines.All(l => JsonDocument.Parse(l).RootElement.TryGetProperty("jsonrpc", out _)));
            T.True("stdout is plain ASCII", c.StdoutLines.All(l => l.All(ch => ch < 128)));
            T.True("stderr lines are all prefixed", c.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => l.StartsWith("cadence-mcp: ", StringComparison.Ordinal)));
        }
        finally { c.Kill(); }

        T.Eq("the foreign task's XML is unchanged", foreignHash, ApplyFx.Sha(e.Gw.Get(ForeignName)!.Xml));
        T.True("both managed jobs are still there and InSync", e.Jobs.Load(false).Count(x => x.Job is not null && x.Sync == SyncStatus.InSync) == 2);
        T.True("the read tools created no registry key for the test folder's agents", Microsoft.Win32.Registry.CurrentUser.OpenSubKey(TestFolder.AgentsKey) is null);
    }, spawnsProcesses: true);

    public static void AnEmptyFolderListsNothingAndNeverCreatesIt() => E2E.Scenario(e =>
    {
        // \CadenceTest does not exist here (Scenario cleans it first): the read tools must say "no jobs" without creating it.
        T.True("the folder does not exist before", !TestFolder.Exists());
        using var c = new McpTestClient(["--mcp", "--folder", "CadenceTest"]);
        try
        {
            c.Request("initialize", InitializeParams);
            Reply list = Call(c, "cadence_list_jobs");
            T.Eq("no jobs", 0, list.Structured.GetProperty("count").GetInt32());
            T.Eq("an empty array", 0, list.Structured.GetProperty("jobs").GetArrayLength());
            T.Eq("status: 0 jobs", 0, Call(c, "cadence_get_status").Structured.GetProperty("jobs").GetInt32());
            T.True("get_job: not_found", Call(c, "cadence_get_job", "{\"jobId\":\"abcdefgh\"}") is { IsError: true } r && Code(r) == "not_found");
            T.True("preview still works", Call(c, "cadence_preview_schedule", "{\"cron\":[\"0 9 * * *\"]}").Structured.GetProperty("valid").GetBoolean());
            T.True("the folder still does not exist", !TestFolder.Exists());
            c.CloseStdinAndWait(5000);
        }
        finally { c.Kill(); }
    }, spawnsProcesses: false);

    static string Abbrev(string s, int max) => s.Length <= max ? s : s[..max] + " ...(" + (s.Length - max).ToString(CultureInfo.InvariantCulture) + " more chars)";
}
