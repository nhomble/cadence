using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests.Mcp;

/// <summary>
/// Phase 21 (PLAN 12.3.2): <c>cadence_save_job</c> changing a job: merge semantics, the revision rules (unchanged is decided BEFORE the revision), stale updates naming
/// who changed the job, the per-job lock, jobs a human touched, renames, limits that only apply when the call sets the schedule, and the owner's own saves staying
/// unaffected. Every owner save goes through the real Applier as the app.
/// </summary>
public static class SaveJobUpdateTests
{
    const string Id = "own00001";

    static string Rev(ClassifiedTask c) => JobRevision.Of(c);

    static string Update(string id, string revision, string body) =>
        "{\"jobId\":\"" + id + "\",\"expectedRevision\":\"" + revision + "\"," + body + "}";

    static JobDefinition Rich() => WriteRig.Job("Owner job", Id, "0 9 * * 1-5", "--first") with
    {
        Schedule = new ScheduleSpec(new ValueList<string>(["0 9 * * 1-5"]), HolidayCalendar.Nyse, new DateOnly(2026, 9, 1)),
        Run = new RunSpec
        {
            Exe = WriteRig.Exe, Args = "--first", WorkingDirectory = @"C:\Data", Hidden = false, Timeout = TimeSpan.FromMinutes(30), Encoding = OutputEncoding.Utf8, SourcePath = @"C:\Tools\tool.lnk",
            Env = ValueList.Of(new KeyValuePair<string, string>("API_KEY", WriteRig.Secret), new KeyValuePair<string, string>("MODE", "prod")),
        },
        Settings = new JobSettings { WakeToRun = true, RunOnBatteries = false },
    };

    // ---- merge ----

    public static void OnlyTheFieldsGivenChange()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        int writes = rig.Fake.Writes.Count;

        ToolResult res = rig.Save(Update(Id, Rev(owner), "\"program\":{\"arguments\":\"--second\"}"));
        T.True("not an error: " + res.Text, !res.IsError);
        JsonObject r = res.Structured!;
        T.Eq("outcome", "updated", r["outcome"]!.GetValue<string>());
        T.True("the plan's summary says what changed", r["changes"]!.GetValue<string>().StartsWith("Will update: ", StringComparison.Ordinal) && r["changes"]!.GetValue<string>().Contains("--second", StringComparison.Ordinal), r["changes"]!.GetValue<string>());
        T.Eq("message", "Saved \\" + rig.Folder + "\\Owner job.", r["message"]!.GetValue<string>());
        JsonObject p = r["job"]!["program"]!.AsObject();
        T.Eq("arguments changed", "--second", p["arguments"]!.GetValue<string>());
        T.Eq("path kept", WriteRig.Exe, p["path"]!.GetValue<string>());
        T.Eq("working folder kept", @"C:\Data", p["workingFolder"]!.GetValue<string>());
        T.Eq("timeout kept", 30, p["timeoutMinutes"]!.GetValue<int>());
        T.Eq("hidden kept", false, p["hidden"]!.GetValue<bool>());
        T.Eq("encoding kept", "utf8", p["outputEncoding"]!.GetValue<string>());
        T.Eq("environment kept (names)", "API_KEY,MODE", string.Join(",", p["environmentNames"]!.AsArray().Select(n => n!.GetValue<string>())));
        T.Eq("cron kept", "0 9 * * 1-5", r["job"]!["schedule"]!["cron"]![0]!.GetValue<string>());
        T.Eq("holidays kept", "nyse", r["job"]!["schedule"]!["skipHolidays"]!.GetValue<string>());
        T.True("the secret is not in the text, the structure or the audit", !res.Text.Contains(WriteRig.Secret, StringComparison.Ordinal) && !r.ToJsonString().Contains(WriteRig.Secret, StringComparison.Ordinal)
            && !string.Join("|", rig.Audit.All.Select(a => a.Detail + a.JobName + a.Code)).Contains(WriteRig.Secret, StringComparison.Ordinal));
        T.Eq("one write", writes + 1, rig.Fake.Writes.Count);
        T.Eq("TASK_UPDATE style rewrite (create-or-update)", RegisterMode.CreateOrUpdate, rig.Fake.Modes.Last());

        ClassifiedTask live = rig.Live(Id);
        CadenceMetadata m = live.Metadata!;
        T.Eq("the real values of the environment are still stored", WriteRig.Secret, live.Job!.Run.Env.First(kv => kv.Key == "API_KEY").Value);
        T.Eq("createdBy is still the app", "app", m.CreatedBy);
        T.Eq("updatedBy is the agent", "agent:Cadence Test Agent", m.UpdatedBy);
        T.Eq("the source path is kept", @"C:\Tools\tool.lnk", live.Job.Run.SourcePath);
        T.True("the settings are kept", live.Job.Settings is { WakeToRun: true, RunOnBatteries: false });
        T.Eq("the anchor is kept", new DateOnly(2026, 9, 1), live.Job.Schedule.Anchor);
        T.True("not locked", !live.Job.AgentLock);
        T.Eq("the response's revision is the live one", JobRevision.Of(live), r["job"]!["revision"]!.GetValue<string>());
        T.True("audited ok/updated with the id", rig.SaveAudit is [{ Outcome: "ok", Code: "updated", JobId: Id, JobName: "Owner job" }]);

        // skipHolidays alone keeps the cron lines.
        string rev = r["job"]!["revision"]!.GetValue<string>();
        JsonObject skip = rig.SaveOk(Update(Id, rev, "\"schedule\":{\"skipHolidays\":\"none\"}"));
        T.Eq("holidays changed", "none", skip["job"]!["schedule"]!["skipHolidays"]!.GetValue<string>());
        T.Eq("cron kept", "0 9 * * 1-5", skip["job"]!["schedule"]!["cron"]![0]!.GetValue<string>());

        // a template replaces the lines.
        JsonObject tpl = rig.SaveOk(Update(Id, skip["job"]!["revision"]!.GetValue<string>(), "\"schedule\":{\"templateId\":\"weekdays-6pm\"}"));
        T.Eq("template cron", "Every weekday at 18:00", tpl["job"]!["schedule"]!["description"]!.GetValue<string>());

        // environment replaces the whole map, timeoutMinutes 0 removes the limit, enabled false turns it off.
        JsonObject env = rig.SaveOk(Update(Id, tpl["job"]!["revision"]!.GetValue<string>(), "\"program\":{\"environment\":{\"ONLY\":\"1\"},\"timeoutMinutes\":0},\"enabled\":false"));
        T.Eq("environment replaced", "ONLY", string.Join(",", env["job"]!["program"]!["environmentNames"]!.AsArray().Select(n => n!.GetValue<string>())));
        T.Eq("no timeout", 0, env["job"]!["program"]!["timeoutMinutes"]!.GetValue<int>());
        T.Eq("disabled", false, env["job"]!["enabled"]!.GetValue<bool>());
        T.Eq("path still kept", WriteRig.Exe, env["job"]!["program"]!["path"]!.GetValue<string>());
    }

    // ---- unchanged and the revision ----

    public static void AnIdenticalSaveIsUnchangedEvenWithAnOldRevision()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        string oldRev = Rev(owner);
        JsonObject first = rig.SaveOk(Update(Id, oldRev, "\"program\":{\"arguments\":\"--second\"}"));
        int writes = rig.Fake.Writes.Count;
        string xml = rig.Fake.XmlOf("Owner job")!;

        // The agent did not get the answer and retries the very same call with the revision it had.
        ToolResult retry = rig.Save(Update(Id, oldRev, "\"program\":{\"arguments\":\"--second\"}"));
        T.True("a retry is a success, not a stale error", !retry.IsError, retry.Text);
        T.Eq("outcome unchanged", "unchanged", retry.Structured!["outcome"]!.GetValue<string>());
        T.Eq("it carries the job's current revision", first["job"]!["revision"]!.GetValue<string>(), retry.Structured["job"]!["revision"]!.GetValue<string>());
        T.Eq("nothing registered", writes, rig.Fake.Writes.Count);
        T.Eq("the task is byte-identical (no updated bump)", xml, rig.Fake.XmlOf("Owner job"));
        T.True("audited ok/unchanged", rig.SaveAudit.Last() is { Outcome: "ok", Code: "unchanged" });

        // A change with the old revision is stale.
        ToolResult stale = rig.Save(Update(Id, oldRev, "\"program\":{\"arguments\":\"--third\"}"));
        T.True("a different change with the old revision is refused", stale.IsError && WriteRig.Code(stale) == "stale_revision", stale.Text);
        T.Eq("still the second", xml, rig.Fake.XmlOf("Owner job"));

        // Giving the values it already has, spelled explicitly, is unchanged too.
        ToolResult same = rig.Save(Update(Id, first["job"]!["revision"]!.GetValue<string>(), "\"name\":\"Owner job\",\"program\":{\"path\":\"" + WriteRig.Exe.Replace("\\", "\\\\", StringComparison.Ordinal) + "\",\"workingFolder\":\"C:\\\\Data\",\"timeoutMinutes\":30,\"hidden\":false},\"enabled\":true,\"schedule\":{\"cron\":[\"0 9 * * 1-5\"],\"skipHolidays\":\"nyse\"}"));
        T.True("explicit identical values: unchanged", same.Structured?["outcome"]?.GetValue<string>() == "unchanged", same.Text);
        T.True("an update with no fields at all is unchanged as well", rig.SaveOk(Update(Id, first["job"]!["revision"]!.GetValue<string>(), "\"enabled\":true"))["outcome"]!.GetValue<string>() == "unchanged");
    }

    public static void AStaleUpdateNamesWhoChangedTheJobAndWhen()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        string seen = Rev(owner);

        // The owner changes the job in the app after the agent read it.
        rig.Now = WriteRig.FixedNow.AddMinutes(2);
        var app = new Cadence.Scheduler.Apply.Applier(rig.Jobs(Actor.App), TimeSpan.FromSeconds(5));
        T.True("the owner's edit", app.Save(Rich() with { Run = Rich().Run with { Args = "--owner edit" } }, "Owner job").Success);
        rig.Now = WriteRig.FixedNow.AddMinutes(4);   // the agent comes back two minutes after the owner's edit
        int writes = rig.Fake.Writes.Count;
        string xml = rig.Fake.XmlOf("Owner job")!;

        ToolResult r = rig.Save(Update(Id, seen, "\"program\":{\"arguments\":\"--agent edit\"}"));
        T.True("stale_revision", r.IsError && WriteRig.Code(r) == "stale_revision", r.Text);
        T.Eq("the plan's wording, with the tool to call", "This job was changed 2 minutes ago by Cadence (the app) after you read it. Read it again with cadence_get_job and re-apply your change.", WriteRig.Msg(r));
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.Eq("the owner's version is untouched", xml, rig.Fake.XmlOf("Owner job"));
        T.True("audited refused/stale_revision", rig.SaveAudit.Last() is { Outcome: "refused", Code: "stale_revision" });

        // A wrong revision that never existed is stale as well.
        T.Eq("an invented revision", "stale_revision", WriteRig.Code(rig.Save(Update(Id, "000000000000", "\"program\":{\"arguments\":\"--x\"}"))));

        // CP-R10: an agent that calls itself "Cadence (the app)" is not mistaken for the owner.
        var spoof = new CallContext(new ClientIdentity("spoof", "Cadence (the app)", null), "ffff0000", "2025-11-25");
        using JsonDocument d = JsonDocument.Parse(Update(Id, Rev(rig.Live(Id)), "\"program\":{\"arguments\":\"--spoofed\"}"));
        T.True("the spoofing agent saves", !rig.Host.Call("cadence_save_job", d.RootElement, spoof).IsError);
        ToolResult after = rig.Save(Update(Id, Rev(owner), "\"program\":{\"arguments\":\"--late\"}"));
        T.True("the stale message names it as an AI agent", WriteRig.Msg(after).Contains("by Cadence (the app) (AI agent) after you read it", StringComparison.Ordinal), WriteRig.Msg(after));
    }

    public static void AJobEditedInTaskSchedulerIsNotWritable()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        string drifted = Edit(rig.Fake.XmlOf("Owner job")!, m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) });
        rig.Fake.Seed("Owner job", drifted);
        int writes = rig.Fake.Writes.Count;
        ToolResult r = rig.Save(Update(Id, Rev(rig.Live(Id)), "\"program\":{\"arguments\":\"--x\"}"));
        T.True("not_allowed_in_this_state", r.IsError && WriteRig.Code(r) == "not_allowed_in_this_state", r.Text);
        T.True("the plain reason and the way out", WriteRig.Msg(r).Contains("it was changed in Task Scheduler outside Cadence", StringComparison.Ordinal) && WriteRig.Msg(r).Contains("Ask the owner to open it in Cadence", StringComparison.Ordinal), WriteRig.Msg(r));
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.Eq("the edited task is untouched", drifted, rig.Fake.XmlOf("Owner job"));
        T.True("an invented revision does not change that answer (the state comes first)", WriteRig.Code(rig.Save(Update(Id, "000000000000", "\"enabled\":false"))) == "not_allowed_in_this_state");
        T.True("audited refused/not_allowed_in_this_state", rig.SaveAudit.Last() is { Outcome: "refused", Code: "not_allowed_in_this_state" });
    }

    public static void DamagedAndNewerJobsAreNeverWritableAndAnOutdatedOneIsRefreshed()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        string good = rig.Fake.XmlOf("Owner job")!;

        rig.Fake.Seed("Owner job", EditMeta(good, m => m with { Gen = m.Gen + 1 }));
        string newer = rig.Fake.XmlOf("Owner job")!;
        ToolResult n = rig.Save(Update(Id, "000000000000", "\"enabled\":false"));
        T.True("a newer-version job can't be written (not addressable or not allowed)", n.IsError && WriteRig.Code(n) is "not_found" or "not_allowed_in_this_state", n.Text);
        T.Eq("untouched", newer, rig.Fake.XmlOf("Owner job"));

        rig.Fake.Seed("Owner job", Edit(good, m => m with { RegistrationInfo = m.RegistrationInfo with { Documentation = "not json at all" } }));
        string corrupt = rig.Fake.XmlOf("Owner job")!;
        ToolResult c = rig.Save(Update(Id, "000000000000", "\"enabled\":false"));
        T.True("a damaged job can't be written", c.IsError && WriteRig.Code(c) is "not_found" or "not_allowed_in_this_state", c.Text);
        T.Eq("untouched", corrupt, rig.Fake.XmlOf("Owner job"));
        T.Eq("no write at all happened after the owner's", 1, rig.Fake.Writes.Count);

        // Outdated (written by an older generator): writable, and a save with no change refreshes it.
        rig.Fake.Seed("Owner job", EditMeta(good, m => m with { Gen = 0 }));
        ClassifiedTask outdated = rig.Live(Id);
        T.Eq("fixture: outdated", SyncStatus.Outdated, outdated.Sync);
        JsonObject refreshed = rig.SaveOk(Update(Id, Rev(outdated), "\"enabled\":true"));
        T.Eq("outcome refreshed", "refreshed", refreshed["outcome"]!.GetValue<string>());
        T.Eq("now in sync", "ok", refreshed["job"]!["state"]!.GetValue<string>());
        T.True("audited ok/refreshed", rig.SaveAudit.Last() is { Outcome: "ok", Code: "refreshed" });
    }

    public static void ACopyOfAJobIsNotTheJobAndTheOriginalWins()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Rich());
        string copy = rig.Fake.XmlOf("Owner job")!;
        rig.Fake.Seed("Z copy of it", copy);
        ToolResult r = rig.Save(Update(Id, Rev(rig.Live(Id)), "\"program\":{\"arguments\":\"--only the original\"}"));
        T.True("the original is saved: " + r.Text, !r.IsError);
        T.True("it is the original task that changed", rig.Fake.Writes.Last() == "register Owner job");
        T.Eq("the copy is byte-identical", copy, rig.Fake.XmlOf("Z copy of it"));
    }

    // ---- the per-job lock ----

    public static void ALockedJobIsRefusedAndTheAgentCanNeverChangeTheLock()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich() with { AgentLock = true });
        T.True("fixture: the job is locked", owner.Job!.AgentLock);
        int writes = rig.Fake.Writes.Count;
        string xml = rig.Fake.XmlOf("Owner job")!;

        foreach (string body in new[] { "\"program\":{\"arguments\":\"--x\"}", "\"enabled\":false", "\"name\":\"Renamed\"", "\"schedule\":{\"skipHolidays\":\"none\"}" })
        {
            ToolResult r = rig.Save(Update(Id, Rev(owner), body));
            T.True("locked: " + body, r.IsError && WriteRig.Code(r) == "locked", r.Text);
            T.Eq("the plan's message", "The owner locked this job against changes by AI agents. Tell the user; only they can unlock it in Cadence.", WriteRig.Msg(r));
        }
        T.Eq("an old revision is still 'locked', not stale", "locked", WriteRig.Code(rig.Save(Update(Id, "000000000000", "\"enabled\":false"))));
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.Eq("byte-identical", xml, rig.Fake.XmlOf("Owner job"));
        T.True("every refusal audited as refused/locked", rig.SaveAudit.All(a => a is { Outcome: "refused", Code: "locked" }) && rig.SaveAudit.Count == 5);

        // The lock is not an argument: neither at the top nor in program.
        T.Eq("agentLock is an unknown property", "invalid_argument", WriteRig.Code(rig.Save(Update(Id, Rev(owner), "\"agentLock\":false"))));
        T.Eq("lockedForAgents too", "invalid_argument", WriteRig.Code(rig.Save(Update(Id, Rev(owner), "\"lockedForAgents\":false"))));
        T.True("the job is still locked", rig.Live(Id).Job!.AgentLock);
    }

    public static void AnAgentSaveNeverFlipsTheLockOnAnUnlockedJob()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        rig.SaveOk(Update(Id, Rev(owner), "\"program\":{\"arguments\":\"--b\"}"));
        T.True("still unlocked after an agent save", !rig.Live(Id).Job!.AgentLock);
        JsonObject created = rig.SaveOk(WriteRig.Create("New by agent"));
        T.True("a job created by an agent is unlocked", !rig.Live(created["job"]!["jobId"]!.GetValue<string>()).Job!.AgentLock);
    }

    // ---- other rules on update ----

    public static void LimitsOnlyApplyWhenTheCallSetsTheSchedule()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(WriteRig.Job("Busy", Id, "* * * * *"));   // the owner may schedule every minute
        JsonObject a = rig.SaveOk(Update(Id, Rev(owner), "\"program\":{\"arguments\":\"--change program only\"}"));
        T.Eq("changing only the program of an every-minute job is allowed", "updated", a["outcome"]!.GetValue<string>());
        JsonObject b = rig.SaveOk(Update(Id, a["job"]!["revision"]!.GetValue<string>(), "\"schedule\":{\"skipHolidays\":\"nyse\"}"));
        T.Eq("so is changing only the holidays", "updated", b["outcome"]!.GetValue<string>());
        ToolResult tooOften = rig.Save(Update(Id, b["job"]!["revision"]!.GetValue<string>(), "\"schedule\":{\"cron\":[\"*/2 * * * *\"]}"));
        T.True("but setting an every-2-minutes schedule is refused", tooOften.IsError && WriteRig.Code(tooOften) == "schedule_too_frequent", tooOften.Text);
        T.True("setting the same every-minute schedule is refused too (the call set it)", WriteRig.Code(rig.Save(Update(Id, b["job"]!["revision"]!.GetValue<string>(), "\"schedule\":{\"cron\":[\"* * * * *\"]}"))) == "schedule_too_frequent");
        T.True("an invalid cron on update is schedule_invalid", WriteRig.Code(rig.Save(Update(Id, b["job"]!["revision"]!.GetValue<string>(), "\"schedule\":{\"cron\":[\"@reboot\"]}"))) == "schedule_invalid");
    }

    public static void ProgramRulesApplyToTheFieldsGivenOnUpdate()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        string xml = rig.Fake.XmlOf("Owner job")!;
        string rev = Rev(owner);
        foreach ((string body, string starts) in new[]
        {
            ("\"program\":{\"path\":\"python.exe\"}", "Task Scheduler does not use your PATH."),
            ("\"program\":{\"path\":\"C:\\\\Tools\\\\gone.exe\"}", "The program 'C:\\Tools\\gone.exe' does not exist"),
            ("\"program\":{\"arguments\":\"a\\nb\"}", "The arguments can't contain line breaks"),
            ("\"program\":{\"workingFolder\":\"C:\\\\Nowhere\"}", "The working folder 'C:\\Nowhere' does not exist"),
            ("\"program\":{\"environment\":{\"A=1\":\"x\"}}", "Environment variable name number 1 is not valid"),
        })
        {
            ToolResult r = rig.Save(Update(Id, rev, body));
            T.True("program_rejected: " + starts, r.IsError && WriteRig.Code(r) == "program_rejected" && WriteRig.Msg(r).StartsWith(starts, StringComparison.Ordinal), WriteRig.Code(r) + ": " + WriteRig.Msg(r));
        }
        T.Eq("the job is unchanged", xml, rig.Fake.XmlOf("Owner job"));
        T.Eq("its revision too", rev, Rev(rig.Live(Id)));

        T.True("a call that does not set the path does not re-check it", !rig.Save(Update(Id, rev, "\"program\":{\"arguments\":\"--ok\"}")).IsError);

    }

    public static void RenamingNeedsAFreeNameAndMovesTheTask()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        rig.OwnerSave(WriteRig.Job("Other job", "own00002"));
        int writes = rig.Fake.Writes.Count;

        ToolResult clash = rig.Save(Update(Id, Rev(owner), "\"name\":\"  other JOB \""));
        T.True("another job's name is name_taken", clash.IsError && WriteRig.Code(clash) == "name_taken" && WriteRig.Msg(clash).Contains("(jobId own00002)", StringComparison.Ordinal), clash.Text);
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);

        JsonObject r = rig.SaveOk(Update(Id, Rev(owner), "\"name\":\"Renamed job\""));
        T.Eq("outcome renamed", "renamed", r["outcome"]!.GetValue<string>());
        T.Eq("the id is the same", Id, r["job"]!["jobId"]!.GetValue<string>());
        T.Eq("the task path moved", "\\" + rig.Folder + "\\Renamed job", r["job"]!["taskPath"]!.GetValue<string>());
        T.True("new task first, then the old one removed", rig.Fake.Writes.Skip(writes).SequenceEqual(["register Renamed job", "delete Owner job"]), string.Join("|", rig.Fake.Writes.Skip(writes)));
        T.True("the old task is gone and exactly one task has the id", rig.Fake.XmlOf("Owner job") is null && rig.Jobs().Load(false).Count(c => c.Job?.Id == Id) == 1);

        // A change of letter case only is the job's own name, not a clash.
        JsonObject c = rig.SaveOk(Update(Id, r["job"]!["revision"]!.GetValue<string>(), "\"name\":\"RENAMED JOB\""));
        T.True("a case-only rename is accepted", c["outcome"]!.GetValue<string>() is "updated" or "renamed", c["outcome"]!.GetValue<string>());
        T.Eq("the display name changed", "RENAMED JOB", c["job"]!["name"]!.GetValue<string>());
    }

    public static void ARawTaskAndOtherFoldersStayUntouchedByUpdates()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        string foreign = Plain();
        rig.Fake.Seed("Somebody else", foreign);
        rig.Fake.SeedOther("\\Microsoft\\Windows\\Other\\Thing", Plain());
        rig.SaveOk(Update(Id, Rev(owner), "\"program\":{\"arguments\":\"--ok\"}"));
        T.Eq("the foreign task in the folder is byte-identical", foreign, rig.Fake.XmlOf("Somebody else"));
        T.True("only the job's own task was written", rig.Fake.Writes.Skip(1).All(w => w == "register Owner job"));

        // The foreign task has no job id: nothing can name it. Names and guesses are all not_found or invalid.
        foreach (string guess in new[] { "somebody", "Somebody else", "00000000", "zzzzzzzz" })
        {
            ToolResult r = rig.Save(Update(guess, "0123456789ab", "\"enabled\":false"));
            T.True("'" + guess + "': invalid_argument or not_found", r.IsError && WriteRig.Code(r) is "invalid_argument" or "not_found", r.Text);
        }
        T.Eq("still byte-identical", foreign, rig.Fake.XmlOf("Somebody else"));
    }

    public static void TheSecondSuspensionCheckProtectsUpdatesToo()
    {
        FlipReader? flip = null;
        using var rig = new WriteRig(store => flip = new FlipReader(store, 2));
        ClassifiedTask owner = rig.OwnerSave(Rich());
        string xml = rig.Fake.XmlOf("Owner job")!;
        ToolResult r = rig.Save(Update(Id, Rev(owner), "\"program\":{\"arguments\":\"--raced\"}"));
        T.True("refused as suspended at the second check", r.IsError && WriteRig.Code(r) == "suspended", r.Text);
        T.Eq("the policy was read twice", 2, flip!.Reads);
        T.Eq("the task is unchanged", xml, rig.Fake.XmlOf("Owner job"));
        T.Eq("only the owner's write happened", 1, rig.Fake.Writes.Count);
    }

    public static void ALockSetBetweenPlanningAndWritingIsObeyedByTheApplier()
    {
        // The agent plans against an unlocked job; the owner locks it; the agent's write is refused inside the writer lock and nothing is written.
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Rich());
        ManagedJobs agent = rig.Jobs(Actor.Agent("Cadence Test Agent"));
        PlannedChange plan = agent.PlanSave(Rich() with { Run = Rich().Run with { Args = "--late" } }, "Owner job", Rev(owner));
        T.True("fixture: the plan writes", plan.NewXml is not null);

        // The owner locks the job (a save through the app).
        T.True("the owner locks it", new Cadence.Scheduler.Apply.Applier(rig.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(Rich() with { AgentLock = true }, "Owner job").Success);
        string xml = rig.Fake.XmlOf("Owner job")!;
        int writes = rig.Fake.Writes.Count;
        var res = new Cadence.Scheduler.Apply.Applier(agent, TimeSpan.FromSeconds(5)).Apply(plan);
        T.True("refused as Locked", !res.Success && res.Kind == Cadence.Scheduler.Apply.ApplyFailure.Locked, res.Message);
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.Eq("byte-identical", xml, rig.Fake.XmlOf("Owner job"));
    }
}
