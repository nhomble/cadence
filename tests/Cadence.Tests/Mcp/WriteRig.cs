using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>A machine that knows a handful of files, folders and one PATH entry (the write tools check programs and folders with it).</summary>
internal sealed class FakeProbe : IFileProbe
{
    public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows\System32\cmd.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"C:\Windows\py.exe",
        @"C:\Tools\job.exe", @"C:\Tools\backup.exe", @"C:\Tools\run.bat", @"C:\Tools\run.ps1", @"C:\Tools\tool.lnk",
        @"C:\Apps\Cadence\Cadence.exe", @"C:\x\Cadence-standalone.exe", @"C:\Tools\Cadence2.exe",
    };

    public readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase) { @"C:\Windows\System32", @"C:\Tools", @"C:\Data", @"C:\Windows" };

    public readonly Dictionary<string, string> Path = new(StringComparer.OrdinalIgnoreCase) { ["python.exe"] = @"C:\Python\python.exe" };

    public bool FileExists(string path) => Files.Contains(path);

    public bool DirectoryExists(string path) => Directories.Contains(path.TrimEnd('\\')) || Directories.Contains(path);

    public string? FindOnPath(string exeName) => Path.TryGetValue(exeName, out string? p) ? p : null;

    public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir) { args = null; workingDir = null; return null; }
}

/// <summary>A policy reader that turns the suspension on at its Nth read (the moment between check 1 and the write), and can run a hook there first.</summary>
internal sealed class FlipReader(MemoryAgentPolicyStore inner, int flipAtRead, Action? hook = null) : IAgentPolicyReader
{
    public int Reads { get; private set; }

    public AgentPolicy Read()
    {
        Reads++;
        if (Reads == flipAtRead)
        {
            hook?.Invoke();
            if (hook is null) inner.SetSuspended(true, "app", new DateTimeOffset(2026, 10, 1, 9, 0, 1, TimeSpan.FromHours(-4)));
        }
        return inner.Read();
    }
}

/// <summary>
/// The write tools over an in-memory gateway with a folder name of its own (so the named writer mutex never collides with a real Cadence), a fixed clock, a
/// memory policy and audit, and a fake machine. Owners' saves go through the real Applier as <c>Actor.App</c>.
/// </summary>
internal sealed class WriteRig : IDisposable
{
    public static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));
    public const string Secret = "s3cr3t-v4lue-9f2c";
    public const string OwnExe = @"C:\Apps\Cadence\Cadence.exe";
    public const string Exe = @"C:\Tools\job.exe";

    public static readonly CallContext Context = new(new ClientIdentity("test", "Cadence Test Agent", "1"), "abcd1234", "2025-11-25");

    public string Folder { get; } = "CadenceWriteTest" + Guid.NewGuid().ToString("N")[..10];
    public FakeGateway Fake { get; }
    public MemoryAgentPolicyStore Policy { get; } = new();
    public MemoryAuditLog Audit { get; } = new();
    public FakeProbe Probe { get; } = new();
    public CadenceToolHost Host { get; }
    public DateTimeOffset Now { get; set; } = FixedNow;
    public string Temp { get; }

    public WriteRig(Func<MemoryAgentPolicyStore, IAgentPolicyReader>? reader = null, TimeSpan? lockTimeout = null, bool withSwitch = false)
    {
        Fake = new FakeGateway(Folder);
        Temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cadence-writetests-" + Guid.NewGuid().ToString("N"));
        IAgentPolicyReader policy = reader?.Invoke(Policy) ?? Policy;
        Host = new CadenceToolHost(new AgentEnvironment(Fake, new RunRecordStore(System.IO.Path.Combine(Temp, "runs")), () => Now, TimeZoneInfo.Utc, Probe, "v0.0.0-test",
            policy, Audit, withSwitch ? Policy : null, OwnExePath: OwnExe, WriteJobs: actor => new ManagedJobs(Fake, () => Now, actor), WriterLockTimeout: lockTimeout ?? TimeSpan.FromSeconds(5)));
    }

    public ManagedJobs Jobs(Actor? actor = null) => new(Fake, () => Now, actor);

    /// <summary>The owner's side: a save through the real Applier as the app.</summary>
    public ClassifiedTask OwnerSave(JobDefinition job, Actor? as_ = null)
    {
        ApplyResult r = new Applier(Jobs(as_ ?? Actor.App), TimeSpan.FromSeconds(5)).Save(job, null);
        T.True("fixture: the owner's job '" + job.Name + "' is saved (" + r.Message + ")", r.Success);
        return Jobs().Load(false).Single(c => c.Job?.Id == job.Id);
    }

    public ClassifiedTask Live(string id) => Jobs().Load(false).Where(c => c.Job?.Id == id).OrderBy(c => c.DuplicateOf is not null).First();

    public static JobDefinition Job(string name, string id, string cron = "0 18 * * *", string args = "--go") => new()
    {
        Id = id,
        Name = name,
        Schedule = new ScheduleSpec(new ValueList<string>([cron]), HolidayCalendar.None, new DateOnly(2026, 10, 1)),
        Run = new RunSpec { Exe = Exe, Args = args, WorkingDirectory = @"C:\Tools", Hidden = true, Timeout = TimeSpan.FromMinutes(30) },
    };

    public ToolResult Call(string tool, string argumentsJson)
    {
        using JsonDocument d = JsonDocument.Parse(argumentsJson);
        return Host.Call(tool, d.RootElement, Context);
    }

    public ToolResult Save(string argumentsJson) => Call("cadence_save_job", argumentsJson);

    /// <summary>Calls cadence_save_job and requires success; returns the structured result.</summary>
    public JsonObject SaveOk(string argumentsJson)
    {
        ToolResult r = Save(argumentsJson);
        if (r.IsError) throw new InvalidOperationException("save failed: " + r.Text);
        return r.Structured!;
    }

    public static string Code(ToolResult r) => r.Structured?["error"]?["code"]?.GetValue<string>() ?? "(none)";

    public static string Msg(ToolResult r) => r.Structured?["error"]?["message"]?.GetValue<string>() ?? r.Text;

    public int Registers => Fake.Writes.Count(w => w.StartsWith("register ", StringComparison.Ordinal));

    /// <summary>The audit lines of the save tool.</summary>
    public IReadOnlyList<AuditEntry> SaveAudit => [.. Audit.All.Where(a => a.Tool == "cadence_save_job")];

    public static string Create(string name, string cron = "0 18 * * 1-5", string path = Exe, string extraProgram = "") =>
        "{\"name\":" + JsonSerializer.Serialize(name) + ",\"schedule\":{\"cron\":[" + JsonSerializer.Serialize(cron) + "]},\"program\":{\"path\":" + JsonSerializer.Serialize(path) + extraProgram + "}}";

    public void Dispose()
    {
        try { Directory.Delete(Temp, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp folder: leave it */ }
    }
}
