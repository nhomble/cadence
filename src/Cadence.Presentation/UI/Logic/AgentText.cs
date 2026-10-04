using System.Globalization;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;

namespace Cadence.App.UI.Logic;

/// <summary>The three looks of the header pill ([P23]): agents allowed (Green), suspended (Orange), unreadable so suspended for safety (Red).</summary>
public enum PillKind { Allowed, Suspended, Unreadable }

/// <summary>What the "Agents" pill in the header says and shows on hover.</summary>
public sealed record PillState(PillKind Kind, string Text, string Tooltip)
{
    public bool IsSuspended => Kind != PillKind.Allowed;
}

/// <summary>The Orange note that agents were turned back on (<see cref="At"/> is what the owner's "seen" mark is compared with).</summary>
public sealed record SwitchNote(string Text, string Tooltip, DateTimeOffset At);

/// <summary>A question for the in-app confirmation panel.</summary>
public sealed record AgentConfirm(string Title, string Message, string Confirm);

/// <summary>
/// All the words of the agent features (PLAN 12.7, [CP-R12], [CP-D]), pure so they are unit tested. Every "by ..." goes through
/// <see cref="Actor.Describe"/> (which is spoof-safe since Review R): this class never builds an agent's name itself, and an agent calling itself
/// "Cadence (the app)" reads as "Cadence (the app) (AI agent)", never as the owner.
/// </summary>
public static class AgentText
{
    public const int MaxReason = 200;

    /// <summary>[CP-E] The Red pill leads with the state (it said "Agents unreadable", but it is the setting that can't be read, not the agents).</summary>
    public const string UnreadablePillText = "Agents suspended: setting unreadable";

    /// <summary>
    /// [CP-E] The resume question. It said agents could change "the schedules it made", which was wrong: by the owner's Q13 default an agent may also change, run
    /// and delete the schedules the owner made, unless they are locked.
    /// </summary>
    public const string ResumeMessage = "While agents are allowed, an AI agent that is connected to Cadence can add schedules and change, run or delete any of your Cadence schedules, also the ones you made. Locked schedules stay out of reach.";

    public const string ResumeHint ="If you didn't ask for this, suspend agents again and check your agent's tool permissions.";
    public const string OutsideNoteText = "AI agents were turned back on outside Cadence";
    public const string OutsideNoteTooltip = "Something running as you cleared the switch without using Cadence. Suspend again if you didn't do this.";

    // ---- who and when ----

    /// <summary>
    /// "in the app", "from the command line", "by Claude Code", "by unknown" (the list rows and the editor line). [CP-E] It said "in Cadence", which also
    /// fits an agent's change (agents work through Cadence too); "in the app" names the owner's own route unambiguously.
    /// </summary>
    public static string Via(Actor actor) => actor.Kind switch
    {
        ActorKind.App => "in the app",
        ActorKind.Cli => "from the command line",
        _ => "by " + actor.Describe(),
    };

    /// <summary>The same for a switch change: "in the app", "from the command line", "by AI agent Claude Code", "" when nobody is recorded.</summary>
    public static string SwitchVia(string? token)
    {
        if (token is null) return "";
        Actor actor = Actor.Parse(token);
        return actor.Kind switch
        {
            ActorKind.Unknown => "",
            ActorKind.Agent => "by " + AgentLabel(actor),
            _ => Via(actor),
        };
    }

    /// <summary>"AI agent Claude Code" (the name comes from <see cref="Actor.Describe"/>; a name that already says "(AI agent)" is not prefixed twice).</summary>
    static string AgentLabel(Actor actor)
    {
        string d = actor.Describe();
        return d.EndsWith("(AI agent)", StringComparison.Ordinal) ? d : "AI agent " + d;
    }

    /// <summary>"at 14:02" today, "on 26 Nov at 14:02" before, with the year when it is not this year.</summary>
    public static string TimeText(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTime a = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        DateTime n = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        string clock = a.ToString("H:mm", CultureInfo.InvariantCulture);
        if (a.Date == n.Date) return "at " + clock;
        return "on " + a.ToString(a.Year == n.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture) + " at " + clock;
    }

    /// <summary>A reason as the owner sees it: control and invisible characters gone, one line, at most 200 characters.</summary>
    public static string CleanReason(string? reason) =>
        AuditText.Clean(reason?.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), MaxReason);   // a line break is a word break, not nothing

    // ---- the pill ----

    public static PillState Pill(AgentPolicy policy, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.FailedClosed)
        {
            string problem = CleanReason(policy.ReadProblem);
            return new PillState(PillKind.Unreadable, UnreadablePillText,
                "Cadence couldn't read the AI agent setting" + (problem.Length > 0 ? ": " + problem.TrimEnd('.') : "") + ". Agents are suspended for safety. "
                + "Click to turn them back on once the setting is fixed.");
        }
        if (policy.Suspended)
            return new PillState(PillKind.Suspended, "Agents suspended", PillTooltip(policy, now, zone));
        return new PillState(PillKind.Allowed, "Agents allowed", "AI agents can add and change your Cadence schedules. Click to suspend them.");
    }

    /// <summary>"Suspended by AI agent Claude Code at 14:02: tidying up. AI agents can see ... Click to resume." ([CP-D]).</summary>
    public static string PillTooltip(AgentPolicy policy, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Suspended) return "AI agents can add and change your Cadence schedules. Click to suspend them.";
        if (policy.FailedClosed) return Pill(policy, now, zone).Tooltip;
        string who = SwitchVia(policy.SuspendedBy ?? policy.ChangedBy);
        DateTimeOffset? at = policy.SuspendedAt ?? policy.ChangedAt;
        string when = at is { } t ? " " + TimeText(t, now, zone) : "";
        string reason = CleanReason(policy.ChangedReason);
        string line = "Suspended" + (who.Length > 0 ? " " + who : "") + when + (reason.Length > 0 ? ": " + reason : ".");
        if (policy.ChangedFrom is { Length: > 0 } from && Actor.Parse(policy.SuspendedBy).Kind == ActorKind.Cli) line += " (" + CleanReason(from) + ")";
        return line + "\nAI agents can see your schedules but can't change or run them. Click to resume.";
    }

    // ---- the note ----

    /// <summary>
    /// The Orange note of [CP-R12] and [CP-D]: null, "turned back on outside Cadence" (the switch was cleared without Cadence), or "turned back on by
    /// Claude Code at 14:02" (an agent resumed). <paramref name="seenAt"/> is when the owner last opened the ⋯ menu: a change at or before it is acknowledged.
    /// A resume by the app or the command line shows nothing.
    /// </summary>
    public static SwitchNote? Note(AgentPolicy policy, DateTimeOffset now, TimeZoneInfo zone, DateTimeOffset? seenAt)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Suspended || policy.FailedClosed) return null;
        if (policy.ResumedOutsideCadence)
        {
            DateTimeOffset marker = policy.SuspendedAt ?? policy.ChangedAt ?? DateTimeOffset.MinValue;
            if (seenAt is { } seen && seen >= marker) return null;
            return new SwitchNote(OutsideNoteText, OutsideNoteTooltip, marker);
        }
        if (policy.ChangedBy is { } by && policy.ChangedAt is { } changed)
        {
            Actor actor = Actor.Parse(by);
            if (actor.Kind != ActorKind.Agent) return null;
            if (seenAt is { } seen && seen >= changed) return null;
            string reason = CleanReason(policy.ChangedReason);
            string tip = (reason.Length > 0 ? "Reason given: " + reason + "\n" : "") + ResumeHint;
            // [CP-E] "by AI agent <name>", like the pill's tooltip: a client name that merely LOOKS like "Cadence (the app)" (a Cyrillic letter, fullwidth brackets)
            // passes Actor's exact-match spoof check, but must still read as an agent here.
            return new SwitchNote("AI agents were turned back on by " + AgentLabel(actor) + " " + TimeText(changed, now, zone), tip, changed);
        }
        return null;
    }

    /// <summary>The [CP-D] name for <see cref="Note"/> (the plan calls it SwitchNote).</summary>
    public static SwitchNote? SwitchNoteFor(AgentPolicy policy, DateTimeOffset now, TimeZoneInfo zone, DateTimeOffset? seenAt) => Note(policy, now, zone, seenAt);

    /// <summary>The question when the owner turns agents back on from the pill or the menu.</summary>
    public static AgentConfirm ResumeConfirmation(AgentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.FailedClosed
            ? new AgentConfirm("Let AI agents change your schedules again?",
                "Cadence couldn't read the agent setting, so agents are suspended for safety. Turning them on writes the setting again.", "Resume")
            : new AgentConfirm("Let AI agents change your schedules again?",
                ResumeMessage, "Resume");
    }

    // ---- job attribution ----

    /// <summary>"Updated 2 h ago by Claude Code" / "... in the app" / "... from the command line" / "... by unknown". Null when the task has no Cadence data.</summary>
    public static string? UpdatedLine(ClassifiedTask task, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Metadata is not { } m) return null;
        return "Updated " + ListFormat.Ago(m.Updated, now, zone) + " " + Via(Actor.Parse(m.UpdatedBy));
    }

    /// <summary>[P25] The Updated cell of the table: "2 h ago by Claude Code" / "yesterday in the app" / "3 d ago from the command line", and the instant for sorting. Empty without Cadence data.</summary>
    public static (string Text, DateTimeOffset? At) UpdatedCell(ClassifiedTask task, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Metadata is not { } m) return ("", null);
        return (ListFormat.Ago(m.Updated, now, zone) + " " + Via(Actor.Parse(m.UpdatedBy)), m.Updated);
    }

    /// <summary>"Created 1 Oct 2026 by Claude Code".</summary>
    public static string? CreatedLine(ClassifiedTask task, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Metadata is not { } m) return null;
        DateTime c = TimeZoneInfo.ConvertTime(m.Created, zone).DateTime;
        return "Created " + c.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + " " + Via(Actor.Parse(m.CreatedBy));
    }

    /// <summary>The full text of the list row's tooltip: both lines and the lock.</summary>
    public static string? RowTooltip(ClassifiedTask task, DateTimeOffset now, TimeZoneInfo zone)
    {
        string? created = CreatedLine(task, now, zone);
        string? updated = UpdatedLine(task, now, zone);
        if (created is null || updated is null) return null;
        string text = created + "\n" + updated;
        if (task.Job is { AgentLock: true }) text += "\n" + LockedTooltip;
        return text;
    }

    public const string LockedTooltip = "Locked: AI agents can't change this";

    /// <summary>The editor's Dim line: "Created 1 Oct 2026 by Claude Code \u00B7 Updated 2 h ago in the app \u00B7 changed in Task Scheduler since \u00B7 Locked for AI agents".</summary>
    public static string? EditorMetaLine(ClassifiedTask? task, bool locked, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (task is null) return null;
        string? created = CreatedLine(task, now, zone);
        string? updated = UpdatedLine(task, now, zone);
        if (created is null || updated is null) return null;
        string text = created + " \u00B7 " + updated;
        if (task.Sync == SyncStatus.Drifted) text += " \u00B7 changed in Task Scheduler since";
        if (locked) text += " \u00B7 Locked for AI agents";
        return text;
    }

    // ---- the stale save ----

    public const string StaleTitle = "Changed while you were editing";
    public const string ReloadButton = "Reload and re-apply my change";
    public const string KeepEditingButton = "Keep editing";

    /// <summary>"This schedule was changed 2 min ago by Claude Code while you were editing." (from the LIVE task, never the agent's own words).</summary>
    public static string StaleMessage(ClassifiedTask live, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Sync == SyncStatus.Drifted) return "This schedule was changed in Task Scheduler while you were editing.";
        if (live.Metadata is not { } m) return "This schedule was changed while you were editing.";
        return "This schedule was changed " + ListFormat.Ago(m.Updated, now, zone) + " by " + Actor.Parse(m.UpdatedBy).Describe() + " while you were editing.";
    }

    /// <summary>Who changed the live job, for a sentence: "Claude Code", "Cadence (the app)", "the command line", "unknown", or "Task Scheduler" for an edit made there.</summary>
    public static string ChangedBy(ClassifiedTask live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Sync == SyncStatus.Drifted) return "Task Scheduler";
        return Actor.Parse(live.Metadata?.UpdatedBy).Describe();
    }

    public const string StaleExplanation = "Reload brings in their version and puts your change back on top of it, so you can look it over and save. Keep editing leaves everything as it is.";

    // ---- merging my change onto the live job ----

    /// <summary>
    /// Re-applies my change on the live job: every part (name, schedule, program, settings, on/off) I changed since I opened it is taken from my draft,
    /// every other part from the live job. <c>Conflicts</c> names the parts both of us changed (mine wins; the person is told).
    /// </summary>
    public static (JobDefinition Merged, IReadOnlyList<string> Conflicts) Merge(JobDefinition baseline, JobDefinition mine, JobDefinition live)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(live);
        var conflicts = new List<string>();

        T Pick<T>(string label, T b, T m, T l) where T : notnull
        {
            if (m.Equals(b)) return l;
            if (!l.Equals(b) && !l.Equals(m)) conflicts.Add(label);
            return m;
        }

        JobDefinition merged = live with
        {
            Name = Pick("the name", baseline.Name, mine.Name, live.Name),
            Schedule = Pick("the schedule", baseline.Schedule, mine.Schedule, live.Schedule),
            Run = Pick("the program", baseline.Run, mine.Run, live.Run),
            Settings = Pick("the settings", baseline.Settings, mine.Settings, live.Settings),
            Enabled = Pick("on or off", baseline.Enabled, mine.Enabled, live.Enabled),
        };
        return (merged, conflicts);
    }

    /// <summary>The line under the reloaded editor: what was kept and whether any change was overwritten.</summary>
    public static string ReloadedStatus(string who, IReadOnlyList<string> conflicts, bool anyChange)
    {
        string head = anyChange
            ? "Reloaded. Your change is applied on top of " + who + "'s version. Look it over, then save."
            : "Reloaded. This is " + who + "'s version.";
        if (conflicts.Count == 0) return head;
        return head + " You both changed " + string.Join(" and ", conflicts) + ", so your version of that is shown.";
    }

    // ---- the menu ----

    public const string SuspendMenuItem = "Suspend AI agents";
    public const string ActivityMenuItem = "Agent activity\u2026";
    public const string LockMenuItem = "Don't let AI agents change this";
}
