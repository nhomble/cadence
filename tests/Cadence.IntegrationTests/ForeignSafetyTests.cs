using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>
/// PLAN 6.3 #7: a task that does not carry Cadence's marker survives every operation unchanged (XML hash). Includes a foreign task in the
/// managed folder, a name collision, newer-version and corrupt tasks. Real Task Scheduler, \CadenceTest only.
/// </summary>
public static class ForeignSafetyTests
{
    static PlannedChange Forged(ChangeKind kind, string name, string jobId, string? xml, string? newName = null) =>
        new(kind, jobId, kind == ChangeKind.Create ? null : name, kind == ChangeKind.Delete ? null : newName ?? name, xml, ValueList<FieldDiff>.Empty, "forged");

    static readonly ChangeKind[] AllKinds = [ChangeKind.Create, ChangeKind.Update, ChangeKind.Upgrade, ChangeKind.Reapply, ChangeKind.Rename, ChangeKind.Delete, ChangeKind.Untrack];

    public static void AForeignTaskInTheManagedFolderSurvivesEveryOperation() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "foreign task";
        gw.Register(name, Fx.Xml(Fx.FarTrigger));
        string hash = ApplyFx.Sha(gw.Get(name)!.Xml);
        T.Eq("classified Foreign", Ownership.Foreign, jobs.Reclassify(name)!.Ownership);

        JobDefinition job = Fx.Job("some job", ["0 9 * * *"]);
        string composed = Fx.ComposeXmlFor(gw, job, name);   // a valid Cadence definition addressed to the foreign task's name

        foreach (ChangeKind kind in AllKinds)
            foreach (string? xml in new[] { null, composed })
            {
                ApplyResult r = applier.Apply(Forged(kind, name, job.Id, xml, kind == ChangeKind.Rename ? "renamed foreign" : null));
                T.True(kind + (xml is null ? " (no xml)" : " (valid xml)") + " refused: " + r.Message, !r.Success);
                T.Eq(kind + ": xml hash unchanged", hash, ApplyFx.Sha(gw.Get(name)!.Xml));
            }

        T.True("Save over it refused", !applier.Save(job, name).Success);
        T.True("Delete refused", !applier.Delete(name).Success);
        T.True("Reapply refused", !applier.Reapply(name).Success);
        T.True("Untrack refused", !applier.Untrack(name).Success);
        T.Throws<PlanRefusedException>("PlanDelete refuses", () => jobs.PlanDelete(name));
        T.Throws<PlanRefusedException>("PlanReapply refuses", () => jobs.PlanReapply(name));
        T.Throws<PlanRefusedException>("PlanUntrack refuses", () => jobs.PlanUntrack(name));
        T.Throws<PlanRefusedException>("PlanSave over it refuses", () => jobs.PlanSave(job, name));

        T.Eq("the task is still there", 1, gw.ListManagedFolder().Count);
        T.Eq("its xml hash is unchanged after every operation", hash, ApplyFx.Sha(gw.Get(name)!.Xml));

        T.Eq("Load(false) hides it", 0, jobs.Load(false).Count);
        IReadOnlyList<ClassifiedTask> all = jobs.Load(true);
        T.True("Load(true) lists it as Foreign", all.Any(t => t.Observed.Path == "\\CadenceTest\\" + name && t.Ownership == Ownership.Foreign));
    });

    public static void ANameThatCollidesWithAJobNeverOverwritesTheForeignTask() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "Nightly backup";
        gw.Register(name, Fx.Xml(Fx.FarTrigger));
        string hash = ApplyFx.Sha(gw.Get(name)!.Xml);

        // A job called exactly like the foreign task is saved under a free name.
        JobDefinition job = Fx.Job(name, ["0 2 * * *"]);
        ApplyResult r = applier.Save(job, null);
        T.True("save succeeds under a free name: " + r.Message, r.Success);
        string[] names = gw.ListManagedFolder().Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        T.Eq("both tasks exist", name + "|" + name + " (2)", string.Join("|", names));
        T.Eq("the foreign task is byte for byte unchanged", hash, ApplyFx.Sha(gw.Get(name)!.Xml));
        T.Eq("the new task is a managed InSync job", SyncStatus.InSync, jobs.Reclassify(name + " (2)")!.Sync);

        // A forged create for exactly the foreign task's name is refused (Cadence never overwrites).
        ApplyResult forged = applier.Apply(Forged(ChangeKind.Create, name, job.Id, Fx.ComposeXmlFor(gw, job, name)));
        T.True("forged create refused: " + forged.Message, !forged.Success);
        T.Eq("still unchanged", hash, ApplyFx.Sha(gw.Get(name)!.Xml));

        // Renaming the managed job onto the foreign task's name gets a suffix; a forged rename onto it is refused.
        JobDefinition managed = jobs.Reclassify(name + " (2)")!.Job!;
        ApplyResult forgedRename = applier.Apply(Forged(ChangeKind.Rename, name + " (2)", managed.Id, Fx.ComposeXmlFor(gw, managed, name), name));
        T.True("forged rename refused: " + forgedRename.Message, !forgedRename.Success);
        T.Eq("foreign still unchanged", hash, ApplyFx.Sha(gw.Get(name)!.Xml));
        T.True("the managed task is still there", gw.Get(name + " (2)") is not null);

        // And deleting the managed job leaves the foreign task alone.
        T.True("delete the managed job", applier.Delete(name + " (2)").Success);
        T.Eq("foreign still unchanged after the delete", hash, ApplyFx.Sha(gw.Get(name)!.Xml));
    });

    public static void NewerVersionAndCorruptTasksAreProtected() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        JobDefinition job = Fx.Job("source job", ["0 9 * * *"]);
        T.True("created", applier.Save(job, null).Success);

        // A newer generator wrote this one (same schema): read-only.
        const string newer = "newer task";
        // (A job of its own: a copy under another name would share the id and be refused as a copy, R21.)
        T.True("a second job of its own", applier.Save(Fx.Job(newer, ["0 9 * * *"]), null).Success);
        ApplyFx.RegisterWithEditedMetadata(gw, newer, m => m with { Gen = m.Gen + 1 });
        ClassifiedTask n = jobs.Reclassify(newer)!;
        T.Eq("newer generator -> NewerVersion", Ownership.NewerVersion, n.Ownership);
        string hash = ApplyFx.Sha(gw.Get(newer)!.Xml);
        foreach (ChangeKind kind in AllKinds.Where(k => k != ChangeKind.Create))
        {
            ApplyResult r = applier.Apply(Forged(kind, newer, n.Job!.Id, null, kind == ChangeKind.Rename ? "moved newer" : null));
            T.True(kind + " refused: " + r.Message, !r.Success);
        }
        T.True("Save refused", !applier.Save(n.Job!, newer).Success);
        T.True("Untrack refused", !applier.Untrack(newer).Success);
        T.True("Reapply refused", !applier.Reapply(newer).Success);
        T.Eq("newer task unchanged", hash, ApplyFx.Sha(gw.Get(newer)!.Xml));

        // Metadata of a newer schema.
        const string schema2 = "schema two";
        TaskDefinitionModel model = TaskXmlReader.Read(gw.Get("source job")!.Xml);
        gw.Register(schema2, TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "cadence/2\n{\"schema\":2}" } }));
        T.Eq("schema 2 -> NewerVersion", Ownership.NewerVersion, jobs.Reclassify(schema2)!.Ownership);
        string hash2 = ApplyFx.Sha(gw.Get(schema2)!.Xml);
        T.True("schema 2: delete refused", !applier.Delete(schema2).Success);
        T.True("schema 2: untrack refused", !applier.Untrack(schema2).Success);
        T.Eq("schema 2 unchanged", hash2, ApplyFx.Sha(gw.Get(schema2)!.Xml));

        // Corrupt metadata: only Delete is possible.
        const string corrupt = "corrupt one";
        gw.Register(corrupt, TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "cadence/1\n{broken" } }));
        T.Eq("broken metadata -> CorruptMetadata", Ownership.CorruptMetadata, jobs.Reclassify(corrupt)!.Ownership);
        string hash3 = ApplyFx.Sha(gw.Get(corrupt)!.Xml);
        T.True("corrupt: untrack refused", !applier.Untrack(corrupt).Success);
        T.True("corrupt: reapply refused", !applier.Reapply(corrupt).Success);
        T.Eq("corrupt unchanged", hash3, ApplyFx.Sha(gw.Get(corrupt)!.Xml));
        T.True("corrupt: delete works", applier.Delete(corrupt).Success);
        T.True("corrupt: gone", gw.Get(corrupt) is null);
        T.True("the other tasks are still there", gw.Get(newer) is not null && gw.Get(schema2) is not null && gw.Get("source job") is not null);
    });

    public static void NamesThatEscapeTheFolderAreRefused() => ApplyFx.Isolated(() =>
    {
        var (gw, _, applier) = ApplyFx.Stack();
        string[] bad = ["..\\Microsoft\\Windows\\Foo", "\\Microsoft\\Foo", "a/b", ".."];
        foreach (string name in bad)
        {
            foreach (ChangeKind kind in AllKinds.Where(k => k != ChangeKind.Create))
            {
                ApplyResult r = applier.Apply(Forged(kind, name, "abcd1234", null));
                T.True(kind + " refused for '" + name + "'", !r.Success, r.Message);
            }
            T.True("Create refused for '" + name + "'", !applier.Apply(Forged(ChangeKind.Create, name, "abcd1234", null)).Success);
        }
        T.Eq("nothing was created", 0, gw.ListManagedFolder().Count);
    });

    /// <summary>Read-only: every other task on this machine reads as Foreign (and none throws while being classified).</summary>
    public static void EveryOtherTaskOnThisMachineIsForeign() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, _) = ApplyFx.Stack();
        IReadOnlyList<RegisteredTaskInfo> others = gw.ListOtherTasks();
        int foreign = 0, notForeign = 0;
        foreach (RegisteredTaskInfo info in others)
        {
            ClassifiedTask c = jobs.Classify(info);
            if (c.Ownership == Ownership.Foreign) foreign++;
            else
            {
                notForeign++;
                Fx.Info("not foreign: " + info.Path + " is " + c.Ownership);
            }
        }
        Fx.Info(others.Count + " other tasks classified: " + foreign + " foreign, " + notForeign + " other");
        T.Eq("no task of this machine reads as Cadence's", 0, notForeign);
        T.True("the listing is not empty", others.Count > 0);

        IReadOnlyList<ClassifiedTask> all = jobs.Load(true);
        T.True("Load(true) never returns a non-Foreign task from outside the folder", all.Where(t => !t.Observed.Path.StartsWith("\\CadenceTest\\", StringComparison.OrdinalIgnoreCase)).All(t => t.Ownership == Ownership.Foreign));
    });
}
