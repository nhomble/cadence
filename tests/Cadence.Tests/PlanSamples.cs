using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;

namespace Cadence.Tests;

/// <summary>Fixtures for the Classifier, Planner and Applier suites (not a test class: the name does not end in "Tests").</summary>
internal static class PlanSamples
{
    public const string Folder = "Cadence";
    public const string StockName = "Stock ingest (Dad)";

    public static string PathOf(string name, string folder = Folder) => "\\" + folder + "\\" + name;

    /// <summary>The factory the Scheduler layer uses: Now = metadata.Updated, Created and Prev from the metadata.</summary>
    public static ComposeContext FactoryContext(CadenceMetadata m, string name, string folder = Folder) =>
        new(folder, folder, ComposeSamples.User, name, m.Updated, m.Created, m.Prev);

    public static bool IsUser(string? id) => id == ComposeSamples.User;

    public static string Compose(JobDefinition job, string name, DateTimeOffset? now = null, ValueList<string>? prev = null, string folder = Folder) =>
        TaskComposer.ComposeXml(job, new ComposeContext(folder, folder, ComposeSamples.User, name, now ?? ComposeSamples.Now, null, prev ?? ValueList<string>.Empty));

    public static string Stock(string name = StockName) => Compose(ComposeSamples.StockIngest(), name);

    public static ObservedTask Observe(string xml, string name = StockName, string folder = Folder) =>
        new(name, PathOf(name, folder), xml, 3, null, null, null);

    public static ClassifiedTask Classify(string xml, string name = StockName, DateOnly? today = null, Func<string?, bool>? isUser = null, string folder = Folder) =>
        Classifier.Classify(Observe(xml, name, folder), (m, n) => FactoryContext(m, n, folder), today ?? ComposeSamples.Anchor, isUser ?? IsUser);

    public static TaskDefinitionModel Read(string xml) => TaskXmlReader.Read(xml);

    /// <summary>Edits the parsed definition and writes it back (what editing in Task Scheduler does).</summary>
    public static string Edit(string xml, Func<TaskDefinitionModel, TaskDefinitionModel> change) => TaskXmlWriter.Write(change(Read(xml)));

    /// <summary>Edits only the embedded metadata (the fingerprint stays as it was written).</summary>
    public static string EditMeta(string xml, Func<CadenceMetadata, CadenceMetadata> change)
    {
        TaskDefinitionModel model = Read(xml);
        if (MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out CadenceMetadata? meta) != MetadataProblem.None)
            throw new InvalidOperationException("sample has no metadata");
        string doc = MetadataCodec.Encode(change(meta!));
        return TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = doc } });
    }

    public static CadenceMetadata Meta(string xml)
    {
        MetadataCodec.TryDecode(Read(xml).RegistrationInfo.Documentation, out CadenceMetadata? meta);
        return meta!;
    }

    public static JobDefinition Job(string name, string id, params string[] cron) => new()
    {
        Id = id,
        Name = name,
        Schedule = new ScheduleSpec(new ValueList<string>(cron), Cadence.Core.Model.HolidayCalendar.None, ComposeSamples.Anchor),
        Run = new RunSpec { Exe = @"C:\Tools\job.exe", Args = "--go \"a b\"", WorkingDirectory = @"C:\Tools" },
    };

    public static IReadOnlySet<string> Names(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    public static string Plain(string command = "cmd.exe", string args = "/c exit 0") =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
        + "<RegistrationInfo><Author>Someone</Author><Description>not ours</Description></RegistrationInfo>"
        + "<Triggers><TimeTrigger><StartBoundary>2099-01-01T00:00:00</StartBoundary><Enabled>false</Enabled></TimeTrigger></Triggers>"
        + "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
        + "<Settings><Enabled>false</Enabled></Settings>"
        + "<Actions Context=\"Author\"><Exec><Command>" + command + "</Command><Arguments>" + args + "</Arguments></Exec></Actions></Task>";
}
