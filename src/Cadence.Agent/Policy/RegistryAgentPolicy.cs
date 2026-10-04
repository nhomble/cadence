using Cadence.Scheduler;
using Microsoft.Win32;

namespace Cadence.Agent.Policy;

/// <summary>
/// The READ side of the registry policy (PLAN 12.4.2): <c>HKCU\Software\&lt;Folder&gt;\Agents</c> ("Cadence" or "CadenceTest", validated by
/// <see cref="ManagedFolder"/>, [CP-R4]). It opens the key read-only and never creates it. This is the class the MCP host hands to the tools; it has
/// no write method and does not derive from <see cref="IAgentPolicyStore"/>. Any failure to read (access denied, I/O error, anything unexpected)
/// returns <see cref="AgentPolicy.Unreadable"/>: agents are refused rather than trusted.
/// </summary>
public sealed class RegistryAgentPolicyReader : IAgentPolicyReader
{
    readonly string _keyPath;
    readonly Func<IReadOnlyDictionary<string, PolicyValue>?> _source;

    public RegistryAgentPolicyReader(string folderName)
    {
        _keyPath = KeyPath(folderName);
        _source = () => Snapshot(_keyPath);
    }

    /// <summary>For tests: a different source of raw values (it may return null for "no key" or throw for "unreadable").</summary>
    public RegistryAgentPolicyReader(string folderName, Func<IReadOnlyDictionary<string, PolicyValue>?> source)
    {
        _keyPath = KeyPath(folderName);
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>The key under HKCU for a managed folder: <c>Software\Cadence\Agents</c> or <c>Software\CadenceTest\Agents</c>. Anything else throws.</summary>
    public static string KeyPath(string folderName)
    {
        if (!ManagedFolder.IsCanonical(folderName)) throw new ArgumentException("The folder must be exactly Cadence or CadenceTest.", nameof(folderName));
        return @"Software\" + folderName + @"\Agents";
    }

    public string Path => _keyPath;

    public AgentPolicy Read()
    {
        try
        {
            return AgentPolicyParser.Parse(_source());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AgentPolicy.Unreadable("the registry key " + _keyPath + " could not be read (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>All known values of the key (null when the key does not exist). Opens read-only: it never creates anything. May throw.</summary>
    public static IReadOnlyDictionary<string, PolicyValue>? Snapshot(string keyPath)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null) return null;
        var values = new Dictionary<string, PolicyValue>(StringComparer.OrdinalIgnoreCase);
        string[] present = key.GetValueNames();
        foreach (string name in new[]
        {
            AgentPolicyParser.SuspendedName, AgentPolicyParser.SuspendedAtName, AgentPolicyParser.SuspendedByName, AgentPolicyParser.MinIntervalName,
            AgentPolicyParser.MaxJobsName, AgentPolicyParser.ChangedByName, AgentPolicyParser.ChangedAtName, AgentPolicyParser.ChangedFromName,
            AgentPolicyParser.ChangedReasonName,
        })
        {
            if (!present.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            RegistryValueKind kind = key.GetValueKind(name);
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name] = new PolicyValue(kind switch { RegistryValueKind.DWord => PolicyValueKind.DWord, RegistryValueKind.String => PolicyValueKind.String, _ => PolicyValueKind.Other }, value);
        }
        return values;
    }
}

/// <summary>
/// [OWNER-Q14] The switch-only writer the MCP host holds: it can turn the suspension switch on or off (attributed to the calling agent) and read the
/// policy, and that is all. It does NOT implement <see cref="IAgentPolicyStore"/> and has no method for the limits, so a tool cannot reach them even with
/// a cast. The write itself is the same routine the owner's store uses, so every route records the same values.
/// </summary>
public sealed class RegistryAgentSwitch : IAgentSwitch
{
    readonly string _keyPath;
    readonly RegistryAgentPolicyReader _reader;

    public RegistryAgentSwitch(string folderName)
    {
        _keyPath = RegistryAgentPolicyReader.KeyPath(folderName);
        _reader = new RegistryAgentPolicyReader(folderName);
    }

    public string Path => _keyPath;

    public AgentPolicy Read() => _reader.Read();

    public void SetSuspended(bool suspended, string by, DateTimeOffset at, string? changedFrom = null, string? reason = null) =>
        RegistryAgentPolicyStore.WriteSuspended(_keyPath, suspended, by, at, changedFrom, reason);
}

/// <summary>
/// The owner's WRITE side (never given to the MCP host, which gets <see cref="RegistryAgentSwitch"/>): creates the key on the first write, never on a read.
/// Suspending writes the flag first and the details after; resuming writes the details first and clears the flag LAST, so a crash half-way
/// leaves agents suspended, never half-resumed.
/// </summary>
public sealed class RegistryAgentPolicyStore : IAgentPolicyStore
{
    readonly string _keyPath;
    readonly RegistryAgentPolicyReader _reader;

    public RegistryAgentPolicyStore(string folderName)
    {
        _keyPath = RegistryAgentPolicyReader.KeyPath(folderName);
        _reader = new RegistryAgentPolicyReader(folderName);
    }

    public string Path => _keyPath;

    public AgentPolicy Read() => _reader.Read();

    public void SetSuspended(bool suspended, string by, DateTimeOffset at, string? changedFrom = null, string? reason = null) =>
        WriteSuspended(_keyPath, suspended, by, at, changedFrom, reason);

    internal static void WriteSuspended(string keyPath, bool suspended, string by, DateTimeOffset at, string? changedFrom, string? reason)
    {
        string who = AgentPolicyParser.NormalizeWho(by) ?? throw new ArgumentException("by must be \"app\", \"cli\" or \"agent:<name>\".", nameof(by));
        string? why = AgentPolicyParser.CleanFrom(reason);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
        string stamp = AgentPolicy.Stamp(at);
        if (suspended)
        {
            key.SetValue(AgentPolicyParser.SuspendedName, 1, RegistryValueKind.DWord);
            key.SetValue(AgentPolicyParser.SuspendedAtName, stamp, RegistryValueKind.String);
            key.SetValue(AgentPolicyParser.SuspendedByName, who, RegistryValueKind.String);
            key.SetValue(AgentPolicyParser.ChangedByName, who, RegistryValueKind.String);
            key.SetValue(AgentPolicyParser.ChangedAtName, stamp, RegistryValueKind.String);
            key.DeleteValue(AgentPolicyParser.ChangedFromName, throwOnMissingValue: false);
            SetOrDelete(key, AgentPolicyParser.ChangedReasonName, why);
        }
        else
        {
            key.SetValue(AgentPolicyParser.ChangedByName, who, RegistryValueKind.String);
            key.SetValue(AgentPolicyParser.ChangedAtName, stamp, RegistryValueKind.String);
            SetOrDelete(key, AgentPolicyParser.ChangedFromName, AgentPolicyParser.CleanFrom(changedFrom));
            SetOrDelete(key, AgentPolicyParser.ChangedReasonName, why);
            key.SetValue(AgentPolicyParser.SuspendedName, 0, RegistryValueKind.DWord);
        }
    }

    static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    public void SetLimits(int? minIntervalMinutes, int? maxAgentJobs)
    {
        if (minIntervalMinutes is { } m && !AgentPolicy.IsValidMinInterval(m)) throw new ArgumentOutOfRangeException(nameof(minIntervalMinutes), "The minimum interval must be 1 to 1440 minutes.");
        if (maxAgentJobs is { } j && !AgentPolicy.IsValidMaxAgentJobs(j)) throw new ArgumentOutOfRangeException(nameof(maxAgentJobs), "The maximum number of agent jobs must be 0 to 500.");
        if (minIntervalMinutes is null && maxAgentJobs is null) return;
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_keyPath);
        if (minIntervalMinutes is { } min) key.SetValue(AgentPolicyParser.MinIntervalName, min, RegistryValueKind.DWord);
        if (maxAgentJobs is { } max) key.SetValue(AgentPolicyParser.MaxJobsName, max, RegistryValueKind.DWord);
    }
}
