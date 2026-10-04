using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Agent.Audit;
using Cadence.Core.Metadata;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.IntegrationTests.Agents;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests.Mcp;

/// <summary>
/// Phase 21: <c>cadence_save_job</c> through the REAL <c>Cadence.exe --mcp --folder CadenceTest</c> against a real \CadenceTest\: create, an identical retry, an update with
/// the right revision, a stale update after the owner's change, suspension flipped through the command line while the session runs, a locked job, a foreign task with
/// the same name, the limits and the program rules. Harmless programs only (<c>cmd.exe /d /c "echo agent-ok"</c>) on schedules that cannot fire: the main job is pinned to a
/// past date, and every job whose schedule is dense is DISABLED. Nothing is ever started. The folder, the agent registry key and the audit file of the TEST folder are
/// cleaned before and after; the product's key and folder are never touched.
/// </summary>
public static class McpSaveJobTests
{
    const string Folder = "CadenceTest";
    const string InitializeParams = "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cadence-tests\",\"title\":\"Cadence Test Agent\",\"version\":\"1\"}}";
    const string Cmd = "C:\\\\Windows\\\\System32\\\\cmd.exe";
    const string Secret = "int-s3cr3t-77aa";
    static readonly string[] Test = ["--folder", Folder];

    sealed record Reply(bool IsError, JsonElement Structured, string Text, string Line);

    static Reply Call(McpTestClient c, string tool, string arguments)
    {
        JsonElement? r = c.Request("tools/call", "{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}", timeoutMs: 60000);
        if (r is null) throw new InvalidOperationException(tool + ": no answer from the server");
        JsonElement res = r.Value.GetProperty("result");
        string text = res.GetProperty("content")[0].GetProperty("text").GetString()!;
        return new Reply(res.GetProperty("isError").GetBoolean(), res.TryGetProperty("structuredContent", out JsonElement s) ? s.Clone() : default, text, r.Value.GetRawText());
    }

    static string Code(Reply r) => r.Structured.GetProperty("error").GetProperty("code").GetString()!;

    static string Msg(Reply r) => r.Structured.GetProperty("error").GetProperty("message").GetString()!;

    static string Str(JsonElement e, params string[] path)
    {
        foreach (string p in path) e = e.GetProperty(p);
        return e.GetString()!;
    }

    static string FolderHash(E2E e) =>
        ApplyFx.Sha(string.Join("\n", e.Gw.ListManagedFolder().OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t.Name + "|" + t.Xml)));

    static string Create(string name, string cron, string arguments = "/d /c \\\"echo agent-ok\\\"", string extraProgram = "", string extraTop = "") =>
        "{\"name\":" + JsonSerializer.Serialize(name) + ",\"schedule\":{\"cron\":[" + JsonSerializer.Serialize(cron) + "]},\"program\":{\"path\":\"" + Cmd + "\",\"arguments\":\"" + arguments + "\"" + extraProgram + "}" + extraTop + "}";

    static string Update(string id, string revision, string body) => "{\"jobId\":\"" + id + "\",\"expectedRevision\":\"" + revision + "\"," + body + "}";

    /// <summary>"m h D M *" for yesterday: a once-a-year schedule whose next date is next year, so it cannot fire during a test.</summary>
    static string Yesterday(int hourOffset = 0)
    {
        DateTime y = DateTime.Now.AddDays(-1);
        return string.Create(CultureInfo.InvariantCulture, $"{y.Minute} {(y.Hour + hourOffset) % 24} {y.Day} {y.Month} *");
    }

    static bool ProductKeyExists() { using Microsoft.Win32.RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Cadence\Agents"); return k is not null; }

    static string[] A(params string[] args) => [.. args, .. Test];

    public static void SaveCreatesRetriesUpdatesAndRefusesOverARealFolder() => E2E.Scenario(e =>
    {
        TestFolder.CleanAgentState();
        bool productKeyBefore = ProductKeyExists();
        try { Run(e); }
        finally
        {
            foreach (ClassifiedTask t in e.Jobs.Load(false)) e.Applier.Delete(t.Observed.Name);
            T.True("every job of the test was deleted", e.Jobs.Load(false).Count == 0);
            TestFolder.CleanAgentState();
        }
        T.Eq("the product's agent key was never created or removed", productKeyBefore, ProductKeyExists());
    }, spawnsProcesses: true);

    static void Run(E2E e)
    {
        string foreignName = "Same name job";
        e.Gw.Register(foreignName, Fx.Xml(Fx.FarTrigger));
        string foreignXml = e.Gw.Get(foreignName)!.Xml;
        string foreignHash = ApplyFx.Sha(foreignXml);
        var appJobs = new ManagedJobs(e.Gw, () => DateTimeOffset.Now, Actor.App);
        string cron = Yesterday();

        using var c = new McpTestClient(["--mcp", "--folder", Folder]);
        try
        {
            T.True("initialize answered", c.Request("initialize", InitializeParams) is not null);
            c.Notify("notifications/initialized");

            // ---- the tool is offered, with its annotations ----
            JsonElement tools = c.Request("tools/list")!.Value.GetProperty("result").GetProperty("tools");
            JsonElement save = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "cadence_save_job");
            T.Eq("twelve tools: six read, the four write tools, the two control tools", 12, tools.GetArrayLength());
            T.True("save: destructive, idempotent, not read-only, closed world", save.GetProperty("annotations") is { } a && !a.GetProperty("readOnlyHint").GetBoolean() && a.GetProperty("destructiveHint").GetBoolean() && a.GetProperty("idempotentHint").GetBoolean() && !a.GetProperty("openWorldHint").GetBoolean());
            T.Eq("save: title", "Create or change a schedule", save.GetProperty("title").GetString());

            // ---- create ----
            Reply preview = Call(c, "cadence_preview_schedule", "{\"cron\":[\"" + cron + "\"]}");
            Reply created = Call(c, "cadence_save_job", Create("Agent job", cron, extraProgram: ",\"environment\":{\"API_KEY\":\"" + Secret + "\"}"));
            T.True("create: not an error: " + created.Text, !created.IsError);
            Fx.Info("SAMPLE cadence_save_job create (real exe): " + Abbrev(created.Line, 1500));
            JsonElement job = created.Structured.GetProperty("job");
            string id = Str(job, "jobId");
            string rev0 = Str(job, "revision");
            T.Eq("create: outcome", "created", Str(created.Structured, "outcome"));
            T.Eq("create: the task is under \\CadenceTest", "\\CadenceTest\\Agent job", Str(job, "taskPath"));
            T.Eq("create: the schedule sentence equals the preview's", Str(preview.Structured, "description"), Str(job, "schedule", "description"));
            RegisteredTaskInfo? task = e.Gw.Get("Agent job");
            T.True("create: the task exists in \\CadenceTest", task is not null);
            ClassifiedTask live = e.Jobs.Reclassify("Agent job")!;
            T.Eq("create: managed", Ownership.Managed, live.Ownership);
            T.Eq("create: in sync", SyncStatus.InSync, live.Sync);
            T.Eq("create: Source is Cadence", "Cadence", TaskXmlReader.Read(task!.Xml).RegistrationInfo.Source);
            T.Eq("create: createdBy from the client's title", "agent:Cadence Test Agent", live.Metadata!.CreatedBy);
            T.Eq("create: updatedBy from the client's title", "agent:Cadence Test Agent", live.Metadata.UpdatedBy);
            T.Eq("create: the revision matches the live task", JobRevision.Of(live), rev0);
            T.Eq("create: the default timeout of an agent job", 60, job.GetProperty("program").GetProperty("timeoutMinutes").GetInt32());
            T.True("create: the environment value is stored but never returned", live.Job!.Run.Env.Any(kv => kv.Value == Secret) && !created.Line.Contains(Secret, StringComparison.Ordinal));

            // ---- retry of the same create: unchanged, no duplicate ----
            int tasksBefore = e.Gw.ListManagedFolder().Count;
            Reply again = Call(c, "cadence_save_job", Create("Agent job", cron, extraProgram: ",\"environment\":{\"API_KEY\":\"" + Secret + "\"}"));
            T.True("retry: unchanged", !again.IsError && Str(again.Structured, "outcome") == "unchanged" && Str(again.Structured, "job", "jobId") == id, again.Text);
            T.Eq("retry: no duplicate", tasksBefore, e.Gw.ListManagedFolder().Count);
            Reply clash = Call(c, "cadence_save_job", Create("Agent job", cron, arguments: "/d /c exit 0"));
            T.True("same name, other settings: name_taken with the id", clash.IsError && Code(clash) == "name_taken" && Msg(clash).Contains(id, StringComparison.Ordinal), clash.Text);

            // ---- update with the right revision; the same call again is unchanged; a stale one is refused ----
            string cron2 = "5 " + (DateTime.Now.AddDays(-1).Hour + 1) % 24 + " " + DateTime.Now.AddDays(-1).Day + " " + DateTime.Now.AddDays(-1).Month + " *";
            string body = "\"schedule\":{\"cron\":[\"" + cron2 + "\"]}";
            Reply upd = Call(c, "cadence_save_job", Update(id, rev0, body));
            T.True("update: ok: " + upd.Text, !upd.IsError && Str(upd.Structured, "outcome") == "updated", upd.Text);
            T.True("update: the plan says what changes", Str(upd.Structured, "changes").StartsWith("Will update: ", StringComparison.Ordinal), Str(upd.Structured, "changes"));
            Fx.Info("SAMPLE cadence_save_job update changes: " + Str(upd.Structured, "changes"));
            string rev1 = Str(upd.Structured, "job", "revision");
            T.True("update: a new revision", rev1 != rev0 && rev1 == JobRevision.Of(e.Jobs.Reclassify("Agent job")!));
            T.Eq("update: updated by the agent, created by the agent", "agent", Str(upd.Structured, "job", "updated", "byKind"));
            Reply same = Call(c, "cadence_save_job", Update(id, rev0, body));
            T.True("the same update again (old revision): unchanged, not stale", !same.IsError && Str(same.Structured, "outcome") == "unchanged", same.Text);
            T.Eq("it reports the current revision", rev1, Str(same.Structured, "job", "revision"));

            // The owner changes the job in the app after the agent read it.
            ClassifiedTask owner = e.Jobs.Reclassify("Agent job")!;
            var ownerSave = new Applier(appJobs);
            T.True("the owner's edit", ownerSave.Save(owner.Job! with { Run = owner.Job!.Run with { Args = owner.Job.Run.Args + " owner" } }, "Agent job").Success);
            string ownerXml = e.Gw.Get("Agent job")!.Xml;
            Reply stale = Call(c, "cadence_save_job", Update(id, rev1, "\"program\":{\"arguments\":\"/d /c exit 1\"}"));
            T.True("stale: refused", stale.IsError && Code(stale) == "stale_revision", stale.Text);
            T.True("stale: names the owner's app and says what to call", Msg(stale).Contains("by Cadence (the app) after you read it", StringComparison.Ordinal) && Msg(stale).Contains("cadence_get_job", StringComparison.Ordinal), Msg(stale));
            T.Eq("stale: the owner's version is untouched", ownerXml, e.Gw.Get("Agent job")!.Xml);
            string rev2 = Str(Call(c, "cadence_get_job", "{\"jobId\":\"" + id + "\"}").Structured, "revision");
            T.True("stale: re-reading gives the current revision", rev2 == JobRevision.Of(e.Jobs.Reclassify("Agent job")!) && rev2 != rev1);

            // ---- suspension flipped through the CLI while the session runs ----
            T.Eq("CLI suspend: exit 0", 0, RealCli.Run(A("--agents", "suspend")).Exit);
            string hashSuspended = FolderHash(e);
            Reply susp = Call(c, "cadence_save_job", Update(id, rev2, "\"program\":{\"arguments\":\"/d /c exit 2\"}"));
            T.True("suspended: refused", susp.IsError && Code(susp) == "suspended" && Msg(susp).StartsWith("AI agents are suspended on this PC", StringComparison.Ordinal), susp.Text);
            T.Eq("suspended: nothing changed, the revision is the same", rev2, JobRevision.Of(e.Jobs.Reclassify("Agent job")!));
            T.True("suspended: a create is refused too", Code(Call(c, "cadence_save_job", Create("Blocked", cron))) == "suspended");
            T.Eq("suspended: the folder is byte-identical", hashSuspended, FolderHash(e));
            T.Eq("CLI resume: exit 0", 0, RealCli.Run(A("--agents", "resume")).Exit);
            Reply back = Call(c, "cadence_save_job", Update(id, rev2, "\"program\":{\"arguments\":\"/d /c exit 2\"}"));
            T.True("resumed: the same session saves again without a restart", !back.IsError && Str(back.Structured, "outcome") == "updated", back.Text);
            string rev3 = Str(back.Structured, "job", "revision");

            // ---- a locked job ----
            T.Eq("CLI lock: exit 0", 0, RealCli.Run(A("--agents", "lock", id, "--yes")).Exit);
            string lockedHash = FolderHash(e);
            string lockedRev = JobRevision.Of(e.Jobs.Reclassify("Agent job")!);
            Reply locked = Call(c, "cadence_save_job", Update(id, lockedRev, "\"program\":{\"arguments\":\"/d /c exit 3\"}"));
            T.True("locked: refused", locked.IsError && Code(locked) == "locked" && Msg(locked).Contains("The owner locked this job", StringComparison.Ordinal), locked.Text);
            T.Eq("locked: byte-identical", lockedHash, FolderHash(e));
            T.True("locked: the agent can't send the lock", Code(Call(c, "cadence_save_job", Update(id, lockedRev, "\"agentLock\":false"))) == "invalid_argument");
            T.Eq("CLI unlock: exit 0", 0, RealCli.Run(A("--agents", "unlock", id, "--yes")).Exit);
            T.True("unlocked: saving works again", !Call(c, "cadence_save_job", Update(id, JobRevision.Of(e.Jobs.Reclassify("Agent job")!), "\"program\":{\"arguments\":\"/d /c exit 3\"}")).IsError);
            _ = rev3;

            // ---- a raw task with the same name in the folder is never touched ----
            Reply twin = Call(c, "cadence_save_job", Create(foreignName, cron));
            T.True("same name as a foreign task: created", !twin.IsError, twin.Text);
            T.Eq("the agent's job got (2) in its task name", "\\CadenceTest\\Same name job (2)", Str(twin.Structured, "job", "taskPath"));
            T.Eq("the foreign task is byte-identical", foreignHash, ApplyFx.Sha(e.Gw.Get(foreignName)!.Xml));
            T.Eq("the foreign task is still Foreign", Ownership.Foreign, e.Jobs.Reclassify(foreignName)!.Ownership);

            // ---- limits ----
            Reply every = Call(c, "cadence_save_job", Create("Too often", "* * * * *", extraTop: ",\"enabled\":false"));
            T.True("minimum interval: every minute is refused", every.IsError && Code(every) == "schedule_too_frequent" && Msg(every).StartsWith("This schedule runs every 1 minute; agents may schedule at most every 5 minutes.", StringComparison.Ordinal), every.Text);
            T.True("minimum interval: every 5 minutes is allowed (disabled, so it cannot run)", !Call(c, "cadence_save_job", Create("Every five", "*/5 * * * *", extraTop: ",\"enabled\":false")).IsError);
            Reply bad = Call(c, "cadence_save_job", Create("Invalid cron", "@reboot"));
            T.True("an invalid schedule: schedule_invalid with the alternative", bad.IsError && Code(bad) == "schedule_invalid" && bad.Structured.GetProperty("error").GetProperty("closestAlternative")[0].GetString() == "@logon", bad.Text);

            int agentJobs = Call(c, "cadence_get_status", "{}").Structured.GetProperty("agents").GetProperty("agentJobs").GetInt32();
            T.Eq("CLI config --max-jobs: exit 0", 0, RealCli.Run(A("--agents", "config", "--max-jobs", (agentJobs + 1).ToString(CultureInfo.InvariantCulture))).Exit);
            T.True("one more job fits under the cap", !Call(c, "cadence_save_job", Create("Last allowed", cron)).IsError);
            Reply over = Call(c, "cadence_save_job", Create("One too many", cron));
            T.True("the cap: limit_reached", over.IsError && Code(over) == "limit_reached" && Msg(over).StartsWith("Not created: agents may create at most ", StringComparison.Ordinal), over.Text);
            T.True("the cap: a retry of an existing job still succeeds", Str(Call(c, "cadence_save_job", Create("Last allowed", cron)).Structured, "outcome") == "unchanged");
            T.Eq("CLI config --max-jobs 20: exit 0", 0, RealCli.Run(A("--agents", "config", "--max-jobs", "20")).Exit);

            // ---- input rules ----
            Reply big = Call(c, "cadence_save_job", Create("Big", cron, arguments: new string('a', 8001)));
            T.True("an oversize argument is refused", big.IsError && Code(big) == "invalid_argument", big.Text);
            string env31 = string.Join(",", Enumerable.Range(0, 31).Select(i => "\"V" + i.ToString(CultureInfo.InvariantCulture) + "\":\"x\""));
            T.True("31 environment entries: program_rejected", Code(Call(c, "cadence_save_job", Create("Env", cron, extraProgram: ",\"environment\":{" + env31 + "}"))) == "program_rejected");
            T.True("a bare program name: program_rejected", Code(Call(c, "cadence_save_job", "{\"name\":\"Bare\",\"schedule\":{\"cron\":[\"" + cron + "\"]},\"program\":{\"path\":\"cmd.exe\"}}")) == "program_rejected");
            T.True("a missing file: program_rejected", Code(Call(c, "cadence_save_job", "{\"name\":\"Missing\",\"schedule\":{\"cron\":[\"" + cron + "\"]},\"program\":{\"path\":\"C:\\\\Windows\\\\System32\\\\no-such-program-cadence.exe\"}}")) == "program_rejected");
            T.True("a .bat file: program_rejected with the cmd form", Msg(Call(c, "cadence_save_job", "{\"name\":\"Bat\",\"schedule\":{\"cron\":[\"" + cron + "\"]},\"program\":{\"path\":\"C:\\\\Windows\\\\System32\\\\run.bat\"}}")).Contains("/d /s /c", StringComparison.Ordinal));
            Reply self = Call(c, "cadence_save_job", Create("Self", cron, arguments: "/c Cadence.exe --agents resume --yes"));
            T.True("CP-R9: a command that resumes agents is refused", self.IsError && Code(self) == "program_rejected" && Msg(self) == "Agents can't schedule Cadence itself or change its settings.", self.Text);

            T.Eq("the foreign task is still byte-identical", foreignHash, ApplyFx.Sha(e.Gw.Get(foreignName)!.Xml));
            T.True("no job named after a refused request exists", e.Jobs.Load(false).All(j => j.Job!.Name is "Agent job" or "Same name job" or "Every five" or "Last allowed"));

            bool exited = c.CloseStdinAndWait(10000);
            T.True("the server exits when stdin closes", exited);
            if (exited) T.Eq("exit code 0", 0, c.ExitCode);
            T.True("every stdout line is a JSON-RPC message", c.StdoutLines.All(l => JsonDocument.Parse(l).RootElement.TryGetProperty("jsonrpc", out _)));
            T.True("stderr lines are all prefixed", c.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => l.StartsWith("cadence-mcp: ", StringComparison.Ordinal)));
        }
        finally { c.Kill(); }

        // ---- the audit file ----
        var log = new FileAuditLog(Folder);
        IReadOnlyList<AuditEntry> saves = [.. log.Tail(1000).Where(x => x.Tool == "cadence_save_job")];
        foreach ((string outcome, string code) in new[]
        {
            ("ok", "created"), ("ok", "updated"), ("ok", "unchanged"), ("refused", "stale_revision"), ("refused", "suspended"), ("refused", "locked"), ("refused", "name_taken"),
            ("refused", "schedule_too_frequent"), ("refused", "schedule_invalid"), ("refused", "limit_reached"), ("refused", "program_rejected"), ("refused", "invalid_argument"),
        })
            T.True("audit has " + outcome + "/" + code, saves.Any(x => x.Outcome == outcome && x.Code == code));
        T.True("every save line names the client", saves.All(x => x.Client == "Cadence Test Agent"));
        T.True("the lines of a job carry its id and name", saves.Any(x => x.Code == "created" && x.JobId is { Length: 8 } && x.JobName == "Agent job"));
        T.True("the session was audited", log.Tail(1000).Any(x => x.Tool == "session" && x.Outcome == "connected") && log.Tail(1000).Any(x => x.Tool == "session" && x.Outcome == "disconnected"));
        string raw = File.ReadAllText(log.FilePath);
        T.True("the audit file never holds the environment value or the arguments", !raw.Contains(Secret, StringComparison.Ordinal) && !raw.Contains("agent-ok", StringComparison.Ordinal));
        T.True("the foreign task was never touched", ApplyFx.Sha(e.Gw.Get(foreignName)!.Xml) == foreignHash);
    }

    static string Abbrev(string s, int max) => s.Length <= max ? s : s[..max] + " ...(" + (s.Length - max).ToString(CultureInfo.InvariantCulture) + " more chars)";
}
