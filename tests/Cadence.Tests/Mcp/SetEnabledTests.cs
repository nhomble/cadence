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
/// Phase 22 (PLAN 12.3.2): <c>cadence_set_job_enabled</c>: a save with Enabled flipped through the shared write pipeline. Idempotent ("unchanged", nothing written, decided
/// before the revision), attributed and audited, refused while suspended (both checks), for a locked job, for jobs a human touched and for stale revisions.
/// </summary>
public static class SetEnabledTests
{
    const string Id = "own00001";
    const string Tool = "cadence_set_job_enabled";

    static string Args(string id, bool enabled, string? revision = null) =>
        "{\"jobId\":\"" + id + "\",\"enabled\":" + (enabled ? "true" : "false") + (revision is null ? "" : ",\"expectedRevision\":\"" + revision + "\"") + "}";

    static JobDefinition Owner() => WriteRig.Job("Owner job", Id);

    static IReadOnlyList<Cadence.Agent.Audit.AuditEntry> AuditOf(WriteRig rig) => [.. rig.Audit.All.Where(a => a.Tool == Tool)];

    public static void TurnsAJobOffAndOnAndSaysWhatChanged()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Owner());
        int writes = rig.Fake.Writes.Count;

        ToolResult off = rig.Call(Tool, Args(Id, false, JobRevision.Of(owner)));
        T.True("not an error: " + off.Text, !off.IsError);
        JsonObject o = off.Structured!;
        T.Eq("outcome", "updated", o["outcome"]!.GetValue<string>());
        T.Eq("the job is disabled in the view", false, o["job"]!["enabled"]!.GetValue<bool>());
        T.Eq("one write", writes + 1, rig.Fake.Writes.Count);
        T.Eq("it is the job's own task", "register Owner job", rig.Fake.Writes.Last());
        ClassifiedTask live = rig.Live(Id);
        T.True("disabled in the stored job, in sync", !live.Job!.Enabled && live.Sync == SyncStatus.InSync);
        T.Eq("attributed to the agent", "agent:Cadence Test Agent", live.Metadata!.UpdatedBy);
        T.Eq("the creator is kept", "app", live.Metadata.CreatedBy);
        T.Eq("the response's revision is the live one", JobRevision.Of(live), o["job"]!["revision"]!.GetValue<string>());
        T.True("audited ok/disabled with the id and name", AuditOf(rig) is [{ Outcome: "ok", Code: "disabled", JobId: Id, JobName: "Owner job" }]);

        // Nothing else changed: the program, the schedule and the lock are as before.
        T.Eq("program kept", owner.Job!.Run.Exe, live.Job.Run.Exe);
        T.Eq("cron kept", owner.Job.Schedule.CronLines[0], live.Job.Schedule.CronLines[0]);
        T.True("not locked", !live.Job.AgentLock);

        ToolResult on = rig.Call(Tool, Args(Id, true, JobRevision.Of(live)));
        T.True("on again: " + on.Text, !on.IsError && on.Structured!["outcome"]!.GetValue<string>() == "updated" && on.Structured["job"]!["enabled"]!.GetValue<bool>());
        T.True("audited ok/enabled", AuditOf(rig).Last() is { Outcome: "ok", Code: "enabled" });
    }

    public static void ARevisionIsOptional()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        T.True("no revision: allowed", !rig.Call(Tool, Args(Id, false)).IsError);
        T.True("disabled", !rig.Live(Id).Job!.Enabled);
    }

    public static void TheSameStateIsUnchangedAndNothingIsWritten()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Owner());
        int writes = rig.Fake.Writes.Count;
        string xml = rig.Fake.XmlOf("Owner job")!;

        ToolResult same = rig.Call(Tool, Args(Id, true));
        T.True("already on: " + same.Text, !same.IsError);
        T.Eq("outcome unchanged", "unchanged", same.Structured!["outcome"]!.GetValue<string>());
        T.True("the message says so", same.Structured["message"]!.GetValue<string>().Contains("already turned on", StringComparison.Ordinal));
        T.Eq("the job is in the result", Id, same.Structured["job"]!["jobId"]!.GetValue<string>());
        T.Eq("no write", writes, rig.Fake.Writes.Count);

        T.Eq("unchanged is decided before the revision: an invented one is fine", "unchanged", rig.Call(Tool, Args(Id, true, "000000000000")).Structured!["outcome"]!.GetValue<string>());
        T.Eq("byte-identical", xml, rig.Fake.XmlOf("Owner job"));
        T.Eq("the revision did not move", JobRevision.Of(owner), JobRevision.Of(rig.Live(Id)));
        T.True("audited ok/unchanged each time", AuditOf(rig).All(a => a is { Outcome: "ok", Code: "unchanged" }) && AuditOf(rig).Count == 2);

        rig.Call(Tool, Args(Id, false));
        int after = rig.Fake.Writes.Count;
        T.Eq("already off is unchanged too", "unchanged", rig.Call(Tool, Args(Id, false)).Structured!["outcome"]!.GetValue<string>());
        T.Eq("still no write", after, rig.Fake.Writes.Count);
    }

    public static void AStaleRevisionIsRefused()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Owner());
        string seen = JobRevision.Of(owner);
        ApplyResult edit = new Applier(rig.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(Owner() with { Run = Owner().Run with { Args = "--changed" } }, "Owner job");
        T.True("fixture: the owner edits the job", edit.Success, edit.Message);
        rig.Now = WriteRig.FixedNow.AddMinutes(2);
        int writes = rig.Fake.Writes.Count;
        string xml = rig.Fake.XmlOf("Owner job")!;

        ToolResult r = rig.Call(Tool, Args(Id, false, seen));
        T.True("stale_revision", r.IsError && WriteRig.Code(r) == "stale_revision", r.Text);
        T.Eq("it names who changed the job", "This job was changed 2 minutes ago by Cadence (the app) after you read it. Read it again with cadence_get_job and re-apply your change.", WriteRig.Msg(r));
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.Eq("byte-identical", xml, rig.Fake.XmlOf("Owner job"));
        T.True("audited refused/stale_revision", AuditOf(rig) is [{ Outcome: "refused", Code: "stale_revision" }]);
        T.True("with the current revision it works", !rig.Call(Tool, Args(Id, false, JobRevision.Of(rig.Live(Id)))).IsError);
    }

    public static void SuspendedRefusesBeforeAnythingElse()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        rig.Policy.SetSuspended(true, "app", WriteRig.FixedNow.AddMinutes(-5));
        int writes = rig.Fake.Writes.Count;
        ToolResult r = rig.Call(Tool, Args(Id, false));
        T.True("suspended", r.IsError && WriteRig.Code(r) == "suspended" && WriteRig.Msg(r).StartsWith("AI agents are suspended on this PC", StringComparison.Ordinal), r.Text);
        T.Eq("unknown job ids get the same answer (suspension first)", "suspended", WriteRig.Code(rig.Call(Tool, Args("zzzzzzzz", true))));
        T.Eq("a call that would be unchanged is refused too", "suspended", WriteRig.Code(rig.Call(Tool, Args(Id, true))));
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.True("audited refused/suspended", AuditOf(rig).All(a => a is { Outcome: "refused", Code: "suspended" }) && AuditOf(rig).Count == 3);
        rig.Policy.SetSuspended(false, "app", WriteRig.FixedNow);
        T.True("resumed: works on the next call", !rig.Call(Tool, Args(Id, false)).IsError);
    }

    public static void TheSecondSuspensionCheckRunsInsideTheWriterLock()
    {
        FlipReader? flip = null;
        using var rig = new WriteRig(store => flip = new FlipReader(store, 2));
        rig.OwnerSave(Owner());
        string xml = rig.Fake.XmlOf("Owner job")!;
        ToolResult r = rig.Call(Tool, Args(Id, false));
        T.True("refused as suspended at the second check", r.IsError && WriteRig.Code(r) == "suspended", r.Text);
        T.Eq("read twice", 2, flip!.Reads);
        T.Eq("task unchanged", xml, rig.Fake.XmlOf("Owner job"));
        T.Eq("only the owner's write happened", 1, rig.Fake.Writes.Count);
        T.True("audited once", AuditOf(rig) is [{ Outcome: "refused", Code: "suspended" }]);
    }

    public static void ALockedJobIsRefusedAndStaysByteIdentical()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Owner() with { AgentLock = true });
        string xml = rig.Fake.XmlOf("Owner job")!;
        int writes = rig.Fake.Writes.Count;
        foreach (bool want in new[] { false, true })
        {
            ToolResult r = rig.Call(Tool, Args(Id, want, JobRevision.Of(owner)));
            T.True("locked (enabled=" + want + ")", r.IsError && WriteRig.Code(r) == "locked", r.Text);
            T.Eq("the plan's message", ManagedJobs.AgentLockedMessage, WriteRig.Msg(r));
        }
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.Eq("byte-identical", xml, rig.Fake.XmlOf("Owner job"));
        T.True("audited refused/locked", AuditOf(rig).All(a => a is { Outcome: "refused", Code: "locked" }) && AuditOf(rig).Count == 2);
    }

    public static void ALockSetBetweenPlanningAndWritingIsObeyed()
    {
        WriteRig? holder = null;
        using var rig = new WriteRig(store => new FlipReader(store, 2, () =>
            T.True("the owner locks it", new Applier(holder!.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(Owner() with { AgentLock = true }, "Owner job").Success)));
        holder = rig;
        rig.OwnerSave(Owner());
        ToolResult r = rig.Call(Tool, Args(Id, false));
        T.True("refused as locked", r.IsError && WriteRig.Code(r) == "locked", r.Text);
        T.True("the job is still enabled and now locked", rig.Live(Id).Job is { Enabled: true, AgentLock: true });
    }

    public static void JobsAHumanTouchedAndMissingJobsAreRefused()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Owner());
        string good = rig.Fake.XmlOf("Owner job")!;

        T.Eq("an unknown id", "not_found", WriteRig.Code(rig.Call(Tool, Args("zzzzzzzz", false))));

        string drifted = Edit(good, m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) });
        rig.Fake.Seed("Owner job", drifted);
        ToolResult d = rig.Call(Tool, Args(Id, false));
        T.True("drifted: not_allowed_in_this_state", d.IsError && WriteRig.Code(d) == "not_allowed_in_this_state" && WriteRig.Msg(d).Contains("changed in Task Scheduler outside Cadence", StringComparison.Ordinal), d.Text);
        T.Eq("untouched", drifted, rig.Fake.XmlOf("Owner job"));
        T.Eq("the same for 'unchanged' (the state comes first)", "not_allowed_in_this_state", WriteRig.Code(rig.Call(Tool, Args(Id, true))));

        rig.Fake.Seed("Owner job", EditMeta(good, m => m with { Gen = m.Gen + 1 }));
        string newer = rig.Fake.XmlOf("Owner job")!;
        T.True("newer version: refused", WriteRig.Code(rig.Call(Tool, Args(Id, false))) is "not_found" or "not_allowed_in_this_state");
        T.Eq("untouched", newer, rig.Fake.XmlOf("Owner job"));

        rig.Fake.Seed("Owner job", good);
        rig.Fake.Seed("Z copy", good);
        T.True("the original is changed, the copy is not", !rig.Call(Tool, Args(Id, false)).IsError && rig.Fake.XmlOf("Z copy") == good && rig.Fake.Writes.Last() == "register Owner job");
        _ = owner;
    }

    public static void ArgumentsAreValidated()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        int writes = rig.Fake.Writes.Count;
        (string Json, string Contains)[] cases =
        [
            ("{}", "jobId is required"),
            ("{\"jobId\":\"" + Id + "\"}", "enabled is required"),
            ("{\"enabled\":true}", "jobId is required"),
            ("{\"jobId\":\"" + Id + "\",\"enabled\":\"yes\"}", "enabled must be true or false"),
            ("{\"jobId\":\"" + Id + "\",\"enabled\":null}", "enabled must not be null"),
            ("{\"jobId\":\"OWN00001\",\"enabled\":true}", "jobId"),
            ("{\"jobId\":\"../../x\",\"enabled\":true}", "jobId"),
            ("{\"jobId\":\"" + Id + "\",\"enabled\":true,\"expectedRevision\":\"xyz\"}", "expectedRevision"),
            ("{\"jobId\":\"" + Id + "\",\"enabled\":true,\"agentLock\":false}", "Unknown property 'agentLock'"),
            ("{\"jobId\":\"" + Id + "\",\"enabled\":true,\"name\":\"x\"}", "Unknown property 'name'"),
            ("[]", "must be a JSON object"),
        ];
        foreach ((string json, string contains) in cases)
        {
            ToolResult r = rig.Call(Tool, json);
            T.True(json + ": invalid_argument containing '" + contains + "'", r.IsError && WriteRig.Code(r) == "invalid_argument" && WriteRig.Msg(r).Contains(contains, StringComparison.Ordinal), WriteRig.Code(r) + ": " + WriteRig.Msg(r));
        }
        T.Eq("nothing written", writes, rig.Fake.Writes.Count);
        T.True("each malformed call was audited once as refused/invalid_argument", AuditOf(rig).Count == cases.Length && AuditOf(rig).All(a => a is { Outcome: "refused", Code: "invalid_argument" }));
    }

    public static void ARawTaskInTheFolderIsNeverTouched()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        string foreign = Plain();
        rig.Fake.Seed("Somebody else", foreign);
        rig.Fake.SeedOther("\\Microsoft\\Windows\\Other\\Thing", Plain());
        T.True("the job itself can be switched", !rig.Call(Tool, Args(Id, false)).IsError);
        foreach (string guess in new[] { "somebody", "00000000", "zzzzzzzz" })
            T.True("guessed id " + guess, rig.Call(Tool, Args(guess, false)).IsError);
        T.Eq("the foreign task is byte-identical", foreign, rig.Fake.XmlOf("Somebody else"));
        T.True("only the job's own task was written", rig.Fake.Writes.Skip(1).All(w => w == "register Owner job"));
    }
}
