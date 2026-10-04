namespace Cadence.Agent.Policy;

/// <summary>An in-memory policy for unit tests and the dev-only screenshot tool, with the same semantics as the registry store (no I/O).</summary>
public sealed class MemoryAgentPolicyStore : IAgentPolicyStore
{
    readonly object _gate = new();
    AgentPolicy _policy = AgentPolicy.Defaults;
    string? _fault;

    public MemoryAgentPolicyStore() { }

    public MemoryAgentPolicyStore(AgentPolicy initial) { _policy = initial; }

    /// <summary>How many times <see cref="Read"/> was called (tests: "re-read on every call").</summary>
    public int Reads { get; private set; }

    /// <summary>When set, <see cref="Read"/> behaves like an unreadable registry: it returns <see cref="AgentPolicy.Unreadable"/> with this reason. Null clears it.</summary>
    public string? Fault
    {
        get { lock (_gate) return _fault; }
        set { lock (_gate) _fault = value; }
    }

    /// <summary>When set, <see cref="Read"/> throws this (tests of code that must fail closed even if a reader misbehaves).</summary>
    public Exception? ThrowOnRead { get; set; }

    public AgentPolicy Read()
    {
        lock (_gate)
        {
            Reads++;
            if (ThrowOnRead is { } ex) throw ex;
            return _fault is null ? _policy : AgentPolicy.Unreadable(_fault);
        }
    }

    /// <summary>When set, <see cref="SetSuspended"/> throws this (tests of "the flag could not be written: state unchanged"). Null clears it.</summary>
    public Exception? ThrowOnWrite { get; set; }

    public void SetSuspended(bool suspended, string by, DateTimeOffset at, string? changedFrom = null, string? reason = null)
    {
        string who = AgentPolicyParser.NormalizeWho(by) ?? throw new ArgumentException("by must be \"app\", \"cli\" or \"agent:<name>\".", nameof(by));
        string? why = AgentPolicyParser.CleanFrom(reason);
        lock (_gate)
        {
            if (ThrowOnWrite is { } ex) throw ex;
            _policy = suspended
                ? _policy with { Suspended = true, SuspendedAt = at, SuspendedBy = who, ChangedBy = who, ChangedAt = at, ChangedFrom = null, ChangedReason = why, FailedClosed = false }
                : _policy with { Suspended = false, ChangedBy = who, ChangedAt = at, ChangedFrom = AgentPolicyParser.CleanFrom(changedFrom), ChangedReason = why, FailedClosed = false };
        }
    }

    public void SetLimits(int? minIntervalMinutes, int? maxAgentJobs)
    {
        if (minIntervalMinutes is { } m && !AgentPolicy.IsValidMinInterval(m)) throw new ArgumentOutOfRangeException(nameof(minIntervalMinutes));
        if (maxAgentJobs is { } j && !AgentPolicy.IsValidMaxAgentJobs(j)) throw new ArgumentOutOfRangeException(nameof(maxAgentJobs));
        lock (_gate)
        {
            _policy = _policy with { MinIntervalMinutes = minIntervalMinutes ?? _policy.MinIntervalMinutes, MaxAgentJobs = maxAgentJobs ?? _policy.MaxAgentJobs };
        }
    }

    /// <summary>Simulates someone clearing the value without Cadence (reg.exe): Suspended becomes false and nothing else changes.</summary>
    public void ClearSuspendedOutsideCadence()
    {
        lock (_gate) _policy = _policy with { Suspended = false };
    }
}
