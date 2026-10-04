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
/// Phase 22 (PLAN 12.3.2): <c>cadence_run_job_now</c>. It starts one existing job through the gateway and writes nothing: no Register, no Delete. It is refused while agents
/// are suspended (checked twice), for a locked job (checked live right before the start), for a disabled or already running job and for jobs a human touched, and it is
/// rate limited to one start per job per 30 seconds.
/// </summary>
public static class RunNowTests
{
    const string Id = "own00001";
    const string Tool = "cadence_run_job_now";

    static string Args(string id) => "{\"jobId\":" + System.Text.Json.JsonSerializer.Serialize(id) + "}";

    static JobDefinition Owner(string name = "Owner job", string id = Id) => WriteRig.Job(name, id);

    static IReadOnlyList<Cadence.Agent.Audit.AuditEntry> AuditOf(WriteRig rig) => [.. rig.Audit.All.Where(a => a.Tool == Tool)];

    static int Runs(WriteRig rig) => rig.Fake.Writes.Count(w => w.StartsWith("run ", StringComparison.Ordinal));

    public static void StartsTheJobOnceAndWritesNothing()
    {
        using var rig = new WriteRig();
        ClassifiedTask owner = rig.OwnerSave(Owner());
        string xml = rig.Fake.XmlOf("Owner job")!;
        int before = rig.Fake.Writes.Count;

        ToolResult res = rig.Call(Tool, Args(Id));
        T.True("not an error: " + res.Text, !res.IsError);
        JsonObject r = res.Structured!;
        T.Eq("started", true, r["started"]!.GetValue<bool>());
        T.Eq("jobId", Id, r["jobId"]!.GetValue<string>());
        T.Eq("startedAt is the clock, ISO with offset", "2026-10-01T13:00:00+00:00", r["startedAt"]!.GetValue<string>());
        T.Eq("checkAfterSeconds", 5, r["checkAfterSeconds"]!.GetValue<int>());
        T.Eq("note", "Call cadence_get_job to see the result.", r["note"]!.GetValue<string>());
        T.True("exactly the five documented properties", r.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(["checkAfterSeconds", "jobId", "note", "started", "startedAt"]));
        T.True("the gateway was asked to run the job's own task, once, and nothing else was written", rig.Fake.Writes.Skip(before).SequenceEqual(["run Owner job"]), string.Join("|", rig.Fake.Writes.Skip(before)));
        T.Eq("the task is byte-identical", xml, rig.Fake.XmlOf("Owner job"));
        T.Eq("the revision did not move", JobRevision.Of(owner), JobRevision.Of(rig.Live(Id)));
        T.True("audited ok/started with the job", AuditOf(rig) is [{ Outcome: "ok", Code: "started", JobId: Id, JobName: "Owner job" }]);
    }

    public static void OnlyOneStartPerJobPer30Seconds()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        rig.OwnerSave(Owner("Other job", "own00002"));
        T.True("first start", !rig.Call(Tool, Args(Id)).IsError);

        rig.Now = WriteRig.FixedNow.AddSeconds(10);
        ToolResult again = rig.Call(Tool, Args(Id));
        T.True("the second start within 30 s is rate_limited", again.IsError && WriteRig.Code(again) == "rate_limited", again.Text);
        T.True("the message gives the wait", WriteRig.Msg(again).Contains("Wait ", StringComparison.Ordinal) && WriteRig.Msg(again).Contains("seconds", StringComparison.Ordinal), WriteRig.Msg(again));
        T.Eq("not started again", 1, Runs(rig));
        T.True("another job is not limited", !rig.Call(Tool, Args("own00002")).IsError);
        T.Eq("two starts", 2, Runs(rig));

        rig.Now = WriteRig.FixedNow.AddSeconds(31);
        T.True("after 30 s the first job can be started again", !rig.Call(Tool, Args(Id)).IsError);
        T.Eq("three starts", 3, Runs(rig));
        T.True("the refusal was audited refused/rate_limited", AuditOf(rig).Count(a => a is { Outcome: "refused", Code: "rate_limited" }) == 1);
    }

    public static void ADisabledJobIsRefusedWithTheWayOut()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner() with { Enabled = false });
        ToolResult r = rig.Call(Tool, Args(Id));
        T.True("not_allowed_in_this_state", r.IsError && WriteRig.Code(r) == "not_allowed_in_this_state", r.Text);
        T.True("tells how to turn it on", WriteRig.Msg(r).Contains("Turn it on first with cadence_set_job_enabled", StringComparison.Ordinal), WriteRig.Msg(r));
        T.Eq("not run", 0, Runs(rig));
        T.True("audited refused", AuditOf(rig) is [{ Outcome: "refused", Code: "not_allowed_in_this_state" }]);
    }

    public static void ARefusedCallDoesNotUseUpTheQuotaOfTheCallThatWouldWork()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner() with { Enabled = false });
        T.Eq("refused while off", "not_allowed_in_this_state", WriteRig.Code(rig.Call(Tool, Args(Id))));
        T.True("the owner turns it on", new Applier(rig.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(Owner(), "Owner job").Success);
        ToolResult r = rig.Call(Tool, Args(Id));
        T.True("the very next call is not rate limited: " + r.Text, !r.IsError);
        T.Eq("one start", 1, Runs(rig));
        T.Eq("the real second start is limited", "rate_limited", WriteRig.Code(rig.Call(Tool, Args(Id))));
    }

    public static void ARunningJobIsRefused()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        rig.Fake.SetRuntime("Owner job", TaskState.Running, null, null);
        ToolResult r = rig.Call(Tool, Args(Id));
        T.True("not_allowed_in_this_state", r.IsError && WriteRig.Code(r) == "not_allowed_in_this_state" && WriteRig.Msg(r).Contains("already running", StringComparison.Ordinal), r.Text);
        T.Eq("not run", 0, Runs(rig));

        rig.Fake.SetRuntime("Owner job", TaskState.Ready, null, null);
        rig.Now = WriteRig.FixedNow.AddSeconds(40);
        T.True("once it has finished it can be started", !rig.Call(Tool, Args(Id)).IsError);
        T.Eq("one start", 1, Runs(rig));
    }

    public static void SuspendedRefusesAndTheSwitchIsReadAgainBeforeTheStart()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        rig.Policy.SetSuspended(true, "app", WriteRig.FixedNow.AddMinutes(-5));
        ToolResult r = rig.Call(Tool, Args(Id));
        T.True("suspended", r.IsError && WriteRig.Code(r) == "suspended" && WriteRig.Msg(r).StartsWith("AI agents are suspended on this PC", StringComparison.Ordinal), r.Text);
        T.Eq("not run", 0, Runs(rig));
        T.Eq("the audit says refused/suspended", "suspended", AuditOf(rig).Single().Code);
        rig.Policy.SetSuspended(false, "app", WriteRig.FixedNow);
        rig.Now = WriteRig.FixedNow.AddSeconds(40);
        T.True("resumed: works on the next call", !rig.Call(Tool, Args(Id)).IsError);

        FlipReader? flip = null;
        using var raced = new WriteRig(store => flip = new FlipReader(store, 2));
        raced.OwnerSave(Owner());
        ToolResult late = raced.Call(Tool, Args(Id));
        T.True("flipped between the checks: refused as suspended", late.IsError && WriteRig.Code(late) == "suspended", late.Text);
        T.Eq("the policy was read twice", 2, flip!.Reads);
        T.Eq("the job was not started", 0, Runs(raced));
    }

    public static void ALockedJobIsNeverStarted()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner() with { AgentLock = true });
        ToolResult r = rig.Call(Tool, Args(Id));
        T.True("locked", r.IsError && WriteRig.Code(r) == "locked", r.Text);
        T.Eq("the plan's message", ManagedJobs.AgentLockedMessage, WriteRig.Msg(r));
        T.Eq("not run", 0, Runs(rig));
        T.True("audited refused/locked", AuditOf(rig) is [{ Outcome: "refused", Code: "locked" }]);
    }

    public static void ALockSetRightBeforeTheStartIsObeyed()
    {
        WriteRig? holder = null;
        using var rig = new WriteRig(store => new FlipReader(store, 2, () =>
            T.True("the owner locks it", new Applier(holder!.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(Owner() with { AgentLock = true }, "Owner job").Success)));
        holder = rig;
        rig.OwnerSave(Owner());
        ToolResult r = rig.Call(Tool, Args(Id));
        T.True("refused as locked at the last moment", r.IsError && WriteRig.Code(r) == "locked", r.Text);
        T.Eq("not run", 0, Runs(rig));
    }

    public static void AJobDisabledRightBeforeTheStartIsNotStarted()
    {
        WriteRig? holder = null;
        using var rig = new WriteRig(store => new FlipReader(store, 2, () =>
            T.True("the owner turns it off", new Applier(holder!.Jobs(Actor.App), TimeSpan.FromSeconds(5)).Save(Owner() with { Enabled = false }, "Owner job").Success)));
        holder = rig;
        rig.OwnerSave(Owner());
        ToolResult r = rig.Call(Tool, Args(Id));
        T.True("refused: it is disabled now", r.IsError && WriteRig.Code(r) == "not_allowed_in_this_state" && WriteRig.Msg(r).Contains("turned off", StringComparison.Ordinal), r.Text);
        T.Eq("not run", 0, Runs(rig));
    }

    public static void JobsAHumanTouchedAndMissingJobsAreNeverStarted()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        string good = rig.Fake.XmlOf("Owner job")!;
        T.Eq("an unknown id", "not_found", WriteRig.Code(rig.Call(Tool, Args("zzzzzzzz"))));

        rig.Fake.Seed("Owner job", Edit(good, m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) }));
        rig.Now = WriteRig.FixedNow.AddSeconds(40);
        ToolResult d = rig.Call(Tool, Args(Id));
        T.True("drifted: refused", d.IsError && WriteRig.Code(d) == "not_allowed_in_this_state" && WriteRig.Msg(d).Contains("changed in Task Scheduler outside Cadence", StringComparison.Ordinal), d.Text);

        rig.Fake.Seed("Owner job", EditMeta(good, m => m with { Gen = m.Gen + 1 }));
        rig.Now = WriteRig.FixedNow.AddSeconds(80);
        T.True("newer version: refused", WriteRig.Code(rig.Call(Tool, Args(Id))) is "not_found" or "not_allowed_in_this_state");

        rig.Fake.Seed("Owner job", Edit(good, m => m with { RegistrationInfo = m.RegistrationInfo with { Documentation = "not json at all" } }));
        rig.Now = WriteRig.FixedNow.AddSeconds(120);
        T.True("damaged: refused", WriteRig.Code(rig.Call(Tool, Args(Id))) is "not_found" or "not_allowed_in_this_state");
        T.Eq("nothing was run", 0, Runs(rig));
    }

    public static void ACopyIsNeverStartedAndAForeignTaskCannotBeNamed()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        string good = rig.Fake.XmlOf("Owner job")!;
        string foreign = Plain();
        rig.Fake.Seed("Somebody else", foreign);
        rig.Fake.Seed("Z copy", good);
        T.True("the original is started, never the copy", !rig.Call(Tool, Args(Id)).IsError && rig.Fake.Writes.Last() == "run Owner job" && Runs(rig) == 1);
        foreach (string guess in new[] { "somebody", "Somebody else", "00000000", "zzzzzzzz", "..\\x" })
            T.True("guessed id '" + guess + "' is refused", rig.Call(Tool, Args(guess)).IsError);
        T.Eq("the foreign task is byte-identical", foreign, rig.Fake.XmlOf("Somebody else"));
        T.Eq("still one start", 1, Runs(rig));
    }

    public static void ArgumentsAreValidatedAndNothingElseCanBeRun()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        (string Json, string Contains)[] cases =
        [
            ("{}", "jobId is required"),
            ("{\"jobId\":7}", "jobId must be a string"),
            ("{\"jobId\":\"" + Id + "\",\"command\":\"calc.exe\"}", "Unknown property 'command'"),
            ("{\"jobId\":\"" + Id + "\",\"arguments\":\"/c calc\"}", "Unknown property 'arguments'"),
            ("{\"jobId\":\"" + Id + "\",\"path\":\"C:\\\\Windows\\\\System32\\\\calc.exe\"}", "Unknown property 'path'"),
            ("{\"jobId\":\"*\"}", "jobId"),
            ("{\"jobId\":\"" + Id + "\\n\"}", "jobId"),
            ("[\"" + Id + "\"]", "must be a JSON object"),
        ];
        foreach ((string json, string contains) in cases)
        {
            ToolResult r = rig.Call(Tool, json);
            T.True(json + ": invalid_argument containing '" + contains + "'", r.IsError && WriteRig.Code(r) == "invalid_argument" && WriteRig.Msg(r).Contains(contains, StringComparison.Ordinal), WriteRig.Code(r) + ": " + WriteRig.Msg(r));
        }
        T.Eq("nothing was run", 0, Runs(rig));
    }

    public static void TheResultNeverContainsTheProgram()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(Owner());
        ToolResult r = rig.Call(Tool, Args(Id));
        T.True("works", !r.IsError);
        T.True("the result never contains a program path or a command line", !r.Text.Contains(WriteRig.Exe, StringComparison.OrdinalIgnoreCase) && !r.Text.Contains("--go", StringComparison.Ordinal));
    }
}
