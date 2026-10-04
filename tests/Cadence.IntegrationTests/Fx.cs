using System.Globalization;
using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>Shared helpers (not a test class: the name does not end in "Tests"). Everything here stays inside \CadenceTest\.</summary>
internal static class Fx
{
    /// <summary>Fixed so that composed output never depends on the clock (only the embedded holiday list and timestamps use it).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));

    /// <summary>Triggers anchored here never fire: the year 2099 is far away and every composed test task is disabled as well.</summary>
    public static readonly DateOnly FarAnchor = new(2099, 1, 1);

    public static TaskSchedulerGateway Gateway() => TestFolder.NewGateway();

    /// <summary>A harmless disabled job (the wrapper would only run cmd.exe /c exit 0). Never enabled.</summary>
    public static JobDefinition Job(string name, string[] cron, DateOnly? anchor = null, HolidayCalendar skip = HolidayCalendar.None) => new()
    {
        Id = JobId.New(),
        Name = name,
        Schedule = new ScheduleSpec(new ValueList<string>(cron), skip, anchor ?? FarAnchor),
        Run = new RunSpec { Exe = "cmd.exe", Args = "/c exit 0" },
        Enabled = false,
    };

    public static string ComposeXmlFor(ITaskSchedulerGateway gw, JobDefinition job, string taskName) =>
        TaskComposer.ComposeXml(job, Ctx(gw, taskName));

    public static ComposeContext Ctx(ITaskSchedulerGateway gw, string taskName, DateTimeOffset? now = null) =>
        new(TestFolder.Name, TestFolder.Name, gw.ConnectedUserId, taskName, now ?? Now, null, ValueList<string>.Empty);

    /// <summary>
    /// Task XML with a trigger that cannot fire in this century (or a disabled task). <paramref name="triggers"/> is the inner XML of
    /// &lt;Triggers&gt;; the action is <c>cmd.exe /c exit 0</c> unless given.
    /// </summary>
    public static string Xml(string triggers, bool enabled = false, string command = "cmd.exe", string args = "/c exit 0", bool hiddenTask = false, string settingsExtra = "") =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
        + "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
        + (triggers.Length == 0 ? "" : "<Triggers>" + triggers + "</Triggers>")
        + "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
        + "<Settings><Enabled>" + (enabled ? "true" : "false") + "</Enabled>" + (hiddenTask ? "<Hidden>true</Hidden>" : "") + settingsExtra + "</Settings>"
        + "<Actions Context=\"Author\"><Exec><Command>" + command + "</Command><Arguments>" + args + "</Arguments></Exec></Actions></Task>";

    /// <summary>A trigger that is dated 2099 (and disabled).</summary>
    public const string FarTrigger = "<TimeTrigger><StartBoundary>2099-01-01T00:00:00</StartBoundary><Enabled>false</Enabled></TimeTrigger>";

    /// <summary>Runs one case; an unexpected exception becomes a FAIL line instead of aborting the rest of the method.</summary>
    public static void Case(string label, Action body)
    {
        try { body(); }
        catch (Exception ex) { T.True(label + ": no exception", false, ex.GetType().Name + ": " + ex.Message); }
    }

    public static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(250);
        }
        return condition();
    }

    public static void Info(string text) => Console.WriteLine("INFO " + text);

    public static string Iso(DateTime? d) => d?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) ?? "(none)";

    /// <summary>Composes, registers and reads back one job; checks everything the Classifier will later rely on.</summary>
    public static RegisteredTaskInfo? ComposeRegisterCheck(string label, JobDefinition job, string taskName, bool deleteAfter = true)
    {
        var gw = Gateway();
        TaskDefinitionModel model = TaskComposer.Compose(job, Ctx(gw, taskName));
        string xml = TaskXmlWriter.Write(model);
        string composedFp = Fingerprint.Of(model);

        RegisteredTaskInfo info;
        try { info = gw.Register(taskName, xml); }
        catch (SchedulerException ex)
        {
            T.True(label + ": Task Scheduler accepts the task", false, ex.Kind + ": " + ex.Message);
            return null;
        }
        T.True(label + ": Task Scheduler accepts the task", true);

        TaskDefinitionModel stored = TaskXmlReader.Read(info.Xml);
        T.Eq(label + ": fingerprint(stored) == fingerprint(composed)", composedFp, Fingerprint.Of(stored));

        MetadataProblem problem = MetadataCodec.TryDecode(stored.RegistrationInfo.Documentation, out CadenceMetadata? meta);
        T.Eq(label + ": stored metadata decodes", MetadataProblem.None, problem);
        T.Eq(label + ": fingerprint(stored) == metadata fp", meta?.Fp, Fingerprint.Of(stored));
        T.Eq(label + ": Documentation byte for byte", model.RegistrationInfo.Documentation, stored.RegistrationInfo.Documentation);
        T.Eq(label + ": job round-trips through the stored metadata", job, meta?.Job);
        T.Eq(label + ": stays disabled (never fires)", false, info.Enabled);

        // PLAN 6.3 #2 (Phase 8): what Task Scheduler stored must classify as Managed and InSync.
        ClassifiedTask classified = new ManagedJobs(gw, () => Now).Classify(info);
        T.Eq(label + ": Classifier ownership", Ownership.Managed, classified.Ownership);
        T.True(label + ": Classifier says InSync", classified.Sync == SyncStatus.InSync, classified.Sync + ": " + classified.Problem);

        if (deleteAfter) gw.Delete(taskName);
        return info;
    }
}
