using Cadence.Agent.Policy;
using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>Phase 23: every word of the agent features (pill, note, "Updated ... by ...", the stale-save text and merge). Pure; the clock is the demo clock, never today.</summary>
public static class AgentTextTests
{
    static readonly DateTimeOffset Now = DemoServices.FixedNow;           // 2026-11-27 09:00 -05:00
    static readonly TimeZoneInfo Zone = DemoServices.FixedZone;
    static DateTimeOffset Today(int h, int m) => new(2026, 11, 27, h, m, 0, TimeSpan.FromHours(-5));

    static JobDefinition Job(string name = "Backup", string exe = "backup.exe", string cron = "0 9 * * *", bool locked = false) => new()
    {
        Id = "abcd1234",
        Name = name,
        Schedule = new ScheduleSpec(ValueList.Of(cron), HolidayCalendar.None, new DateOnly(2026, 9, 1)),
        Run = new RunSpec { Exe = exe },
        AgentLock = locked,
    };

    static ClassifiedTask Task(string? updatedBy, string? createdBy = "app", DateTimeOffset? updated = null, DateTimeOffset? created = null, bool locked = false, SyncStatus sync = SyncStatus.InSync)
    {
        JobDefinition job = Job(locked: locked);
        var meta = new CadenceMetadata(1, 1, 1, job, null, "fp", created ?? new DateTimeOffset(2026, 10, 1, 11, 20, 0, TimeSpan.FromHours(-5)), updated ?? Now.AddHours(-2),
            ValueList<string>.Empty, createdBy, updatedBy);
        return new ClassifiedTask(new ObservedTask("Backup", @"\Cadence\Backup", "", 3, null, null, null), Ownership.Managed, sync, null, meta, job, ValueList<FieldDiff>.Empty, null);
    }

    static AgentPolicy Off(string? by, string? reason = null, string? from = null) =>
        new(true, Today(8, 12), by, 5, 20, null, ChangedBy: by, ChangedAt: Today(8, 12), ChangedFrom: from, ChangedReason: reason);

    static AgentPolicy On(string? by, string? reason = null, int minute = 47) =>
        new(false, Today(8, 12), "app", 5, 20, null, ChangedBy: by, ChangedAt: Today(8, minute), ChangedReason: reason);

    // ---- "Updated ... by ..." (every by goes through Actor.Describe) ----

    public static void UpdatedLineSaysWhoForEveryKindOfWriter()
    {
        T.Eq("app", "Updated 2 h ago in the app", AgentText.UpdatedLine(Task("app"), Now, Zone));
        T.Eq("cli", "Updated 2 h ago from the command line", AgentText.UpdatedLine(Task("cli"), Now, Zone));
        T.Eq("agent", "Updated 2 h ago by Claude Code", AgentText.UpdatedLine(Task("agent:Claude Code"), Now, Zone));
        T.Eq("a v0.1.0 job has no writer", "Updated 2 h ago by unknown", AgentText.UpdatedLine(Task(null), Now, Zone));
        T.Eq("garbage is unknown, never an error", "Updated 2 h ago by unknown", AgentText.UpdatedLine(Task("???"), Now, Zone));
        T.Eq("minutes", "Updated 5 min ago in the app", AgentText.UpdatedLine(Task("app", updated: Now.AddMinutes(-5)), Now, Zone));
        T.Eq("an old change shows its date", "Updated 12 Oct in the app", AgentText.UpdatedLine(Task("app", updated: new DateTimeOffset(2026, 10, 12, 1, 45, 0, TimeSpan.FromHours(-5))), Now, Zone));
        T.True("a task without Cadence data has no line", AgentText.UpdatedLine(Task("app") with { Metadata = null }, Now, Zone) is null);
    }

    public static void ASpoofedAgentNameNeverReadsAsTheOwner()
    {
        string spoof = AgentText.UpdatedLine(Task("agent:Cadence (the app)"), Now, Zone)!;
        T.Eq("the spoofed name is marked", "Updated 2 h ago by Cadence (the app) (AI agent)", spoof);
        T.True("it does not read like 'in the app'", !spoof.Contains("in the app", StringComparison.Ordinal));
        T.Eq("the command line", "Updated 2 h ago by the command line (AI agent)", AgentText.UpdatedLine(Task("agent:the command line"), Now, Zone));
        T.Eq("unknown", "Updated 2 h ago by unknown (AI agent)", AgentText.UpdatedLine(Task("agent:unknown"), Now, Zone));
        T.Eq("a name with invisible characters", "Updated 2 h ago by Claude Code", AgentText.UpdatedLine(Task("agent:Claude\u202E Code"), Now, Zone));
    }

    public static void CreatedAndEditorLines()
    {
        T.Eq("created", "Created 1 Oct 2026 by Claude Code", AgentText.CreatedLine(Task("app", "agent:Claude Code"), Now, Zone));
        T.Eq("created by unknown", "Created 1 Oct 2026 by unknown", AgentText.CreatedLine(Task("app", null), Now, Zone));
        T.Eq("editor line", "Created 1 Oct 2026 by Claude Code \u00B7 Updated 2 h ago in the app", AgentText.EditorMetaLine(Task("app", "agent:Claude Code"), false, Now, Zone));
        T.Eq("drifted", "Created 1 Oct 2026 in the app \u00B7 Updated 2 h ago in the app \u00B7 changed in Task Scheduler since",
            AgentText.EditorMetaLine(Task("app", sync: SyncStatus.Drifted), false, Now, Zone));
        T.Eq("locked", "Created 1 Oct 2026 in the app \u00B7 Updated 2 h ago in the app \u00B7 Locked for AI agents", AgentText.EditorMetaLine(Task("app"), true, Now, Zone));
        T.True("a new schedule has no line", AgentText.EditorMetaLine(null, false, Now, Zone) is null);
        T.Eq("row tooltip", "Created 1 Oct 2026 by Claude Code\nUpdated 2 h ago in the app\nLocked: AI agents can't change this",
            AgentText.RowTooltip(Task("app", "agent:Claude Code", locked: true), Now, Zone));
    }

    // ---- the pill ----

    public static void PillHasThreeDistinctStates()
    {
        PillState allowed = AgentText.Pill(AgentPolicy.Defaults, Now, Zone);
        T.Eq("allowed kind", PillKind.Allowed, allowed.Kind);
        T.Eq("allowed text", "Agents allowed", allowed.Text);
        T.True("allowed tooltip offers to suspend", allowed.Tooltip.Contains("Click to suspend", StringComparison.Ordinal));
        T.True("allowed is not 'suspended'", !allowed.IsSuspended);

        PillState suspended = AgentText.Pill(Off("app"), Now, Zone);
        T.Eq("suspended kind", PillKind.Suspended, suspended.Kind);
        T.Eq("suspended text", "Agents suspended", suspended.Text);

        PillState bad = AgentText.Pill(AgentPolicy.Unreadable("the registry key could not be read"), Now, Zone);
        T.Eq("unreadable kind", PillKind.Unreadable, bad.Kind);
        T.Eq("unreadable text", "Agents suspended: setting unreadable", bad.Text);
        T.True("unreadable counts as suspended", bad.IsSuspended);
        T.True("unreadable tooltip names the problem", bad.Tooltip.Contains("the registry key could not be read", StringComparison.Ordinal));
    }

    public static void PillTooltipSaysWhoWhenAndWhy()
    {
        T.Eq("by the app", "Suspended in the app at 8:12.\nAI agents can see your schedules but can't change or run them. Click to resume.", AgentText.PillTooltip(Off("app"), Now, Zone));
        T.True("from the command line", AgentText.PillTooltip(Off("cli"), Now, Zone).StartsWith("Suspended from the command line at 8:12.", StringComparison.Ordinal));
        T.True("with ancestors", AgentText.PillTooltip(Off("cli", from: "cmd.exe < svchost.exe"), Now, Zone).Contains("(cmd.exe < svchost.exe)", StringComparison.Ordinal));
        T.True("by an agent, with its reason", AgentText.PillTooltip(Off("agent:Claude Code", "tidying up"), Now, Zone)
            .StartsWith("Suspended by AI agent Claude Code at 8:12: tidying up\n", StringComparison.Ordinal));
        T.True("nobody recorded", AgentText.PillTooltip(AgentPolicy.Defaults with { Suspended = true }, Now, Zone).StartsWith("Suspended.", StringComparison.Ordinal));
        T.True("yesterday shows the date", AgentText.PillTooltip(Off("app") with { SuspendedAt = Now.AddDays(-1) }, Now, Zone).StartsWith("Suspended in the app on 26 Nov at 9:00.", StringComparison.Ordinal));
        T.Eq("time text, other year", "on 3 Mar 2025 at 14:05", AgentText.TimeText(new DateTimeOffset(2025, 3, 3, 14, 5, 0, TimeSpan.FromHours(-5)), Now, Zone));
    }

    public static void ASpoofedSuspenderIsMarkedAsAnAgent()
    {
        string tip = AgentText.PillTooltip(Off("agent:Cadence (the app)", "x"), Now, Zone);
        T.True("marked once, not twice", tip.StartsWith("Suspended by Cadence (the app) (AI agent) at 8:12: x\n", StringComparison.Ordinal), tip);
    }

    public static void AReasonWithControlCharactersAndTwoHundredPlusCharactersIsCleaned()
    {
        string nasty = "line one\r\nline two\u0007\u202E" + new string('x', 400);
        string tip = AgentText.PillTooltip(Off("agent:Claude Code", nasty), Now, Zone);
        string firstLine = tip.Split('\n')[0];
        T.True("no control character survives", !firstLine.Any(char.IsControl));
        T.True("no bidi override survives", !tip.Contains('\u202E', StringComparison.Ordinal));
        string reason = AgentText.CleanReason(nasty);
        T.Eq("cut to 200", 200, reason.Length);
        T.True("lines joined", reason.StartsWith("line one line two", StringComparison.Ordinal));
        T.Eq("a null reason is empty", "", AgentText.CleanReason(null));
    }

    // ---- the note ----

    public static void NoteIsNullWhileSuspendedOrWhenNothingHappened()
    {
        T.True("suspended: none", AgentText.Note(Off("agent:Claude Code"), Now, Zone, null) is null);
        T.True("defaults: none", AgentText.Note(AgentPolicy.Defaults, Now, Zone, null) is null);
        T.True("unreadable: none", AgentText.Note(AgentPolicy.Unreadable("x"), Now, Zone, null) is null);
    }

    public static void ResumeByTheAppOrTheCommandLineShowsNoNote()
    {
        T.True("app", AgentText.Note(On("app"), Now, Zone, null) is null);
        T.True("cli", AgentText.Note(On("cli"), Now, Zone, null) is null);
        T.True("unknown writer", AgentText.Note(On("???"), Now, Zone, null) is null);
    }

    public static void ResumeByAnAgentShowsTheOrangeNote()
    {
        SwitchNote? note = AgentText.Note(On("agent:Claude Code", "Review is done."), Now, Zone, null);
        T.True("a note", note is not null);
        T.Eq("text", "AI agents were turned back on by AI agent Claude Code at 8:47", note!.Text);
        T.Eq("tooltip has the reason and the hint", "Reason given: Review is done.\nIf you didn't ask for this, suspend agents again and check your agent's tool permissions.", note.Tooltip);
        T.Eq("the note is dated by the change", Today(8, 47), note.At);
        T.True("without a reason: the hint only", AgentText.Note(On("agent:Claude Code"), Now, Zone, null)!.Tooltip == AgentText.ResumeHint);
        T.True("the plan's name works too", AgentText.SwitchNoteFor(On("agent:Claude Code"), Now, Zone, null) is not null);
    }

    public static void ASpoofedResumerIsStillAnAgent()
    {
        SwitchNote? note = AgentText.Note(On("agent:Cadence (the app) (AI agent)"), Now, Zone, null);
        T.True("a note, naming an agent", note is not null && note.Text.Contains("(AI agent)", StringComparison.Ordinal));
        SwitchNote? plain = AgentText.Note(On("agent:Cadence (the app)"), Now, Zone, null);
        T.Eq("the spoof reads as an agent", "AI agents were turned back on by Cadence (the app) (AI agent) at 8:47", plain!.Text);
    }

    /// <summary>
    /// [CP-E] A client name that only LOOKS like the app (a Cyrillic letter, fullwidth brackets, a zero-width joiner) passes Actor's exact-match check; the
    /// note must still say it was an AI agent, the way the pill's tooltip does.
    /// </summary>
    public static void ALookAlikeResumerIsStillCalledAnAgent()
    {
        string cyrillic = "Cadence (the " + (char)0x0430 + "pp)";
        string fullwidth = "Cadence " + (char)0xFF08 + "the app" + (char)0xFF09;
        foreach (string name in new[] { cyrillic, fullwidth, "Claude Code" })
        {
            SwitchNote? note = AgentText.Note(On("agent:" + name), Now, Zone, null);
            T.True("the note says AI agent for '" + name + "'", note is not null && (note.Text.StartsWith("AI agents were turned back on by AI agent ", StringComparison.Ordinal) || note.Text.Contains(" (AI agent) at ", StringComparison.Ordinal)), note?.Text);
        }
    }

    public static void SwitchClearedOutsideCadenceShowsTheOutsideNote()
    {
        // Suspended at 8:12, then cleared without Cadence: ChangedAt is still the suspension.
        var outside = new AgentPolicy(false, Today(8, 12), "app", 5, 20, null, ChangedBy: "app", ChangedAt: Today(8, 12));
        SwitchNote? note = AgentText.Note(outside, Now, Zone, null);
        T.Eq("text", "AI agents were turned back on outside Cadence", note!.Text);
        T.Eq("tooltip", "Something running as you cleared the switch without using Cadence. Suspend again if you didn't do this.", note.Tooltip);
        var noChanged = new AgentPolicy(false, Today(8, 12), "agent:Claude Code", 5, 20, null);
        T.Eq("no ChangedAt at all is outside too", AgentText.OutsideNoteText, AgentText.Note(noChanged, Now, Zone, null)!.Text);
    }

    public static void TheNoteIsGoneOnceTheOwnerHasSeenIt()
    {
        AgentPolicy resumed = On("agent:Claude Code");
        T.True("seen before the change: still there", AgentText.Note(resumed, Now, Zone, Today(8, 30)) is not null);
        T.True("seen at the change: gone", AgentText.Note(resumed, Now, Zone, Today(8, 47)) is null);
        T.True("seen after the change: gone", AgentText.Note(resumed, Now, Zone, Today(8, 50)) is null);
        AgentPolicy again = On("agent:Claude Code", minute: 55);
        T.True("a newer change after the mark: back", AgentText.Note(again, Now, Zone, Today(8, 50)) is not null);
        var outside = new AgentPolicy(false, Today(8, 12), "app", 5, 20, null, ChangedBy: "app", ChangedAt: Today(8, 12));
        T.True("outside, seen later: gone", AgentText.Note(outside, Now, Zone, Today(9, 0)) is null);
        T.True("outside, seen earlier: there", AgentText.Note(outside, Now, Zone, Today(8, 0)) is not null);
    }

    // ---- the questions ----

    public static void ResumeConfirmationAndStaleWords()
    {
        AgentConfirm ask = AgentText.ResumeConfirmation(AgentPolicy.Defaults with { Suspended = true });
        T.Eq("title", "Let AI agents change your schedules again?", ask.Title);
        T.Eq("button", "Resume", ask.Confirm);
        T.True("unreadable says what resuming does", AgentText.ResumeConfirmation(AgentPolicy.Unreadable("x")).Message.Contains("writes the setting again", StringComparison.Ordinal));
        // [CP-E] Q13: agents may also change, run and delete the owner's own schedules (unless locked); the question must not say "the schedules it made".
        T.True("the question says agents can reach the owner's schedules too", ask.Message.Contains("also the ones you made", StringComparison.Ordinal) && ask.Message.Contains("Locked schedules stay out of reach", StringComparison.Ordinal), ask.Message);
        T.True("and no longer claims agents only touch their own", !ask.Message.Contains("the schedules it made", StringComparison.Ordinal));

        T.Eq("stale", "This schedule was changed 2 h ago by Claude Code while you were editing.", AgentText.StaleMessage(Task("agent:Claude Code"), Now, Zone));
        T.Eq("stale by the app", "This schedule was changed 5 min ago by Cadence (the app) while you were editing.", AgentText.StaleMessage(Task("app", updated: Now.AddMinutes(-5)), Now, Zone));
        T.Eq("stale in Task Scheduler", "This schedule was changed in Task Scheduler while you were editing.", AgentText.StaleMessage(Task("app", sync: SyncStatus.Drifted), Now, Zone));
        T.Eq("stale spoof", "This schedule was changed 2 h ago by Cadence (the app) (AI agent) while you were editing.", AgentText.StaleMessage(Task("agent:Cadence (the app)"), Now, Zone));
        T.Eq("who: agent", "Claude Code", AgentText.ChangedBy(Task("agent:Claude Code")));
        T.Eq("who: Task Scheduler", "Task Scheduler", AgentText.ChangedBy(Task("app", sync: SyncStatus.Drifted)));
        T.Eq("buttons", "Reload and re-apply my change", AgentText.ReloadButton);
    }

    public static void MergeRePutsOnlyWhatIChanged()
    {
        JobDefinition baseline = Job();
        JobDefinition live = Job(exe: "agent-changed.exe");
        JobDefinition mine = Job(name: "My name");
        (JobDefinition merged, IReadOnlyList<string> conflicts) = AgentText.Merge(baseline, mine, live);
        T.Eq("my name", "My name", merged.Name);
        T.Eq("their program", "agent-changed.exe", merged.Run.Exe);
        T.Eq("no conflict", 0, conflicts.Count);

        (JobDefinition none, _) = AgentText.Merge(baseline, baseline, live);
        T.True("no change of mine: it is just the live job", none.Equals(live));

        JobDefinition both = Job(name: "Their name", cron: "0 10 * * *");
        JobDefinition mine2 = Job(name: "My name", cron: "0 11 * * *");
        (JobDefinition m2, IReadOnlyList<string> c2) = AgentText.Merge(baseline, mine2, both);
        T.Eq("mine wins", "My name", m2.Name);
        T.Eq("two conflicts", 2, c2.Count);
        T.True("named", c2.Contains("the name") && c2.Contains("the schedule"));

        (JobDefinition same, IReadOnlyList<string> c3) = AgentText.Merge(baseline, Job(name: "Same"), Job(name: "Same"));
        T.True("both made the same change: no conflict", c3.Count == 0 && same.Name == "Same");

        (JobDefinition lockLive, _) = AgentText.Merge(baseline, baseline, Job(locked: true));
        T.True("the lock always comes from the live job", lockLive.AgentLock);
    }

    public static void ReloadedStatusWords()
    {
        T.Eq("a change on top", "Reloaded. Your change is applied on top of Claude Code's version. Look it over, then save.", AgentText.ReloadedStatus("Claude Code", [], true));
        T.Eq("nothing of mine", "Reloaded. This is Claude Code's version.", AgentText.ReloadedStatus("Claude Code", [], false));
        T.True("conflicts", AgentText.ReloadedStatus("Claude Code", ["the name", "the schedule"], true).EndsWith("You both changed the name and the schedule, so your version of that is shown.", StringComparison.Ordinal));
    }

    // ---- the demo data the renders use ----

    public static void DemoJobsCoverEveryKindOfWriter()
    {
        ManagedSnapshot snap = new DemoServices(DemoSet.Normal).LoadManagedAsync().GetAwaiter().GetResult();
        string Line(string name) => AgentText.UpdatedLine(snap.Entries.First(e => e.Task.Job?.Name == name).Task, Now, Zone)!;
        T.Eq("agent", "Updated 2 h ago by Claude Code", Line("Sync notes"));
        T.Eq("cli", "Updated 3 d ago from the command line", Line("Weekly report"));
        T.Eq("v0.1.0", "Updated 12 Oct by unknown", Line("Nightly backup"));
        T.True("the rest in the app", Line("Good morning").EndsWith("in the app", StringComparison.Ordinal));
        T.Eq("exactly one locked job", 1, snap.Entries.Count(e => e.Task.Job?.AgentLock == true));
    }
}
