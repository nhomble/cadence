using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.History;
using Cadence.Scheduler;

namespace Cadence.Tests.Mcp;

/// <summary>
/// A gateway around <see cref="FakeGateway"/> that the read tools must treat as read-only: it counts every call, and anything that writes or lists
/// the tasks outside the managed folder throws (so a tool that tried would fail its test).
/// </summary>
internal sealed class GuardGateway(FakeGateway inner) : ITaskSchedulerGateway
{
    public List<string> Calls { get; } = [];

    public SchedulerOptions Options => inner.Options;
    public string ConnectedUserId => inner.ConnectedUserId;
    public string ConnectedUserSid => inner.ConnectedUserSid;
    public bool IsConnectedUser(string? principalUserId) => inner.IsConnectedUser(principalUserId);

    public IReadOnlyList<RegisteredTaskInfo> ListManagedFolder() { Calls.Add("ListManagedFolder"); return inner.ListManagedFolder(); }
    public RegisteredTaskInfo? Get(string taskName) { Calls.Add("Get " + taskName); return inner.Get(taskName); }

    public IReadOnlyList<RegisteredTaskInfo> ListOtherTasks(int max = 5000) => throw new InvalidOperationException("ListOtherTasks must never be called by the agent tools");
    public RegisteredTaskInfo Register(string taskName, string xml, RegisterMode mode = RegisterMode.CreateOrUpdate) => throw new InvalidOperationException("Register from a read tool");
    public void Delete(string taskName) => throw new InvalidOperationException("Delete from a read tool");
    public void Run(string taskName) => throw new InvalidOperationException("Run from a read tool");
    public void Stop(string taskName) => throw new InvalidOperationException("Stop from a read tool");
    public void DeleteFolderIfEmpty() => throw new InvalidOperationException("DeleteFolderIfEmpty from a read tool");
}

/// <summary>A probe that knows nothing (the read tools do not use it).</summary>
internal sealed class NoProbe : IFileProbe
{
    public bool FileExists(string path) => false;
    public string? FindOnPath(string exeName) => null;
    public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir) { args = null; workingDir = null; return null; }
}

/// <summary>
/// The read tools over a fake gateway and a TEMP runs root (never %LOCALAPPDATA%\Cadence). The clock is fixed, so nothing depends on today's date.
/// </summary>
internal sealed class ReadRig : IDisposable
{
    public static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));
    public const string Secret = "s3cr3t-v4lue-9f2c";
    public const string ForeignName = "foreign1";

    public static readonly CallContext Context = new(new ClientIdentity("test", "Test Agent", "1"), "abcd1234", "2025-11-25");

    public FakeGateway Fake { get; } = new();
    public GuardGateway Gw { get; }
    public string Temp { get; }
    public string RunsRoot { get; }
    public RunRecordStore Runs { get; }
    public CadenceToolHost Host { get; }
    public Cadence.Agent.Policy.MemoryAgentPolicyStore Policy { get; } = new();
    public Cadence.Agent.Audit.MemoryAuditLog Audit { get; } = new();
    /// <summary>The fixed clock the tools see; a test may move it (the closures read it on every call).</summary>
    public DateTimeOffset Now { get; set; }

    /// <param name="withSwitch">[OWNER-Q14] True: the environment also carries the suspension switch (the policy store is both reader and switch), so the two control tools are offered.</param>
    public ReadRig(DateTimeOffset? now = null, TimeZoneInfo? zone = null, bool withSwitch = false)
    {
        Temp = Path.Combine(Path.GetTempPath(), "cadence-readtests-" + Guid.NewGuid().ToString("N"));
        RunsRoot = Path.Combine(Temp, "runs");
        Directory.CreateDirectory(RunsRoot);
        Runs = new RunRecordStore(RunsRoot);
        Gw = new GuardGateway(Fake);
        Now = now ?? FixedNow;
        Host = new CadenceToolHost(new AgentEnvironment(Gw, Runs, () => Now, zone ?? TimeZoneInfo.Utc, new NoProbe(), "v0.0.0-test", Policy, Audit, withSwitch ? Policy : null));
    }

    /// <summary>A host over an empty fake gateway and a runs root that does not exist (nothing is created on disk).</summary>
    public static CadenceToolHost Bare() =>
        new(new AgentEnvironment(new GuardGateway(new FakeGateway()), new RunRecordStore(Path.Combine(Path.GetTempPath(), "cadence-nonexistent-runs")),
            () => FixedNow, TimeZoneInfo.Utc, new NoProbe(), "v0.0.0-test", new Cadence.Agent.Policy.MemoryAgentPolicyStore(), Cadence.Agent.Audit.NullAuditLog.Instance));

    /// <summary>The Stock ingest job with a secret in its environment.</summary>
    public static JobDefinition Stock(string? id = null, string? name = null)
    {
        JobDefinition j = ComposeSamples.StockIngest();
        return j with
        {
            Id = id ?? j.Id,
            Name = name ?? j.Name,
            Run = j.Run with { Env = ValueList.Of(new KeyValuePair<string, string>("PYTHONIOENCODING", "utf-8"), new KeyValuePair<string, string>("API_KEY", Secret)) },
        };
    }

    /// <summary>Composes the job and puts the task into the fake managed folder; returns the task name.</summary>
    public string Seed(JobDefinition job, string? taskName = null, DateTimeOffset? at = null)
    {
        string name = taskName ?? job.Name;
        Fake.Seed(name, PlanSamples.Compose(job, name, at ?? Now));
        return name;
    }

    /// <summary>A task Cadence did not write, inside the managed folder.</summary>
    public void SeedForeign(string name = ForeignName) => Fake.Seed(name, PlanSamples.Plain());

    /// <summary>Appends a run record (and its output files) under the temp runs root, the way the wrapper does.</summary>
    public void AddRun(string jobId, string taskName, string runId, string status, int? exit, string? stdout = null, string? stderr = null, string? msg = null)
    {
        string dir = Path.Combine(RunsRoot, jobId);
        Directory.CreateDirectory(dir);
        DateTimeOffset start = DateTimeOffset.ParseExact(runId[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var rec = new JsonObject
        {
            ["v"] = 1, ["wv"] = 1, ["task"] = "\\Cadence\\" + taskName, ["runId"] = runId,
            ["wrapperStart"] = start.ToString("o", CultureInfo.InvariantCulture),
            ["start"] = status == "skipped" ? null : start.AddMilliseconds(400).ToString("o", CultureInfo.InvariantCulture),
            ["end"] = start.AddSeconds(21).ToString("o", CultureInfo.InvariantCulture),
            ["exit"] = exit, ["status"] = status, ["pid"] = 100,
            ["outBytes"] = stdout?.Length ?? 0, ["errBytes"] = stderr?.Length ?? 0, ["msg"] = msg,
        };
        File.AppendAllText(Path.Combine(dir, RunRecordStore.JournalFile), rec.ToJsonString() + "\n", new UTF8Encoding(false));
        if (stdout is not null) File.WriteAllText(Path.Combine(dir, runId + ".out.txt"), stdout, new UTF8Encoding(false));
        if (stderr is not null) File.WriteAllText(Path.Combine(dir, runId + ".err.txt"), stderr, new UTF8Encoding(false));
    }

    public ToolResult Call(string tool, string argumentsJson = "{}")
    {
        using JsonDocument d = JsonDocument.Parse(argumentsJson);
        return Host.Call(tool, d.RootElement, Context);
    }

    /// <summary>Calls a tool and requires success; returns the structured content.</summary>
    public JsonObject Ok(string tool, string argumentsJson = "{}")
    {
        ToolResult r = Call(tool, argumentsJson);
        if (r.IsError) throw new InvalidOperationException(tool + " failed: " + r.Text);
        return r.Structured!;
    }

    public static string Code(ToolResult r) => r.Structured?["error"]?["code"]?.GetValue<string>() ?? "(none)";

    public void Dispose()
    {
        try { Directory.Delete(Temp, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp folder: leave it */ }
    }
}
