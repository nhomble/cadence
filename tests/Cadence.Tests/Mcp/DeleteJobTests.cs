using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests.Mcp;

/// <summary>
/// Phase 22 (PLAN 12.3.2): <c>cadence_delete_job</c>. One job per call, by id, with the job's exact current name as confirmation; only that job's task and its run-history folder
/// go. A missing job is "alreadyGone" (idempotent). Refused while suspended (both checks), for a locked job, for jobs a human touched (drifted, copy, damaged, newer) and
/// for a stale job; limited to 10 deletes a minute. There is no wildcard, no bulk form and no way to name a task by path.
/// </summary>
public static class DeleteJobTests
{
    const string Id = "own00001";
    const string Tool = "cadence_delete_job";

    static string Args(string id, string name) => "{\"jobId\":" + System.Text.Json.JsonSerializer.Serialize(id) + ",\"confirmName\":" + System.Text.Json.JsonSerializer.Serialize(name) + "}";

    static JobDefinition Owner(string name = "Owner job", string id = Id) => WriteRig.Job(name, id);

    static IReadOnlyList<Cadence.Agent.Audit.AuditEntry> AuditOf(WriteRig rig) => [.. rig.Audit.All.Where(a => a.Tool == Tool)];

    static string History(WriteRig rig, string id) => System.IO.Path.Combine(rig.Temp, "runs", id);

    static void MakeHistory(WriteRig rig, string id)
    {
        Directory.CreateDirectory(History(rig, id));
        File.WriteAllText(System.IO.Path.Combine(History(rig, id), "20261001-090000-000.json"), "{}");
    }

    public static void DeletesTheTaskAndItsHistoryOnly()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        rig.OwnerSave(Owner("Other job", "own00002"));
        MakeHistory(rig, Id);
        MakeHistory(rig, "own00002");
        string otherXml = rig.Fake.XmlOf("Other job")!;
        int before = rig.Fake.Writes.Count;

        ToolResult res = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("not an error: " + res.Text, !res.IsError);
        JsonObject r = res.Structured!;
        T.Eq("deleted", true, r["deleted"]!.GetValue<bool>());
        T.Eq("jobId", Id, r["jobId"]!.GetValue<string>());
        T.Eq("name", "Owner job", r["name"]!.GetValue<string>());
        T.Eq("historyRemoved", true, r["historyRemoved"]!.GetValue<bool>());
        T.True("exactly the four documented properties", r.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(["deleted", "historyRemoved", "jobId", "name"]));
        T.True("only that task was deleted", rig.Fake.Writes.Skip(before).SequenceEqual(["delete Owner job"]), string.Join("|", rig.Fake.Writes.Skip(before)));
        T.True("the task is gone", rig.Fake.XmlOf("Owner job") is null);
        T.True("its history folder is gone", !Directory.Exists(History(rig, Id)));
        T.True("the other job and its history are untouched", rig.Fake.XmlOf("Other job") == otherXml && Directory.Exists(History(rig, "own00002")));
        T.True("audited ok/deleted with the id and name", AuditOf(rig) is [{ Outcome: "ok", Code: "deleted", JobId: Id, JobName: "Owner job" }]);

        ToolResult again = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("repeat: not an error", !again.IsError);
        T.Eq("alreadyGone", true, again.Structured!["alreadyGone"]!.GetValue<bool>());
        T.Eq("not deleted this time", false, again.Structured["deleted"]!.GetValue<bool>());
        T.True("audited ok/alreadyGone", AuditOf(rig).Last() is { Outcome: "ok", Code: "alreadyGone" });
        T.True("nothing more was written", rig.Fake.Writes.Skip(before).SequenceEqual(["delete Owner job"]));
    }

    public static void AMissingJobIsAlreadyGoneAndNothingIsTouched()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        MakeHistory(rig, "zzzzzzzz");   // an orphan folder of some other id: never touched by a delete of a job that is not there
        int writes = rig.Fake.Writes.Count;
        ToolResult r = rig.Call(Tool, Args("zzzzzzzz", "whatever"));
        T.True("alreadyGone, not an error: " + r.Text, !r.IsError && r.Structured!["alreadyGone"]!.GetValue<bool>());
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.True("the orphan folder is left alone", Directory.Exists(History(rig, "zzzzzzzz")));
    }

    public static void AWrongNameIsRefusedAndNamesTheRealJob()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner("Backup notes"));
        MakeHistory(rig, Id);
        int writes = rig.Fake.Writes.Count;
        ToolResult r = rig.Call(Tool, Args(Id, "Backup"));
        T.True("invalid_argument", r.IsError && WriteRig.Code(r) == "invalid_argument", r.Text);
        T.True("names the real job and the wrong one", WriteRig.Msg(r).StartsWith("jobId own00001 is the job 'Backup notes', not 'Backup'. Check the id with cadence_list_jobs.", StringComparison.Ordinal), WriteRig.Msg(r));
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.True("task and history intact", rig.Fake.XmlOf("Backup notes") is not null && Directory.Exists(History(rig, Id)));
        T.True("audited refused/invalid_argument", AuditOf(rig) is [{ Outcome: "refused", Code: "invalid_argument", JobId: Id }]);

        foreach (string wrong in new[] { "backup notes", "Backup notes!", "Backup  notes", "*", "Backup*", "Backup notes\u0000", "..\\Backup notes" })
            T.True("not the job's name: '" + wrong + "'", rig.Call(Tool, Args(Id, wrong)).IsError);
        T.True("a name with spaces around it is the same name (ordinal after trim)", !rig.Call(Tool, Args(Id, "  Backup notes  ")).IsError);
        T.True("and then it is gone", rig.Fake.XmlOf("Backup notes") is null);
    }

    public static void SuspendedRefusesAtBothChecks()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        MakeHistory(rig, Id);
        rig.Policy.SetSuspended(true, "app", WriteRig.FixedNow.AddMinutes(-5));
        int writes = rig.Fake.Writes.Count;
        ToolResult r = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("suspended", r.IsError && WriteRig.Code(r) == "suspended" && WriteRig.Msg(r).StartsWith("AI agents are suspended on this PC", StringComparison.Ordinal), r.Text);
        T.Eq("a missing id gets the same answer (suspension first)", "suspended", WriteRig.Code(rig.Call(Tool, Args("zzzzzzzz", "x"))));
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.True("history intact", Directory.Exists(History(rig, Id)));
        rig.Policy.SetSuspended(false, "app", WriteRig.FixedNow);

        FlipReader? flip = null;
        using var raced = new WriteRig(store => flip = new FlipReader(store, 2));
        raced.OwnerSave(Owner());
        MakeHistory(raced, Id);
        ToolResult late = raced.Call(Tool, Args(Id, "Owner job"));
        T.True("flipped between the checks: suspended", late.IsError && WriteRig.Code(late) == "suspended", late.Text);
        T.Eq("read twice", 2, flip!.Reads);
        T.True("the task and its history survive", raced.Fake.XmlOf("Owner job") is not null && Directory.Exists(History(raced, Id)));
        T.Eq("no delete reached the gateway", 0, raced.Fake.Writes.Count(w => w.StartsWith("delete ", StringComparison.Ordinal)));
    }

    public static void ALockedJobIsNeverDeleted()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner() with { AgentLock = true });
        MakeHistory(rig, Id);
        string xml = rig.Fake.XmlOf("Owner job")!;
        ToolResult r = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("locked", r.IsError && WriteRig.Code(r) == "locked" && WriteRig.Msg(r) == ManagedJobs.AgentLockedMessage, r.Text);
        T.True("the wrong name does not change the answer for the lock (the lock comes first)", WriteRig.Code(rig.Call(Tool, Args(Id, "nope"))) == "locked");
        T.True("byte-identical and history intact", rig.Fake.XmlOf("Owner job") == xml && Directory.Exists(History(rig, Id)));
        T.Eq("no write", 1, rig.Fake.Writes.Count);
        T.True("audited refused/locked", AuditOf(rig).All(a => a is { Outcome: "refused", Code: "locked" }) && AuditOf(rig).Count == 2);
    }

    public static void ALockSetBetweenPlanningAndDeletingIsObeyed()
    {
        WriteRig? holder = null;
        using var rig = new WriteRig(store => new FlipReader(store, 2, () =>
            T.True("the owner locks it", new Applier(holder!.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(Owner() with { AgentLock = true }, "Owner job").Success)));
        holder = rig;
        rig.OwnerSave(Owner());
        MakeHistory(rig, Id);
        ToolResult r = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("refused as locked inside the writer lock (the revision moved too: either way nothing is deleted)", r.IsError && WriteRig.Code(r) is "locked" or "stale_revision" or "suspended", r.Text);
        T.True("task and history survive", rig.Fake.XmlOf("Owner job") is not null && Directory.Exists(History(rig, Id)));
    }

    public static void AJobChangedAfterItWasReadIsNotDeleted()
    {
        WriteRig? holder = null;
        using var rig = new WriteRig(store => new FlipReader(store, 2, () =>
            T.True("the owner edits it", new Applier(holder!.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(Owner() with { Run = Owner().Run with { Args = "--changed" } }, "Owner job").Success)));
        holder = rig;
        rig.OwnerSave(Owner());
        MakeHistory(rig, Id);
        ToolResult r = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("stale_revision", r.IsError && WriteRig.Code(r) == "stale_revision" && WriteRig.Msg(r).Contains("by Cadence (the app)", StringComparison.Ordinal), r.Text);
        T.True("task and history survive", rig.Fake.XmlOf("Owner job") is not null && Directory.Exists(History(rig, Id)));
    }

    public static void JobsAHumanTouchedAreNotDeletable()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        MakeHistory(rig, Id);
        string good = rig.Fake.XmlOf("Owner job")!;

        string drifted = Edit(good, m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) });
        rig.Fake.Seed("Owner job", drifted);
        ToolResult d = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("drifted: not_allowed_in_this_state", d.IsError && WriteRig.Code(d) == "not_allowed_in_this_state" && WriteRig.Msg(d).Contains("Ask the owner to open it in Cadence", StringComparison.Ordinal), d.Text);
        T.Eq("untouched", drifted, rig.Fake.XmlOf("Owner job"));

        string newer = EditMeta(good, m => m with { Gen = m.Gen + 1 });
        rig.Fake.Seed("Owner job", newer);
        ToolResult n = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("newer version: refused, or not addressable (alreadyGone); never deleted", n.IsError || n.Structured!["deleted"]!.GetValue<bool>() == false, n.Text);
        T.Eq("untouched", newer, rig.Fake.XmlOf("Owner job"));

        string damaged = Edit(good, m => m with { RegistrationInfo = m.RegistrationInfo with { Documentation = "not json at all" } });
        rig.Fake.Seed("Owner job", damaged);
        rig.Call(Tool, Args(Id, "Owner job"));
        T.Eq("a damaged job is untouched", damaged, rig.Fake.XmlOf("Owner job"));
        T.True("its history is intact", Directory.Exists(History(rig, Id)));
        T.Eq("no delete reached the gateway", 0, rig.Fake.Writes.Count(w => w.StartsWith("delete ", StringComparison.Ordinal)));
    }

    public static void ACopyIsNotDeletedAndTheOriginalWins()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        string good = rig.Fake.XmlOf("Owner job")!;
        rig.Fake.Seed("Z copy", good);
        ToolResult r = rig.Call(Tool, Args(Id, "Owner job"));
        T.True("the original is deleted: " + r.Text, !r.IsError && r.Structured!["deleted"]!.GetValue<bool>());
        T.True("the copy is byte-identical and still there", rig.Fake.XmlOf("Z copy") == good && rig.Fake.XmlOf("Owner job") is null);
        T.Eq("only the original was deleted", 1, rig.Fake.Writes.Count(w => w.StartsWith("delete ", StringComparison.Ordinal)));
    }

    public static void ARawTaskCannotBeDeletedByAnyId()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        string foreign = Plain();
        rig.Fake.Seed("Somebody else", foreign);
        rig.Fake.SeedOther("\\Microsoft\\Windows\\Other\\Thing", Plain());
        foreach (string guess in new[] { "somebody", "Somebody else", "00000000", "zzzzzzzz", "..\\x", "*", "\\Microsoft\\Windows\\Other\\Thing" })
            foreach (string name in new[] { "Somebody else", "*", "Thing" })
            {
                ToolResult r = rig.Call(Tool, Args(guess, name));
                T.True("id '" + guess + "' / name '" + name + "': refused or alreadyGone", r.IsError || r.Structured!["alreadyGone"]?.GetValue<bool>() == true);
            }
        T.Eq("the foreign task is byte-identical", foreign, rig.Fake.XmlOf("Somebody else"));
        T.True("no delete reached the gateway", rig.Fake.Writes.Count(w => w.StartsWith("delete ", StringComparison.Ordinal)) == 0);
    }

    public static void AtMostTenDeletesAMinute()
    {
        using var rig = new WriteRig();
        for (int i = 0; i < 12; i++) rig.OwnerSave(Owner("Job " + i, "own000" + (10 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        for (int i = 0; i < 10; i++)
            T.True("delete " + i + " is allowed", !rig.Call(Tool, Args("own000" + (10 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "Job " + i)).IsError);
        ToolResult r = rig.Call(Tool, Args("own00020", "Job 10"));
        T.True("the 11th within a minute is rate_limited", r.IsError && WriteRig.Code(r) == "rate_limited", r.Text);
        T.True("that job is still there", rig.Fake.XmlOf("Job 10") is not null);
        rig.Now = WriteRig.FixedNow.AddSeconds(61);
        T.True("a minute later it works", !rig.Call(Tool, Args("own00020", "Job 10")).IsError);
    }

    public static void AWrongNameDoesNotUseUpTheDeleteQuota()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        for (int i = 0; i < 12; i++)
            T.Eq("wrong name #" + i, "invalid_argument", WriteRig.Code(rig.Call(Tool, Args(Id, "wrong " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        T.True("the right name still deletes", !rig.Call(Tool, Args(Id, "Owner job")).IsError && rig.Fake.XmlOf("Owner job") is null);
    }

    public static void ArgumentsAreValidated()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        (string Json, string Contains)[] cases =
        [
            ("{}", "jobId is required"),
            ("{\"jobId\":\"" + Id + "\"}", "confirmName is required"),
            ("{\"confirmName\":\"Owner job\"}", "jobId is required"),
            ("{\"jobId\":\"" + Id + "\",\"confirmName\":\"\"}", "confirmName must be"),
            ("{\"jobId\":\"" + Id + "\",\"confirmName\":\"   \"}", "confirmName must be the job's name"),
            ("{\"jobId\":\"" + Id + "\",\"confirmName\":\"" + new string('x', 101) + "\"}", "confirmName must be"),
            ("{\"jobId\":\"" + Id + "\",\"confirmName\":5}", "confirmName must be a string"),
            ("{\"jobId\":\"" + Id + "\",\"confirmName\":\"Owner job\",\"all\":true}", "Unknown property 'all'"),
            ("{\"jobId\":\"" + Id + "\",\"confirmName\":\"Owner job\",\"force\":true}", "Unknown property 'force'"),
            ("{\"jobId\":\"*\",\"confirmName\":\"Owner job\"}", "jobId"),
            ("{\"jobId\":[\"" + Id + "\"],\"confirmName\":\"Owner job\"}", "jobId must be a string"),
            ("{\"jobId\":\"ab\",\"confirmName\":\"Owner job\"}", "jobId"),
        ];
        foreach ((string json, string contains) in cases)
        {
            ToolResult r = rig.Call(Tool, json);
            T.True(json + ": invalid_argument containing '" + contains + "'", r.IsError && WriteRig.Code(r) == "invalid_argument" && WriteRig.Msg(r).Contains(contains, StringComparison.Ordinal), WriteRig.Code(r) + ": " + WriteRig.Msg(r));
        }
        T.True("the job is still there", rig.Fake.XmlOf("Owner job") is not null);
        T.Eq("nothing was written after the owner's save", 1, rig.Fake.Writes.Count);
    }

    public static void TheNameInTheMessageIsCleanedAndCapped()
    {
        using var rig = new WriteRig();
        char rlo = (char)0x202E, sep = (char)0x2028;
        rig.OwnerSave(Owner("Report " + rlo + "evil" + sep + "x"));
        ToolResult r = rig.Call(Tool, Args(Id, "other"));
        T.True("refused", r.IsError && WriteRig.Code(r) == "invalid_argument", r.Text);
        T.True("no bidi or line separator characters in the message", !WriteRig.Msg(r).Contains(rlo) && !WriteRig.Msg(r).Contains(sep), WriteRig.Msg(r));
    }
}
