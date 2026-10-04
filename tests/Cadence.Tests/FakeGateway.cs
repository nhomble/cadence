using Cadence.Scheduler;

namespace Cadence.Tests;

/// <summary>
/// An in-memory <see cref="ITaskSchedulerGateway"/> for the Applier unit tests. It stores the XML exactly as registered (unless
/// <see cref="OnRegister"/> rewrites it, to simulate Task Scheduler storing something else) and records every write in <see cref="Writes"/>.
/// It never touches Task Scheduler.
/// </summary>
internal sealed class FakeGateway : ITaskSchedulerGateway
{
    readonly Dictionary<string, string> _tasks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _others = new(StringComparer.OrdinalIgnoreCase);

    public const string User = @"TESTPC\tester";
    public const string Sid = "S-1-5-21-1111-2222-3333-1001";

    /// <param name="folder">The managed folder name (also the name of the cross-process writer lock). Tests that hold or contend for that lock use a name of their own so they never collide with a real Cadence on this PC.</param>
    public FakeGateway(string folder = "Cadence") => Options = new SchedulerOptions(folder, folder);

    public SchedulerOptions Options { get; }

    string Prefix => "\\" + Options.FolderName + "\\";

    public string ConnectedUserId => User;

    public string ConnectedUserSid => Sid;

    /// <summary>Every Register / Delete / Run / Stop, in order ("register NAME", "delete NAME").</summary>
    public List<string> Writes { get; } = [];

    /// <summary>The mode of every Register that went through, in order.</summary>
    public List<RegisterMode> Modes { get; } = [];

    /// <summary>When set, the XML that is stored for a Register (the name and the XML given).</summary>
    public Func<string, string, string>? OnRegister { get; set; }

    /// <summary>When set, Register throws this.</summary>
    public Exception? RegisterFails { get; set; }

    public bool IsConnectedUser(string? principalUserId) => principalUserId is User or Sid;

    public void Seed(string name, string xml) => _tasks[name] = xml;

    public void SeedOther(string path, string xml) => _others[path] = xml;

    public string? XmlOf(string name) => _tasks.TryGetValue(name, out string? x) ? x : null;

    public IReadOnlyList<string> Names => _tasks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    readonly Dictionary<string, (TaskState State, DateTime? LastRun, int? LastResult)> _runtime = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What Task Scheduler reports for the task's run state (default: Disabled, never ran).</summary>
    public void SetRuntime(string name, TaskState state, DateTime? lastRun, int? lastResult) => _runtime[name] = (state, lastRun, lastResult);

    /// <summary>When set, called with the task name at the start of every Register (to simulate another writer getting in first).</summary>
    public Action<string>? BeforeRegister { get; set; }

    RegisteredTaskInfo Info(string name, string path, string xml) =>
        _runtime.TryGetValue(name, out var r)
            ? new(name, path, xml, r.State, r.State != TaskState.Disabled, r.LastRun, r.LastResult, null, 0)
            : new(name, path, xml, TaskState.Disabled, false, null, null, null, 0);

    public IReadOnlyList<RegisteredTaskInfo> ListManagedFolder() =>
        _tasks.Select(kv => Info(kv.Key, Prefix + kv.Key, kv.Value)).ToList();

    public IReadOnlyList<RegisteredTaskInfo> ListOtherTasks(int max = 5000) =>
        _others.Select(kv => Info(kv.Key[(kv.Key.LastIndexOf('\\') + 1)..], kv.Key, kv.Value)).ToList();

    public RegisteredTaskInfo? Get(string taskName) =>
        _tasks.TryGetValue(taskName, out string? xml) ? Info(Canonical(taskName), Prefix + Canonical(taskName), xml) : null;

    string Canonical(string name) => _tasks.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    public RegisteredTaskInfo Register(string taskName, string xml, RegisterMode mode = RegisterMode.CreateOrUpdate)
    {
        if (taskName.Contains('\\', StringComparison.Ordinal)) throw new ArgumentException("A task name, not a path.", nameof(taskName));
        BeforeRegister?.Invoke(taskName);
        Writes.Add("register " + taskName);
        if (RegisterFails is not null) throw RegisterFails;
        bool exists = _tasks.Keys.Any(k => string.Equals(k, taskName, StringComparison.OrdinalIgnoreCase));
        if (mode == RegisterMode.Create && exists) throw new SchedulerException(SchedulerErrorKind.AlreadyExists, "The task already exists.", unchecked((int)0x800700B7));
        if (mode == RegisterMode.Update && !exists) throw new SchedulerException(SchedulerErrorKind.NotFound, "The task does not exist.", unchecked((int)0x80070002));
        Modes.Add(mode);
        string stored = OnRegister?.Invoke(taskName, xml) ?? xml;
        string key = _tasks.Keys.FirstOrDefault(k => string.Equals(k, taskName, StringComparison.OrdinalIgnoreCase)) ?? taskName;
        _tasks[key] = stored;
        return Info(key, Prefix + key, stored);
    }

    public void Delete(string taskName)
    {
        Writes.Add("delete " + taskName);
        string? key = _tasks.Keys.FirstOrDefault(k => string.Equals(k, taskName, StringComparison.OrdinalIgnoreCase));
        if (key is not null) _tasks.Remove(key);
    }

    public void Run(string taskName) => Writes.Add("run " + taskName);

    public void Stop(string taskName) => Writes.Add("stop " + taskName);

    public void DeleteFolderIfEmpty() => Writes.Add("delete folder");
}
