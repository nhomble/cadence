using System.Globalization;

namespace Cadence.Agent.Policy;

/// <summary>
/// The owner's controls over AI agents (PLAN 12.4.2, 12.4.5): the global suspension switch and the two limits, as read from
/// <c>HKCU\Software\&lt;Folder&gt;\Agents</c> at one moment. Nothing here is cached by anyone: every tool call reads it again.
/// </summary>
/// <param name="Suspended">True while agents may not change anything. Also true when the setting could not be read (<see cref="FailedClosed"/>).</param>
/// <param name="SuspendedAt">When the switch was turned on (null when unknown).</param>
/// <param name="SuspendedBy">Who turned the switch on: "app", "cli" or "agent:&lt;name&gt;" (anything else in the registry reads as null) ([OWNER-Q14]).</param>
/// <param name="MinIntervalMinutes">Agents may not create schedules that fire more often than this (1-1440, default 5).</param>
/// <param name="MaxAgentJobs">Agents may create at most this many jobs (0-500, default 20).</param>
/// <param name="ReadProblem">What could not be read or was out of range. With <see cref="FailedClosed"/> it is the reason agents are refused; otherwise only a note (the value in question fell back to its default).</param>
/// <param name="FailedClosed">True when <see cref="Suspended"/> is true BECAUSE the setting could not be trusted (wrong type or value, unreadable key).</param>
/// <param name="ChangedBy">"app", "cli" or "agent:&lt;name&gt;": who last changed the switch through Cadence ([CP-R2], [OWNER-Q14]).</param>
/// <param name="ChangedAt">When Cadence last changed the switch.</param>
/// <param name="ChangedFrom">For a command-line change: up to three ancestor process names ("cmd.exe &lt; svchost.exe").</param>
/// <param name="ChangedReason">The optional short reason given with the last change (an agent's <c>reason</c>), cleaned and capped.</param>
public sealed record AgentPolicy(bool Suspended, DateTimeOffset? SuspendedAt, string? SuspendedBy, int MinIntervalMinutes, int MaxAgentJobs, string? ReadProblem,
    bool FailedClosed = false, string? ChangedBy = null, DateTimeOffset? ChangedAt = null, string? ChangedFrom = null, string? ChangedReason = null)
{
    public const int DefaultMinIntervalMinutes = 5;
    public const int DefaultMaxAgentJobs = 20;
    public const int MinIntervalLowest = 1;
    public const int MinIntervalHighest = 1440;
    public const int MaxAgentJobsLowest = 0;
    public const int MaxAgentJobsHighest = 500;

    /// <summary>Nothing configured: agents allowed, default limits.</summary>
    public static AgentPolicy Defaults { get; } = new(false, null, null, DefaultMinIntervalMinutes, DefaultMaxAgentJobs, null);

    /// <summary>The state when the setting cannot be trusted: agents are refused (fail closed) and the limits are the defaults.</summary>
    public static AgentPolicy Unreadable(string reason) =>
        new(true, null, null, DefaultMinIntervalMinutes, DefaultMaxAgentJobs, reason, FailedClosed: true);

    /// <summary>
    /// [CP-R2] The switch is off although the last thing Cadence recorded is the suspension itself: someone cleared the value without Cadence
    /// (for example <c>reg.exe</c> or a script). A resume made through the app or the command line writes a newer <see cref="ChangedAt"/> and so never reads as this.
    /// Limit: deleting the whole key leaves nothing to compare with and is not detected.
    /// </summary>
    public bool ResumedOutsideCadence => !Suspended && SuspendedAt is { } s && (ChangedAt is not { } c || c <= s);

    public static bool IsValidMinInterval(int minutes) => minutes is >= MinIntervalLowest and <= MinIntervalHighest;

    public static bool IsValidMaxAgentJobs(int jobs) => jobs is >= MaxAgentJobsLowest and <= MaxAgentJobsHighest;

    /// <summary>The ISO 8601 text with offset that is shown in JSON (whole seconds).</summary>
    public static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>The ISO 8601 text with offset and milliseconds that is stored in the registry (so a resume right after a suspend still reads as newer).</summary>
    public static string Stamp(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}

/// <summary>
/// The READ-ONLY view of the policy. There is no setter on this type; the tools that only read (and the gate that checks every write) receive nothing more.
/// <see cref="Read"/> never throws: if the setting cannot be read it returns <see cref="AgentPolicy.Unreadable"/> (fail closed).
/// </summary>
public interface IAgentPolicyReader
{
    AgentPolicy Read();
}

/// <summary>
/// [OWNER-Q14] The narrow WRITE interface for the suspension switch and nothing else. The owner decided that agents may suspend AND resume themselves
/// through two distinct tools (<c>cadence_suspend_agents</c>, <c>cadence_resume_agents</c>) so that a tool-permission layer can allow, ask about or deny
/// that one action separately from every other agent action. Cadence therefore does not try to stop a resume: it makes it distinct, attributed
/// (<c>app</c>, <c>cli</c> or <c>agent:&lt;name&gt;</c>), audited and visible. Limits, the per-job lock and the audit log are NOT reachable through this interface.
/// </summary>
public interface IAgentSwitch : IAgentPolicyReader
{
    /// <param name="suspended">True to turn the switch on, false to resume.</param>
    /// <param name="by">"app", "cli" or "agent:&lt;name&gt;" (anything else throws <see cref="ArgumentException"/>).</param>
    /// <param name="at">When (recorded as <c>SuspendedAt</c> on suspend and as <c>ChangedAt</c> on both).</param>
    /// <param name="changedFrom">For a command-line change: the ancestor process names ([CP-R2]); null otherwise.</param>
    /// <param name="reason">An optional short reason (an agent's), cleaned and capped at 200 characters; null clears the previous one.</param>
    void SetSuspended(bool suspended, string by, DateTimeOffset at, string? changedFrom = null, string? reason = null);
}

/// <summary>The owner's full side: the switch plus the limits. The app and the command line only; never passed to the MCP tool host.</summary>
public interface IAgentPolicyStore : IAgentSwitch
{
    /// <summary>Sets the limits that are not null; a value outside 1-1440 / 0-500 throws <see cref="ArgumentOutOfRangeException"/> and nothing is written.</summary>
    void SetLimits(int? minIntervalMinutes, int? maxAgentJobs);
}
