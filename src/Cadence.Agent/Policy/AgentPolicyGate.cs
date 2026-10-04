using System.Globalization;
using Cadence.Core.Metadata;
using Cadence.Core.Planning;

namespace Cadence.Agent.Policy;

/// <summary>A refusal by the policy: the MCP error code (<c>suspended</c>, <c>schedule_too_frequent</c>, <c>limit_reached</c>) and the sentence the agent reads.</summary>
public sealed record PolicyRefusal(string Code, string Message);

/// <summary>
/// THE seam the write tools call (PLAN 12.3.2 steps 2, 5 and 6; 12.4.2; 12.4.5): every method reads the policy again from its
/// <see cref="IAgentPolicyReader"/> (nothing is cached, so a switch flipped between two calls is obeyed by the very next one), and every method fails
/// closed. A tool calls <see cref="CheckWrite"/> at the start (gate 2) and AGAIN immediately before it writes (gate 6); the read tools never call it.
/// <para>
/// Phase 21/22 wiring: <c>CheckWrite</c> first, then <c>CheckSchedule</c> (when the call sets a schedule) and <c>CheckCreate</c> (on create), then
/// <c>CheckWrite</c> once more right before <c>Applier</c>.
/// </para>
/// </summary>
public sealed class AgentPolicyGate(IAgentPolicyReader reader, TimeZoneInfo zone)
{
    public IAgentPolicyReader Reader { get; } = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>The policy as it is right now. Never throws: a reader that misbehaves is treated like an unreadable registry (fail closed).</summary>
    public AgentPolicy Read()
    {
        try { return Reader.Read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AgentPolicy.Unreadable("the agent settings could not be read (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>Null when agents may write right now; otherwise the <c>suspended</c> refusal (also when the setting cannot be read).</summary>
    public PolicyRefusal? CheckWrite() => CheckWrite(Read());

    public PolicyRefusal? CheckWrite(AgentPolicy policy) => policy.Suspended ? new PolicyRefusal("suspended", SuspendedMessage(policy, zone)) : null;

    /// <summary>
    /// The minimum-interval rule: refused when the schedule fires more often than <see cref="AgentPolicy.MinIntervalMinutes"/>. <paramref name="minimumGap"/> is
    /// <c>ScheduleDensity.MinimumGap</c> (null = fewer than two runs, never too frequent). Exactly the limit is allowed (5 minutes at a limit of 5).
    /// </summary>
    public PolicyRefusal? CheckSchedule(TimeSpan? minimumGap) => CheckSchedule(minimumGap, Read());

    public static PolicyRefusal? CheckSchedule(TimeSpan? minimumGap, AgentPolicy policy)
    {
        if (!IsTooFrequent(minimumGap, policy.MinIntervalMinutes)) return null;
        int gap = GapMinutes(minimumGap!.Value);
        return new PolicyRefusal("schedule_too_frequent",
            "This schedule runs every " + Plural(gap, "minute") + "; agents may schedule at most every " + policy.MinIntervalMinutes.ToString(CultureInfo.InvariantCulture)
            + " minutes. Use a slower schedule, or ask the owner to change the limit (Cadence.exe --agents config --min-interval N).");
    }

    /// <summary>True when <paramref name="minimumGap"/> is shorter than the limit. A null gap (@logon, yearly) is never too frequent.</summary>
    public static bool IsTooFrequent(TimeSpan? minimumGap, int minIntervalMinutes) =>
        minimumGap is { } g && g < TimeSpan.FromMinutes(minIntervalMinutes);

    /// <summary>The gap as whole minutes for a message (at least 1).</summary>
    public static int GapMinutes(TimeSpan gap) => (int)Math.Max(1, Math.Round(gap.TotalMinutes));

    /// <summary>
    /// The cap on agent-created jobs: refused when <paramref name="agentJobs"/> (see <see cref="CountAgentJobs"/>) has already reached
    /// <see cref="AgentPolicy.MaxAgentJobs"/> (19 of 20 may create the 20th; 20 of 20 may not; a limit of 0 forbids creating).
    /// </summary>
    public PolicyRefusal? CheckCreate(int agentJobs) => CheckCreate(agentJobs, Read());

    public static PolicyRefusal? CheckCreate(int agentJobs, AgentPolicy policy)
    {
        if (agentJobs < policy.MaxAgentJobs) return null;
        string limit = policy.MaxAgentJobs == 0
            ? "agents may not create jobs on this PC right now"
            : "agents may create at most " + policy.MaxAgentJobs.ToString(CultureInfo.InvariantCulture) + " jobs and " + agentJobs.ToString(CultureInfo.InvariantCulture) + " already exist";
        return new PolicyRefusal("limit_reached",
            "Not created: " + limit + ". Ask the owner to delete some jobs or to raise the limit (Cadence.exe --agents config --max-jobs N).");
    }

    /// <summary>
    /// How many managed jobs an agent created: <c>createdBy</c> is <c>agent:*</c>. A job the owner created and an agent later changed does not count (only
    /// <c>updatedBy</c> is an agent); a job an agent created and the owner later changed still counts. A copy made outside Cadence (duplicate id) is not
    /// counted twice, and a job whose metadata cannot be read has no creator.
    /// </summary>
    public static int CountAgentJobs(IEnumerable<ClassifiedTask> managed) =>
        managed.Count(c => c.DuplicateOf is null && c.Metadata is { } m && Actor.Parse(m.CreatedBy).Kind == ActorKind.Agent);

    /// <summary>
    /// The text an agent reads when a write is refused (12.4.2). A setting that could not be read says so; a real suspension says since when and from where.
    /// </summary>
    public static string SuspendedMessage(AgentPolicy policy, TimeZoneInfo zone)
    {
        if (policy.FailedClosed)
            return "Cadence could not read the agent settings (" + (policy.ReadProblem ?? "unknown reason") + "), so it refuses changes to be safe. Nothing was changed. Tell the user: they can check it with Cadence.exe --agents status.";
        string since = "";
        if (policy.SuspendedAt is { } at)
        {
            string when = TimeZoneInfo.ConvertTime(at, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
            string from = policy.SuspendedBy switch { "app" => ", from the Cadence app", "cli" => ", from the command line", _ => "" };
            since = " (since " + when + from + ")";
        }
        return "AI agents are suspended on this PC" + since + ". Nothing was changed. The owner resumes agents in the Cadence app (... menu > Resume AI agents) or with \"Cadence.exe --agents resume\"; do not call cadence_resume_agents unless the user clearly asks you to. Tell the user what you were going to do.";
    }

    static string Plural(int n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");
}
