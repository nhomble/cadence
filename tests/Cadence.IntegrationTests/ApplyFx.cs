using System.Security.Cryptography;
using System.Text;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.Scheduler.Com;

namespace Cadence.IntegrationTests;

/// <summary>Helpers for the Phase 8 suites (not a test class). Everything stays inside \CadenceTest\ and %LOCALAPPDATA%\CadenceTest\.</summary>
internal static class ApplyFx
{
    /// <summary>The gateway (always \CadenceTest), ManagedJobs with the fixed clock (never today's date) and the Applier.</summary>
    public static (TaskSchedulerGateway gw, ManagedJobs jobs, Applier applier) Stack()
    {
        TaskSchedulerGateway gw = Fx.Gateway();
        if (gw.Options.FolderName != TestFolder.Name || gw.Options.RunsRootName != TestFolder.Name)
            throw new InvalidOperationException("The test gateway must use \\" + TestFolder.Name + ".");
        var jobs = new ManagedJobs(gw, () => Fx.Now);
        return (gw, jobs, new Applier(jobs));
    }

    /// <summary>Runs a test body on an empty \CadenceTest (cleaned before and after), so suites cannot see each other's tasks.</summary>
    public static void Isolated(Action body)
    {
        TestFolder.Cleanup();
        try { body(); }
        finally { TestFolder.Cleanup(); }
    }

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Edits the task's definition through the object model and re-registers it (TASK_UPDATE), like the Task Scheduler MMC does.</summary>
    public static void RawEdit(string taskName, Action<dynamic> edit)
    {
        if (taskName.Contains('\\', StringComparison.Ordinal)) throw new ArgumentException("A task name, not a path.", nameof(taskName));
        using TaskServiceSession s = TaskServiceSession.Open();
        object? folder = s.TryGetFolder(TestFolder.Path) ?? throw new InvalidOperationException("\\" + TestFolder.Name + " does not exist.");
        object? task = null;
        object? definition = null;
        try
        {
            dynamic f = folder;
            task = (object)f.GetTask(taskName);
            dynamic t = task;
            definition = (object)t.Definition;
            edit(definition);
            // flags 4 = TASK_UPDATE, logon type 3 = interactive token; userId, password and sddl null.
            f.RegisterTaskDefinition(taskName, definition, 4, null, null, 3, null);
        }
        finally
        {
            ComRelease.Release(definition);
            ComRelease.Release(task);
            ComRelease.Release(folder);
        }
    }

    /// <summary>Moves the first trigger's StartBoundary to <paramref name="hour"/>:00 on 2099-01-01 (never fires).</summary>
    public static void MoveFirstTrigger(string taskName, int hour) =>
        RawEdit(taskName, def => def.Triggers.Item(1).StartBoundary = "2099-01-01T" + hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":00:00");

    /// <summary>The stored task re-registered with its metadata edited (the fingerprint stays as written).</summary>
    public static void RegisterWithEditedMetadata(TaskSchedulerGateway gw, string taskName, Func<CadenceMetadata, CadenceMetadata> change)
    {
        RegisteredTaskInfo info = gw.Get(taskName) ?? throw new InvalidOperationException("no such task");
        TaskDefinitionModel model = TaskXmlReader.Read(info.Xml);
        if (MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out CadenceMetadata? meta) != MetadataProblem.None)
            throw new InvalidOperationException("no metadata");
        string doc = MetadataCodec.Encode(change(meta!));
        gw.Register(taskName, TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = doc } }));
    }

    public static string LocalDataDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TestFolder.Name);

    /// <summary>A job that cannot fire in this century, enabled (so Run() works), with the given program.</summary>
    public static JobDefinition RunnableJob(string name, string exe, string args, string cron = "0 18 * * *") =>
        Fx.Job(name, [cron]) with { Enabled = true, Run = new RunSpec { Exe = exe, Args = args, Hidden = true } };
}
