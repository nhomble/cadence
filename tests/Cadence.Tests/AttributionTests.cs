using System.Security.Cryptography;
using System.Text;
using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>
/// Phase 19 (PLAN 12.4.1, 12.4.4, 12.1.3): the optional createdBy / updatedBy / agentLock metadata fields, the fingerprint and the Classifier
/// verdicts that must not depend on them, v0.1.0 compatibility, and JobRevision. Pure: no Task Scheduler.
/// </summary>
public static class AttributionTests
{
    static readonly DateTimeOffset T0 = ComposeSamples.Now;
    static readonly DateTimeOffset Later = new(2026, 10, 1, 9, 2, 0, TimeSpan.FromHours(-4));

    static ComposeContext Ctx(Actor? actor, DateTimeOffset? now = null, DateTimeOffset? created = null, string? createdBy = null, string name = StockName) =>
        ComposeSamples.Context(name, now, created) with { Actor = actor, CreatedBy = createdBy };

    static string ComposeAs(JobDefinition job, Actor? actor, DateTimeOffset? now = null, string name = StockName) =>
        TaskComposer.ComposeXml(job, Ctx(actor, now, name: name));

    static string DocOf(string xml) => Read(xml).RegistrationInfo.Documentation!;

    static string JsonOf(string doc) => doc[(doc.IndexOf('\n', StringComparison.Ordinal) + 1)..];

    static CadenceMetadata Decode(string doc)
    {
        T.Eq("metadata decodes", MetadataProblem.None, MetadataCodec.TryDecode(doc, out CadenceMetadata? m));
        return m!;
    }

    /// <summary>A job as v0.1.0 wrote it: this exact text, no optional fields.</summary>
    const string GoldenV010 = """
        cadence/1
        {"schema":1,"gen":1,"wv":1,"id":"a1b2c3d4","name":"Backup","enabled":true,"schedule":{"cron":["0 9 * * *"],"skipHolidays":"none","anchor":"2026-10-01"},"run":{"kind":"runProgram","exe":"cmd.exe","args":"/c exit 0","env":{},"timeoutSec":0,"hidden":true,"capture":true,"encoding":"oem"},"settings":{"startWhenAvailable":true,"runOnBatteries":true,"stopOnBatteries":false,"wakeToRun":false},"fp":"3f9c0a1b2c3d4e5f","created":"2026-10-01T09:00:00-04:00","updated":"2026-10-01T09:00:00-04:00","prev":[]}
        """;

    static string Golden => GoldenV010.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- the codec ----

    public static void RoundTripWithAndWithoutTheFields()
    {
        JobDefinition job = ComposeSamples.StockIngest();
        var plain = new CadenceMetadata(1, 1, 1, job, null, "3f9c0a1b2c3d4e5f", T0, Later, ValueList.Of("Old name"));
        string plainText = MetadataCodec.Encode(plain);
        T.True("no optional field is written when none is set", !plainText.Contains("createdBy", StringComparison.Ordinal) && !plainText.Contains("updatedBy", StringComparison.Ordinal) && !plainText.Contains("agentLock", StringComparison.Ordinal));
        CadenceMetadata plainBack = Decode(plainText);
        T.True("decodes to nulls and no lock", plainBack.CreatedBy is null && plainBack.UpdatedBy is null && !plainBack.Job.AgentLock);
        T.True("round trip equal", plain.Equals(plainBack));
        T.Eq("re-encode byte for byte", plainText, MetadataCodec.Encode(plainBack));

        CadenceMetadata full = plain with { CreatedBy = "app", UpdatedBy = "agent:Claude Code", Job = job with { AgentLock = true } };
        string fullText = MetadataCodec.Encode(full);
        T.True("the new fields sit after updated and before prev, in this order",
            fullText.Contains("\"updated\":\"2026-10-01T09:02:00-04:00\",\"createdBy\":\"app\",\"updatedBy\":\"agent:Claude Code\",\"agentLock\":true,\"prev\":[\"Old name\"]}", StringComparison.Ordinal), fullText);
        CadenceMetadata fullBack = Decode(fullText);
        T.True("round trip equal (with fields)", full.Equals(fullBack));
        T.Eq("createdBy", "app", fullBack.CreatedBy);
        T.Eq("updatedBy", "agent:Claude Code", fullBack.UpdatedBy);
        T.True("lock", fullBack.Job.AgentLock);
        T.Eq("re-encode byte for byte (with fields)", fullText, MetadataCodec.Encode(fullBack));

        string onlyUpdated = MetadataCodec.Encode(plain with { UpdatedBy = "cli" });
        T.True("one field alone", onlyUpdated.Contains("\"updatedBy\":\"cli\",\"prev\"", StringComparison.Ordinal) && !onlyUpdated.Contains("createdBy", StringComparison.Ordinal));
        T.True("agentLock false is omitted, not written as false", !MetadataCodec.Encode(plain with { Job = job with { AgentLock = false } }).Contains("agentLock", StringComparison.Ordinal));
    }

    public static void GoldenV010MetadataIsReadAndRewrittenUnchanged()
    {
        CadenceMetadata m = Decode(Golden);
        T.True("no createdBy, no updatedBy, no lock", m.CreatedBy is null && m.UpdatedBy is null && !m.Job.AgentLock);
        T.Eq("id", "a1b2c3d4", m.Job.Id);
        T.Eq("re-encodes byte for byte (a v0.1.0 job is not rewritten differently by a no-op)", Golden, MetadataCodec.Encode(m));
        // With CRLF line ends in the stored text (XML normalizes them, a hand edit might not) it still decodes.
        T.Eq("CRLF after the marker still decodes", MetadataProblem.None, MetadataCodec.TryDecode(Golden.Replace("\n", "\r\n", StringComparison.Ordinal), out _));
    }

    public static void UnknownAndMistypedOptionalFieldsAreTolerated()
    {
        // A future writer's extra top-level property: ignored (this is also what v0.1.0's decoder does with the new fields).
        string withFuture = Golden.Replace("\"prev\":[]", "\"futureField\":{\"x\":1},\"prev\":[]", StringComparison.Ordinal);
        CadenceMetadata a = Decode(withFuture);
        T.True("unknown property ignored", a.Equals(Decode(Golden)));
        T.True("and dropped by the next write", !MetadataCodec.Encode(a).Contains("futureField", StringComparison.Ordinal));

        string withFields = Golden.Replace("\"prev\":[]", "\"createdBy\":\"cli\",\"updatedBy\":\"app\",\"agentLock\":true,\"prev\":[]", StringComparison.Ordinal);
        CadenceMetadata b = Decode(withFields);
        T.True("fields read", b.CreatedBy == "cli" && b.UpdatedBy == "app" && b.Job.AgentLock);

        // Wrong JSON types (a hand edit): read as "not there", never CorruptMetadata.
        string wrong = Golden.Replace("\"prev\":[]", "\"createdBy\":5,\"updatedBy\":{\"a\":[1,2]},\"agentLock\":\"yes\",\"prev\":[]", StringComparison.Ordinal);
        CadenceMetadata c = Decode(wrong);
        // [Review R] ... except the lock, which fails closed: a damaged safety switch must not let agents in.
        T.True("mistyped attribution reads as unknown, a mistyped lock reads as LOCKED", c.CreatedBy is null && c.UpdatedBy is null && c.Job.AgentLock);
        foreach (string odd in new[] { "1", "0", "\"false\"", "{}", "[]" })
            T.True("agentLock " + odd + " reads as locked", Decode(Golden.Replace("\"prev\":[]", "\"agentLock\":" + odd + ",\"prev\":[]", StringComparison.Ordinal)).Job.AgentLock);
        string nulls = Golden.Replace("\"prev\":[]", "\"createdBy\":null,\"updatedBy\":null,\"agentLock\":null,\"prev\":[]", StringComparison.Ordinal);
        T.True("explicit nulls read as unknown / unlocked", Decode(nulls).Equals(Decode(Golden)));
        T.True("agentLock false reads as unlocked", !Decode(Golden.Replace("\"prev\":[]", "\"agentLock\":false,\"prev\":[]", StringComparison.Ordinal)).Job.AgentLock);

        // Garbage text in updatedBy is kept as read (the codec is symmetric) and means "unknown" to people.
        CadenceMetadata g = Decode(Golden.Replace("\"prev\":[]", "\"updatedBy\":\"hacker??\",\"prev\":[]", StringComparison.Ordinal));
        T.Eq("garbage kept verbatim by the codec", "hacker??", g.UpdatedBy);
        T.Eq("garbage is unknown to people", ActorKind.Unknown, Actor.Parse(g.UpdatedBy).Kind);
    }

    public static void SixteenKilobyteBoundaryWithTheLargestAttribution()
    {
        string longName = new('N', 64);
        JobDefinition job = ComposeSamples.StockIngest() with { AgentLock = true };
        CadenceMetadata Make(int pad) => new(1, 1, 1, job with { Run = job.Run with { Args = new string('x', pad) } }, null, "3f9c0a1b2c3d4e5f", T0, Later,
            ValueList<string>.Empty, "agent:" + longName, "agent:" + longName);

        int baseLen = MetadataCodec.Encode(Make(0)).Length;
        int pad = MetadataCodec.MaxEncodedLength - baseLen;
        string exact = MetadataCodec.Encode(Make(pad));
        T.Eq("fills the cap exactly (padding computed from MaxEncodedLength, never a magic number)", MetadataCodec.MaxEncodedLength, exact.Length);
        T.Eq("the full-size text decodes", MetadataProblem.None, MetadataCodec.TryDecode(exact, out CadenceMetadata? back));
        T.Eq("and re-encodes identically", exact, MetadataCodec.Encode(back!));
        T.Throws<InvalidOperationException>("one character more is refused", () => MetadataCodec.Encode(Make(pad + 1)));
        T.True("the attribution itself costs under 200 bytes", MetadataCodec.Encode(Make(0)).Length - MetadataCodec.Encode(Make(0) with { CreatedBy = null, UpdatedBy = null, Job = Make(0).Job with { AgentLock = false } }).Length < 200);
    }

    // ---- the composer ----

    public static void ComposerWritesTheActors()
    {
        JobDefinition job = ComposeSamples.StockIngest();

        CadenceMetadata created = Decode(DocOf(ComposeAs(job, Actor.Agent("Claude Code"))));
        T.Eq("new job: createdBy is the writer", "agent:Claude Code", created.CreatedBy);
        T.Eq("new job: updatedBy is the writer", "agent:Claude Code", created.UpdatedBy);

        CadenceMetadata app = Decode(DocOf(ComposeAs(job, Actor.App)));
        T.True("app writes 'app' twice", app.CreatedBy == "app" && app.UpdatedBy == "app");

        CadenceMetadata none = Decode(DocOf(ComposeAs(job, null)));
        T.True("no actor writes nothing (tests of older behaviour rely on it)", none.CreatedBy is null && none.UpdatedBy is null);
        CadenceMetadata unknown = Decode(DocOf(ComposeAs(job, Actor.Unknown)));
        T.True("the Unknown actor writes nothing either", unknown.CreatedBy is null && unknown.UpdatedBy is null);

        string update = TaskComposer.ComposeXml(job, Ctx(Actor.Cli, Later, created: T0, createdBy: "app"));
        CadenceMetadata updated = Decode(DocOf(update));
        T.Eq("existing job: createdBy kept", "app", updated.CreatedBy);
        T.Eq("existing job: updatedBy is the new writer", "cli", updated.UpdatedBy);
        T.Eq("existing job: created stamp kept", T0, updated.Created);
        T.Eq("existing job: updated stamp is now", Later, updated.Updated);

        CadenceMetadata legacy = Decode(DocOf(TaskComposer.ComposeXml(job, Ctx(Actor.Cli, Later, created: T0, createdBy: null))));
        T.True("a job without a creator stays without one (it is not claimed by whoever edits it)", legacy.CreatedBy is null && legacy.UpdatedBy == "cli");

        CadenceMetadata garbage = Decode(DocOf(TaskComposer.ComposeXml(job, Ctx(Actor.Cli, Later, created: T0, createdBy: "hacker??"))));
        T.True("an unreadable creator token is dropped on rewrite", garbage.CreatedBy is null);
        CadenceMetadata messy = Decode(DocOf(TaskComposer.ComposeXml(job, Ctx(Actor.Cli, Later, created: T0, createdBy: "agent:  Foo   Bar "))));
        T.Eq("a readable but untidy creator token is normalized", "agent:Foo Bar", messy.CreatedBy);

        CadenceMetadata locked = Decode(DocOf(ComposeAs(job with { AgentLock = true }, Actor.App)));
        T.True("the lock is written", locked.Job.AgentLock);
        T.True("and the job equals what was composed", (job with { AgentLock = true }).Equals(locked.Job));
    }

    public static void FingerprintAndXmlOutsideDocumentationIgnoreTheActor()
    {
        JobDefinition job = ComposeSamples.StockIngest();
        var variants = new (string Label, JobDefinition Job, Actor? Actor)[]
        {
            ("no actor", job, null),
            ("app", job, Actor.App),
            ("cli", job, Actor.Cli),
            ("agent X", job, Actor.Agent("X")),
            ("agent with a long name", job, Actor.Agent(new string('z', 200))),
            ("locked, agent", job with { AgentLock = true }, Actor.Agent("Y")),
        };
        TaskDefinitionModel baseline = TaskComposer.Compose(job, Ctx(null));
        string baselineFp = Fingerprint.Of(baseline);
        string baselineXml = XmlWithoutDocumentation(baseline);
        foreach ((string label, JobDefinition j, Actor? actor) in variants)
        {
            TaskDefinitionModel model = TaskComposer.Compose(j, Ctx(actor));
            T.Eq(label + ": identical fingerprint", baselineFp, Fingerprint.Of(model));
            T.Eq(label + ": identical XML outside <Documentation>", baselineXml, XmlWithoutDocumentation(model));
            T.Eq(label + ": the fingerprint stored in the metadata is the same too", baselineFp, Decode(model.RegistrationInfo.Documentation!).Fp);
        }
        // And through the XML text: the metadata of the task that the actor wrote does not change what is read back.
        T.Eq("fingerprint after a write and read", baselineFp, Fingerprint.Of(Read(ComposeAs(job, Actor.Agent("X")))));
    }

    static string XmlWithoutDocumentation(TaskDefinitionModel m) =>
        TaskXmlWriter.Write(m with { RegistrationInfo = m.RegistrationInfo with { Documentation = null } });

    // ---- the Classifier ----

    public static void ClassifierVerdictsDoNotDependOnAttribution()
    {
        JobDefinition job = ComposeSamples.StockIngest();
        // Every state, for the job as v0.1.0 wrote it (no actor) and as this version writes it (each actor, with and without the lock).
        var writers = new (string Label, JobDefinition Job, Actor? Actor)[]
        {
            ("legacy", job, null),
            ("app", job, Actor.App),
            ("cli", job, Actor.Cli),
            ("agent", job, Actor.Agent("Claude Code")),
            ("agent + lock", job with { AgentLock = true }, Actor.Agent("Claude Code")),
        };
        var states = new (string State, Func<string, string> Make, Ownership Own, SyncStatus Sync)[]
        {
            ("in sync", x => x, Ownership.Managed, SyncStatus.InSync),
            ("drifted (trigger edited in Task Scheduler)", x => Edit(x, m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) }), Ownership.Managed, SyncStatus.Drifted),
            ("outdated (older generator)", x => EditMeta(x, m => m with { Gen = 0 }), Ownership.Managed, SyncStatus.Outdated),
            ("outdated (older wrapper)", x => EditMeta(x, m => m with { Wv = 0 }), Ownership.Managed, SyncStatus.Outdated),
            ("newer version (gen)", x => EditMeta(x, m => m with { Gen = m.Gen + 1 }), Ownership.NewerVersion, SyncStatus.NotApplicable),
            ("newer version (wrapper)", x => EditMeta(x, m => m with { Wv = m.Wv + 1 }), Ownership.NewerVersion, SyncStatus.NotApplicable),
            ("corrupt metadata", x => { TaskDefinitionModel m = Read(x); return TaskXmlWriter.Write(m with { RegistrationInfo = m.RegistrationInfo with { Documentation = "cadence/1\n{broken" } }); }, Ownership.CorruptMetadata, SyncStatus.NotApplicable),
        };
        foreach (var state in states)
        {
            string? first = null;
            foreach ((string label, JobDefinition j, Actor? actor) in writers)
            {
                ClassifiedTask c = Classify(state.Make(ComposeAs(j, actor)));
                T.Eq(state.State + " / " + label + ": ownership", state.Own, c.Ownership);
                T.Eq(state.State + " / " + label + ": sync", state.Sync, c.Sync);
                string verdict = c.Ownership + "|" + c.Sync + "|" + c.Problem + "|" + string.Join(";", c.DriftDiffs.Select(d => d.Describe()));
                first ??= verdict;
                T.Eq(state.State + " / " + label + ": same verdict, problem text and diffs as the legacy job", first, verdict);
            }
        }

        // The legacy job through the real text of v0.1.0 (the golden metadata): InSync needs a matching fingerprint, so build the task from it.
        ClassifiedTask goldenTask = Classify(ComposeAs(job, null));
        T.True("a legacy composed job has no actor to show", goldenTask.Metadata is { CreatedBy: null, UpdatedBy: null });
        T.Eq("legacy: in sync", SyncStatus.InSync, goldenTask.Sync);
    }

    // ---- planner: creator kept, lock diff, no-op ----

    static ClassifiedTask Existing(JobDefinition job, Actor? actor, string name = StockName) => Classify(ComposeAs(job, actor, T0, name), name);

    static ComposeContext PlanCtx(Actor? actor) => ComposeSamples.Context("ignored", Later) with { Actor = actor };

    public static void CreatedByIsKeptAcrossUpdateRenameAndReapply()
    {
        JobDefinition job = ComposeSamples.StockIngest();
        ClassifiedTask app = Existing(job, Actor.App);

        PlannedChange update = Planner.ForSave(job with { Run = job.Run with { Args = "-u other.py" } }, app, Names(StockName), PlanCtx(Actor.Cli));
        CadenceMetadata u = Meta(update.NewXml!);
        T.Eq("update: kind", ChangeKind.Update, update.Kind);
        T.Eq("update: createdBy stays the app", "app", u.CreatedBy);
        T.Eq("update: updatedBy is the command line", "cli", u.UpdatedBy);

        PlannedChange rename = Planner.ForSave(job with { Name = "Stock ingest renamed" }, app, Names(StockName), PlanCtx(Actor.Agent("Claude Code")));
        CadenceMetadata r = Meta(rename.NewXml!);
        T.Eq("rename: kind", ChangeKind.Rename, rename.Kind);
        T.True("rename: createdBy app, updatedBy the agent", r.CreatedBy == "app" && r.UpdatedBy == "agent:Claude Code");
        T.Eq("rename: the old name is remembered", StockName, string.Join("|", r.Prev));

        ClassifiedTask drifted = Classify(Edit(ComposeAs(job, Actor.Agent("Claude Code"), T0), m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(2) })) }));
        T.Eq("fixture drifted", SyncStatus.Drifted, drifted.Sync);
        PlannedChange reapply = Planner.ForReapply(drifted, ComposeSamples.Context(StockName, Later) with { Actor = Actor.Cli });
        CadenceMetadata p = Meta(reapply.NewXml!);
        T.True("re-apply: createdBy is the agent that created it, updatedBy the command line", p.CreatedBy == "agent:Claude Code" && p.UpdatedBy == "cli");

        ClassifiedTask legacy = Existing(job, null);
        PlannedChange legacyUpdate = Planner.ForSave(job with { Run = job.Run with { Args = "x" } }, legacy, Names(StockName), PlanCtx(Actor.App));
        CadenceMetadata l = Meta(legacyUpdate.NewXml!);
        T.True("a legacy job edited in the app: updatedBy app, creator still unknown", l.CreatedBy is null && l.UpdatedBy == "app");

        PlannedChange create = Planner.ForSave(job, null, Names(), PlanCtx(Actor.Cli));
        CadenceMetadata c = Meta(create.NewXml!);
        T.True("create: both are the writer", c.CreatedBy == "cli" && c.UpdatedBy == "cli");
    }

    public static void PlannerDiffsTheAgentLockAndNoOpWritesNothing()
    {
        JobDefinition job = ComposeSamples.StockIngest();
        ClassifiedTask existing = Existing(job, Actor.App);

        PlannedChange lockIt = Planner.ForSave(job with { AgentLock = true }, existing, Names(StockName), PlanCtx(Actor.Cli));
        T.Eq("lock: an update", ChangeKind.Update, lockIt.Kind);
        T.True("lock: there is something to write", lockIt.NewXml is not null);
        T.Eq("lock: one diff", 1, lockIt.Changes.Count);
        T.Eq("lock: field", "Agents", lockIt.Changes[0].Field);
        T.Eq("lock: before", "may change it", lockIt.Changes[0].Before);
        T.Eq("lock: after", "may not change it", lockIt.Changes[0].After);
        T.True("lock: the summary says so", lockIt.Summary.Contains("agents", StringComparison.OrdinalIgnoreCase), lockIt.Summary);
        T.True("lock: written to the metadata", Meta(lockIt.NewXml!).Job.AgentLock);

        ClassifiedTask locked = Existing(job with { AgentLock = true }, Actor.Cli);
        PlannedChange unlock = Planner.ForSave(job, locked, Names(StockName), PlanCtx(Actor.Cli));
        T.True("unlock: diff goes the other way", unlock.Changes.Count == 1 && unlock.Changes[0].Before == "may not change it" && unlock.Changes[0].After == "may change it");
        T.True("unlock: written", !Meta(unlock.NewXml!).Job.AgentLock);

        // The lock is not part of the task definition: no projection diff, no fingerprint change.
        T.Eq("the lock does not change the fingerprint", Fingerprint.Of(existing.Actual!), Fingerprint.Of(Read(lockIt.NewXml!)));

        PlannedChange same = Planner.ForSave(job, existing, Names(StockName), PlanCtx(Actor.Cli));
        T.Eq("no-op: an update", ChangeKind.Update, same.Kind);
        T.True("no-op: nothing to write", same.NewXml is null);
        T.Eq("no-op: says so", "No changes to save", same.Summary);

        PlannedChange sameLocked = Planner.ForSave(job with { AgentLock = true }, locked, Names(StockName), PlanCtx(Actor.Agent("Z")));
        T.True("no-op with the lock set: nothing to write, no updatedBy bump", sameLocked.NewXml is null);
    }

    // ---- JobRevision ----

    public static void JobRevisionFormulaIsPinned()
    {
        ClassifiedTask t = Existing(ComposeSamples.StockIngest(), Actor.App);
        string doc = t.Actual!.RegistrationInfo.Documentation!;
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(doc + "\n" + Fingerprint.Of(t.Actual))))[..12];
        T.Eq("SHA-256(Documentation + LF + fingerprint), first 12 lowercase hex", expected, JobRevision.Of(t));
        T.Eq("the model overload gives the same value", expected, JobRevision.Of(t.Actual));
        T.True("12 lowercase hex characters", JobRevision.Of(t).Length == 12 && JobRevision.Of(t).All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
        string none = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("\n")))[..12];
        T.Eq("a task with no readable definition hashes the empty text", none, JobRevision.Of((TaskDefinitionModel?)null));
    }

    public static void JobRevisionIsStableAndSensitive()
    {
        JobDefinition job = ComposeSamples.StockIngest();
        string baseline = JobRevision.Of(Existing(job, Actor.App));
        T.Eq("stable: the same task twice", baseline, JobRevision.Of(Existing(job, Actor.App)));

        // Observed run state is not part of the revision.
        string xml = ComposeAs(job, Actor.App, T0);
        var ran = new ObservedTask(StockName, PathOf(StockName), xml, 4, new DateTime(2026, 10, 2, 18, 0, 0), 0, new DateTime(2026, 10, 3, 18, 0, 0));
        ClassifiedTask running = Classifier.Classify(ran, (m, n) => FactoryContext(m, n), ComposeSamples.Anchor, IsUser);
        T.Eq("unchanged by LastRunTime / state / NextRunTime", baseline, JobRevision.Of(running));

        var changes = new (string Label, JobDefinition Job, Actor? Actor)[]
        {
            ("trigger (cron)", job with { Schedule = job.Schedule with { CronLines = ValueList.Of("30 18 * * 1-5") } }, Actor.App),
            ("program", job with { Run = job.Run with { Exe = @"C:\Windows\other.exe" } }, Actor.App),
            ("arguments", job with { Run = job.Run with { Args = "-u x" } }, Actor.App),
            ("enabled", job with { Enabled = false }, Actor.App),
            ("agent lock", job with { AgentLock = true }, Actor.App),
            ("updatedBy (the same job written by the CLI)", job, Actor.Cli),
            ("updatedBy (an agent)", job, Actor.Agent("Claude Code")),
            ("no actor at all", job, null),
        };
        var seen = new HashSet<string> { baseline };
        foreach ((string label, JobDefinition j, Actor? actor) in changes)
        {
            string rev = JobRevision.Of(Existing(j, actor));
            T.True(label + ": the revision changes", rev != baseline, rev);
            T.True(label + ": and differs from every other change", seen.Add(rev), rev);
        }
        T.True("the update stamp changes it", JobRevision.Of(Classify(ComposeAs(job, Actor.App, Later))) != baseline);

        // An edit in Task Scheduler (the MMC) leaves the Documentation and changes the fingerprint.
        string mmc = Edit(xml, m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) });
        T.True("an edit made outside Cadence changes it", JobRevision.Of(Classify(mmc)) != baseline);
        string settingsEdit = Edit(xml, m => m with { Settings = m.Settings with { WakeToRun = !m.Settings.WakeToRun } });
        T.True("so does a settings edit", JobRevision.Of(Classify(settingsEdit)) != baseline);
    }

    public static void StaleMessageNamesWhoAndWhen()
    {
        JobDefinition job = ComposeSamples.StockIngest();
        string Msg(Actor? by, TimeSpan age, string? edit = null)
        {
            string xml = ComposeAs(job, by, T0);
            if (edit is not null) xml = Edit(xml, m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) });
            return JobRevision.StaleMessage(Classify(xml), T0 + age);
        }
        const string tail = " after you read it. Read it again and re-apply your change.";
        T.Eq("two minutes ago, the app", "This job was changed 2 minutes ago by Cadence (the app)" + tail, Msg(Actor.App, TimeSpan.FromMinutes(2)));
        T.Eq("one minute ago, the command line", "This job was changed 1 minute ago by the command line" + tail, Msg(Actor.Cli, TimeSpan.FromSeconds(75)));
        T.Eq("just now, an agent", "This job was changed just now by Claude Code" + tail, Msg(Actor.Agent("Claude Code"), TimeSpan.FromSeconds(5)));
        T.Eq("hours", "This job was changed 3 hours ago by Cadence (the app)" + tail, Msg(Actor.App, TimeSpan.FromHours(3.5)));
        T.Eq("one day", "This job was changed 1 day ago by Cadence (the app)" + tail, Msg(Actor.App, TimeSpan.FromHours(30)));
        T.Eq("days", "This job was changed 12 days ago by Cadence (the app)" + tail, Msg(Actor.App, TimeSpan.FromDays(12)));
        T.Eq("a legacy job has no known writer", "This job was changed 5 minutes ago by unknown" + tail, Msg(null, TimeSpan.FromMinutes(5)));
        T.Eq("an edit in Task Scheduler leaves no author or time", "This job was changed in Task Scheduler (outside Cadence)" + tail, Msg(Actor.App, TimeSpan.FromMinutes(5), edit: "trigger"));
        T.Eq("a clock that runs backwards reads as just now", "This job was changed just now by the command line" + tail, Msg(Actor.Cli, TimeSpan.FromMinutes(-3)));
    }
}
