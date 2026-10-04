using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// Phase 23 view-model behaviour: the header pill and its refresh, the confirmation panel (inside the window, never a separate one), the Orange note and its
/// acknowledgement, Agent activity, the limits panel, the per-job lock and the stale-save flow. No WPF, no Task Scheduler, no registry: scripted services and the demo clock.
/// </summary>
public static class AgentViewModelTests
{
    static DateTimeOffset Today(int h, int m) => new(2026, 11, 27, h, m, 0, TimeSpan.FromHours(-5));

    static (MainViewModel Vm, ScriptedServices Services, RecordingDialogs Dialogs) Window(AgentPolicy? policy = null)
    {
        var services = new ScriptedServices();
        if (policy is not null) services.Policy = policy;
        var dialogs = new RecordingDialogs();
        var vm = new MainViewModel(services, dialogs, dialogs);
        vm.Start();
        vm.InitialLoad.GetAwaiter().GetResult();
        return (vm, services, dialogs);
    }

    static AgentPolicy Off(string by = "app", string? reason = null) => new(true, Today(8, 12), by, 5, 20, null, ChangedBy: by, ChangedAt: Today(8, 12), ChangedReason: reason);

    static AgentPolicy On(string by, string? reason = null, int minute = 47) => new(false, Today(8, 12), "app", 5, 20, null, ChangedBy: by, ChangedAt: Today(8, minute), ChangedReason: reason);

    static bool WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) Thread.Sleep(10);
        return condition();
    }

    // ---- the pill ----

    public static void PillAppearsOnlyOnceTheSwitchHasBeenRead()
    {
        var services = new ScriptedServices();
        var dialogs = new RecordingDialogs();
        var vm = new MainViewModel(services, dialogs, dialogs);
        T.True("before the first read: hidden", !vm.IsPillVisible);
        vm.Start();
        vm.InitialLoad.GetAwaiter().GetResult();
        T.True("after: visible", vm.IsPillVisible);
        T.Eq("allowed", "Agents allowed", vm.PillText);
        T.Eq("green", PillKind.Allowed, vm.PillKind);
        T.True("menu item unchecked", !vm.AgentsSuspended);
        T.Eq("one read at start", 1, services.PolicyReads);
    }

    public static void PillShowsSuspendedAndWhoDidIt()
    {
        var (vm, _, _) = Window(Off("agent:Claude Code", "tidying up"));
        T.Eq("text", "Agents suspended", vm.PillText);
        T.Eq("orange", PillKind.Suspended, vm.PillKind);
        T.True("tooltip names the agent, the time and the reason", vm.PillTooltip.StartsWith("Suspended by AI agent Claude Code at 8:12: tidying up", StringComparison.Ordinal));
        T.True("menu item checked", vm.AgentsSuspended);
    }

    public static void AnUnreadableSwitchShowsUnreadableSuspendedForSafety()
    {
        var services = new ScriptedServices { PolicyFails = new IOException("boom") };
        var dialogs = new RecordingDialogs();
        var vm = new MainViewModel(services, dialogs, dialogs);
        vm.Start();
        vm.InitialLoad.GetAwaiter().GetResult();
        T.Eq("kind", PillKind.Unreadable, vm.PillKind);
        T.Eq("text", "Agents suspended: setting unreadable", vm.PillText);
        T.True("counts as suspended", vm.AgentsSuspended);
        T.True("pill is shown", vm.IsPillVisible);

        var (vm2, _, _) = Window(AgentPolicy.Unreadable("the key could not be read"));
        T.Eq("an unreadable policy value too", PillKind.Unreadable, vm2.PillKind);
    }

    public static void ThePolicyIsReReadWithEveryRefresh()
    {
        var (vm, services, _) = Window();
        services.Policy = Off("cli");
        vm.OnPollTick();
        T.True("the 30 s poll re-reads the switch", WaitFor(() => vm.PillKind == PillKind.Suspended));
        T.True("its tooltip says command line", vm.PillTooltip.StartsWith("Suspended from the command line", StringComparison.Ordinal));
        services.Policy = AgentPolicy.Defaults;
        vm.RefreshCommand.Execute(null);
        T.True("F5 re-reads it too", WaitFor(() => vm.PillKind == PillKind.Allowed));
        T.True("a read for every refresh", services.PolicyReads >= 3);
    }

    // ---- toggling ----

    public static void SuspendingIsImmediateAndNeedsNoQuestion()
    {
        var (vm, services, dialogs) = Window();
        vm.ToggleAgentsAsync().GetAwaiter().GetResult();
        T.True("the app wrote the switch", services.Calls.Contains("suspend"));
        T.True("no panel", vm.ActivePanel is null && dialogs.Chosen.Count == 0);
        T.Eq("the pill now says suspended", "Agents suspended", vm.PillText);
        T.True("tooltip says in the app", vm.PillTooltip.StartsWith("Suspended in the app at 9:00.", StringComparison.Ordinal));
        T.True("a toast says what it means", vm.Toast is { } t && t.Contains("suspended", StringComparison.Ordinal));
    }

    public static void ResumingAsksFirstInAPanelInsideTheWindow()
    {
        var (vm, services, _) = Window(Off());
        Task toggle = vm.ToggleAgentsAsync();
        T.True("a panel, not a window", vm.ActivePanel is ChoicePanelViewModel && vm.HasPanel);
        var panel = (ChoicePanelViewModel)vm.ActivePanel!;
        T.Eq("title", "Let AI agents change your schedules again?", panel.Title);
        T.Eq("two buttons", 2, panel.Buttons.Count);
        T.Eq("cancel first", "Cancel", panel.Buttons[0].Label);
        T.True("resume is the primary one", panel.Buttons[1].Label == "Resume" && panel.Buttons[1].IsPrimary);
        T.True("nothing written yet", !services.Calls.Contains("resume"));
        panel.Buttons[1].Command.Execute(null);
        toggle.GetAwaiter().GetResult();
        T.True("resume written", services.Calls.Contains("resume"));
        T.True("panel gone", vm.ActivePanel is null);
        T.Eq("allowed again", PillKind.Allowed, vm.PillKind);
        T.True("a resume by the app shows no note", !vm.HasAgentNote);
    }

    public static void CancellingOrEscapeLeavesAgentsSuspended()
    {
        var (vm, services, _) = Window(Off());
        Task toggle = vm.ToggleAgentsAsync();
        ((ChoicePanelViewModel)vm.ActivePanel!).Buttons[0].Command.Execute(null);
        toggle.GetAwaiter().GetResult();
        T.True("cancel: not resumed", !services.Calls.Contains("resume") && vm.PillKind == PillKind.Suspended);

        Task again = vm.ToggleAgentsAsync();
        T.True("Escape closes the panel", vm.HandleEscape() && vm.ActivePanel is null);
        again.GetAwaiter().GetResult();
        T.True("Escape: not resumed", !services.Calls.Contains("resume"));
        T.True("with no panel, Escape does what it did before", !vm.HandleEscape());

        Task third = vm.ToggleAgentsAsync();
        vm.ClosePanelCommand.Execute(null);
        third.GetAwaiter().GetResult();
        T.True("a click outside is the same", !services.Calls.Contains("resume"));
    }

    public static void ResumingAnUnreadableSwitchAlsoAsks()
    {
        var (vm, services, _) = Window(AgentPolicy.Unreadable("x"));
        Task toggle = vm.ToggleAgentsAsync();
        var panel = (ChoicePanelViewModel)vm.ActivePanel!;
        T.True("the message says what resuming does", panel.Message.Contains("writes the setting again", StringComparison.Ordinal));
        panel.Buttons[1].Command.Execute(null);
        toggle.GetAwaiter().GetResult();
        T.True("written", services.Calls.Contains("resume"));
        T.Eq("readable and allowed now", PillKind.Allowed, vm.PillKind);
    }

    public static void AFailedSwitchWriteSaysSoAndChangesNothing()
    {
        var (vm, services, _) = Window();
        services.SwitchFails = new UnauthorizedAccessException("denied");
        vm.ToggleAgentsAsync().GetAwaiter().GetResult();
        T.True("still allowed", vm.PillKind == PillKind.Allowed);
        T.True("a toast says why", vm.Toast is { } t && t.Contains("Nothing was changed", StringComparison.Ordinal));
    }

    public static void ASecondQuestionReplacesTheFirst()
    {
        var (vm, _, _) = Window();
        Task<int> first = vm.ShowChoiceAsync("One", "first", [new PanelChoice("Ok")]);
        Task<int> second = vm.ShowChoiceAsync("Two", "second", [new PanelChoice("Ok")]);
        T.Eq("the first counts as dismissed", -1, first.GetAwaiter().GetResult());
        T.Eq("the second is on screen", "Two", ((ChoicePanelViewModel)vm.ActivePanel!).Title);
        ((ChoicePanelViewModel)vm.ActivePanel!).Buttons[0].Command.Execute(null);
        T.Eq("and answers its own button", 0, second.GetAwaiter().GetResult());
    }

    // ---- the Orange note ----

    public static void AnAgentResumeShowsTheNoteUntilTheMenuIsOpened()
    {
        var (vm, services, _) = Window(On("agent:Claude Code", "Review is done."));
        T.True("note", vm.HasAgentNote);
        T.Eq("text", "AI agents were turned back on by AI agent Claude Code at 8:47", vm.AgentNoteText);
        T.True("tooltip has the hint", vm.AgentNoteTooltip.Contains("check your agent's tool permissions", StringComparison.Ordinal));
        T.Eq("the pill is plain green", PillKind.Allowed, vm.PillKind);

        vm.AcknowledgeAgentNote();     // what opening the ⋯ menu does
        T.True("gone", !vm.HasAgentNote);
        T.True("remembered", WaitFor(() => services.Settings.AgentNoteSeenAt == Today(8, 47)));

        vm.RefreshPolicyAsync().GetAwaiter().GetResult();
        T.True("a refresh does not bring it back", !vm.HasAgentNote);

        services.Policy = On("agent:Claude Code", minute: 55);
        vm.RefreshPolicyAsync().GetAwaiter().GetResult();
        T.True("a newer resume does", vm.HasAgentNote && vm.AgentNoteText.EndsWith("at 8:55", StringComparison.Ordinal));
        vm.AcknowledgeNoteCommand.Execute(null);
        T.True("'Got it' acknowledges too", !vm.HasAgentNote);
    }

    public static void AnAlreadyAcknowledgedNoteStaysGoneAtTheNextStart()
    {
        var services = new ScriptedServices { Policy = On("agent:Claude Code") };
        services.Settings.AgentNoteSeenAt = Today(8, 50);
        var dialogs = new RecordingDialogs();
        var vm = new MainViewModel(services, dialogs, dialogs);
        vm.Start();
        vm.InitialLoad.GetAwaiter().GetResult();
        T.True("no note", !vm.HasAgentNote);
    }

    public static void ASwitchClearedOutsideCadenceShowsItsOwnNote()
    {
        var outside = new AgentPolicy(false, Today(8, 12), "app", 5, 20, null, ChangedBy: "app", ChangedAt: Today(8, 12));
        var (vm, _, _) = Window(outside);
        T.True("note", vm.HasAgentNote);
        T.Eq("text", "AI agents were turned back on outside Cadence", vm.AgentNoteText);
        T.True("tooltip", vm.AgentNoteTooltip.StartsWith("Something running as you", StringComparison.Ordinal));
    }

    public static void ResumeByTheAppOrCliShowsNoNote()
    {
        var (vm, _, _) = Window(On("cli"));
        T.True("no note", !vm.HasAgentNote);
        var (vm2, _, _) = Window(On("app"));
        T.True("no note either", !vm2.HasAgentNote);
    }

    // ---- Agent activity ----

    static AuditEntry Entry(DateTimeOffset at, string tool, string outcome, string client = "Claude Code", string? job = null, string? code = null, string detail = "") =>
        new(at, "s1", client, null, tool, null, job, outcome, code, detail);

    public static void AgentActivityListsTheNewestFirst()
    {
        var (vm, services, _) = Window();
        services.AgentLog.Add(Entry(Today(7, 0), "cadence_save_job", "ok", job: "Sync notes", detail: "Created the job"));
        services.AgentLog.Add(Entry(Today(7, 5), "cadence_save_job", "refused", job: "Sync notes", code: "schedule_too_frequent", detail: "Too often."));
        vm.OpenActivityCommand.Execute(null);
        T.True("the panel is open", WaitFor(() => vm.ActivePanel is AgentActivityViewModel { HasRows: true }));
        var panel = (AgentActivityViewModel)vm.ActivePanel!;
        T.Eq("two rows", 2, panel.Rows.Count);
        T.Eq("newest first", "Refused: schedule too frequent", panel.Rows[0].Result);
        T.Eq("then the older one", "Done", panel.Rows[1].Result);
        T.Eq("path", @"C:\Test\agents\audit.jsonl", panel.Path);
        T.Eq("header", "2 entries, newest first", panel.Header);
        T.True("Escape closes it", vm.HandleEscape() && vm.ActivePanel is null);
    }

    public static void AgentActivityWithNoLogSaysSo()
    {
        var (vm, _, _) = Window();
        vm.OpenActivityAsync().GetAwaiter().GetResult();
        var panel = (AgentActivityViewModel)vm.ActivePanel!;
        T.True("empty", panel.IsEmpty && !panel.HasRows);
        T.True("plain words", panel.EmptyText.StartsWith("No agent activity yet.", StringComparison.Ordinal));
    }

    public static void OpenLogFolderSaysSoWhenThereIsNone()
    {
        var services = new ScriptedServices();
        var panel = new AgentActivityViewModel(services, () => { }, _ => false);
        panel.OpenFolderCommand.Execute(null);
        T.True("a message instead of nothing", panel.HasMessage);
        string? opened = null;
        var ok = new AgentActivityViewModel(services, () => { }, p => { opened = p; return true; });
        ok.OpenFolderCommand.Execute(null);
        T.Eq("opens the log's folder", @"C:\Test\agents", opened);
        T.True("no message", !ok.HasMessage);
    }

    public static void AgentActivityIsBounded()
    {
        var (vm, services, _) = Window();
        for (int i = 0; i < 1000; i++) services.AgentLog.Add(Entry(Today(0, 0).AddSeconds(i), "cadence_list_jobs", "ok"));
        vm.OpenActivityAsync().GetAwaiter().GetResult();
        var panel = (AgentActivityViewModel)vm.ActivePanel!;
        T.Eq("at most the cap", ActivityText.MaxRows, panel.Rows.Count);
        T.True("says it shows the newest", panel.Header.StartsWith("Showing the newest 200", StringComparison.Ordinal));
        T.Eq("newest first", "Today 0:16", panel.Rows[0].When);
    }

    // ---- the limits ----

    public static void LimitsPanelValidatesAndSaves()
    {
        var (vm, services, _) = Window();
        vm.OpenLimitsCommand.Execute(null);
        var panel = (AgentLimitsViewModel)vm.ActivePanel!;
        T.Eq("shows the current minimum", "5", panel.MinInterval);
        T.Eq("shows the current maximum", "20", panel.MaxJobs);
        panel.MinInterval = "0";
        panel.SaveCommand.Execute(null);
        T.True("an invalid minimum is explained", panel.HasError && panel.Error!.Contains("1 to 1440", StringComparison.Ordinal));
        T.True("nothing written", !services.Calls.Any(c => c.StartsWith("limits", StringComparison.Ordinal)));
        panel.MinInterval = "15";
        T.True("typing clears the error", !panel.HasError);
        panel.MaxJobs = "501";
        panel.SaveCommand.Execute(null);
        T.True("an invalid maximum is explained", panel.HasError && panel.Error!.Contains("0 to 500", StringComparison.Ordinal));
        panel.MaxJobs = "7";
        panel.SaveCommand.Execute(null);
        T.True("saved", WaitFor(() => services.Calls.Contains("limits 15 7")));
        T.True("the panel closed", WaitFor(() => vm.ActivePanel is null));
        T.Eq("the policy changed", 15, services.Policy.MinIntervalMinutes);
        T.True("the CLI is named", panel.CliHint.Contains("--agents config", StringComparison.Ordinal));
    }

    public static void LimitsValidationWords()
    {
        T.True("ok", AgentLimitsText.Validate("5", "20", out int a, out int b) is null && a == 5 && b == 20);
        T.True("zero jobs is allowed", AgentLimitsText.Validate("5", "0", out _, out int zero) is null && zero == 0);
        T.True("1440 is allowed", AgentLimitsText.Validate("1440", "1", out _, out _) is null);
        T.True("1441 is not", AgentLimitsText.Validate("1441", "1", out _, out _) is not null);
        T.True("text is not", AgentLimitsText.Validate("abc", "1", out _, out _) is not null);
        T.True("negative is not", AgentLimitsText.Validate("5", "-1", out _, out _) is not null);
        T.True("blank is not", AgentLimitsText.Validate("", "", out _, out _) is not null);
        T.True("spaces are trimmed", AgentLimitsText.Validate(" 5 ", " 7 ", out int c, out _) is null && c == 5);
    }

    // ---- the editor: attribution, the lock, the stale save ----

    static ManagedEntry DemoEntry(string job) =>
        new DemoServices(DemoSet.Showcase).LoadManagedAsync().GetAwaiter().GetResult().Entries.First(e => e.Task.Observed.Name == job);

    static (EditorViewModel Vm, ScriptedServices Services, RecordingDialogs Dialogs, RecordingHost Host) Editor(string job)
    {
        var services = new ScriptedServices();
        ManagedEntry entry = DemoEntry(job);
        services.Entries.Add(entry);
        var dialogs = new RecordingDialogs();
        var host = new RecordingHost();
        return (EditorViewModel.ForEntry(services, dialogs, host.Value, entry), services, dialogs, host);
    }

    public static void TheEditorShowsWhoCreatedAndChangedTheJob()
    {
        var (vm, _, _, _) = Editor("Sync notes");
        T.Eq("agent job", "Created 1 Oct 2026 by Claude Code \u00B7 Updated 2 h ago by Claude Code", vm.Notice.MetaLine);
        T.True("shown", vm.Notice.HasMetaLine);
        var (v0, _, _, _) = Editor("Nightly backup");
        T.Eq("a v0.1.0 job", "Created 1 Sep 2026 by unknown \u00B7 Updated 12 Oct by unknown", v0.Notice.MetaLine);
        var (drifted, _, _, _) = Editor("Weekly report");
        T.True("drift is said", drifted.Notice.MetaLine!.EndsWith("changed in Task Scheduler since", StringComparison.Ordinal));
        var services = new ScriptedServices();
        var fresh = EditorViewModel.ForTemplate(services, new RecordingDialogs(), new RecordingHost().Value, Cadence.Core.Text.Templates.All.First(t => t.Id == "blank"));
        T.True("a new schedule has none", !fresh.Notice.HasMetaLine && fresh.Notice.MetaLine is null);
    }

    public static void TheLockToggleSavesTheJobAndShowsInTheLine()
    {
        var (vm, services, _, host) = Editor("Sync notes");
        T.True("can toggle", vm.Notice.CanToggleAgentLock);
        T.True("not locked", !vm.Notice.IsAgentLocked);
        services.AfterLock = (task, locked) => services.Entries[0] = services.Entries[0] with { Task = task with { Job = task.Job! with { AgentLock = locked } } };
        vm.Notice.ToggleAgentLockAsync().GetAwaiter().GetResult();
        T.True("the app asked for the lock", services.Calls.Contains("lock Sync notes"));
        T.True("locked", vm.Notice.IsAgentLocked);
        T.True("the line says so", vm.Notice.MetaLine!.EndsWith("Locked for AI agents", StringComparison.Ordinal));
        T.True("a message", vm.Status!.StartsWith("Locked.", StringComparison.Ordinal));
        T.True("the list was refreshed", host.Refreshed >= 1);
        T.True("the form is not dirty because of it", !vm.IsDirty);
        vm.Notice.ToggleAgentLockAsync().GetAwaiter().GetResult();
        T.True("unlocked again", !vm.Notice.IsAgentLocked && services.Calls.Contains("unlock Sync notes"));
        T.True("the line is back", !vm.Notice.MetaLine!.Contains("Locked", StringComparison.Ordinal));
    }

    public static void LockingKeepsMyUnsavedEdits()
    {
        var (vm, services, _, _) = Editor("Sync notes");
        vm.Name = "Sync my notes";
        T.True("dirty", vm.IsDirty);
        vm.Notice.ToggleAgentLockAsync().GetAwaiter().GetResult();
        T.Eq("the edit is still there", "Sync my notes", vm.Name);
        T.True("still dirty (the lock was saved on its own)", vm.IsDirty);
        T.True("no save of the draft", !services.Calls.Any(c => c.StartsWith("save", StringComparison.Ordinal)));
    }

    public static void AFailedLockSaysWhy()
    {
        var (vm, services, _, _) = Editor("Sync notes");
        services.LockResult = ScriptedServices.Fail("This job was changed 1 minute ago by Claude Code after you read it.");
        vm.Notice.ToggleAgentLockAsync().GetAwaiter().GetResult();
        T.True("not locked", !vm.Notice.IsAgentLocked);
        T.True("the reason is shown", vm.StatusIsError && vm.Status!.Contains("Claude Code", StringComparison.Ordinal));
    }

    public static void TheLockIsOffWhereASaveWouldBeWrong()
    {
        var (drifted, _, _, _) = Editor("Weekly report");
        T.True("a changed-outside job", !drifted.Notice.CanToggleAgentLock);
        var (copy, _, _, _) = Editor("Stock ingest (Dad) (copy)");
        T.True("a copy", !copy.Notice.CanToggleAgentLock);
        var services = new ScriptedServices();
        var fresh = EditorViewModel.ForTemplate(services, new RecordingDialogs(), new RecordingHost().Value, Cadence.Core.Text.Templates.All.First(t => t.Id == "blank"));
        T.True("an unsaved schedule", !fresh.Notice.CanToggleAgentLock);
        fresh.Notice.ToggleAgentLockAsync().GetAwaiter().GetResult();
        T.True("and the command does nothing", !services.Calls.Any(c => c.Contains("lock", StringComparison.Ordinal)));
    }

    static ApplyResult Stale() => new(false, "This job was changed 2 minutes ago by Claude Code after you read it. Read it again and re-apply your change.", ValueList<FieldDiff>.Empty, ApplyFailure.Stale);

    /// <summary>The live job as an agent changed it 2 minutes ago (a different program), as the next load will report it.</summary>
    static void AgentChangedTheJob(ScriptedServices services, Func<JobDefinition, JobDefinition> change)
    {
        ManagedEntry e = services.Entries[0];
        JobDefinition job = change(e.Task.Job!);
        var meta = e.Task.Metadata! with { Job = job, Updated = DemoServices.FixedNow.AddMinutes(-2), UpdatedBy = "agent:Claude Code" };
        services.Entries[0] = e with { Task = e.Task with { Job = job, Metadata = meta } };
    }

    public static void StaleSaveAsksInAPanelAndReloadPutsMyChangeOnTop()
    {
        var (vm, services, dialogs, host) = Editor("Sync notes");
        vm.Name = "Sync notes 2";
        AgentChangedTheJob(services, j => j with { Run = new RunSpec { Exe = @"C:\Tools\agent-picked.exe" } });
        services.SaveResult = Stale();
        dialogs.Choice = 1;
        vm.SaveAsync().GetAwaiter().GetResult();

        T.Eq("one question", 1, dialogs.Chosen.Count);
        T.Eq("title", AgentText.StaleTitle, dialogs.Chosen[0].Title);
        T.True("it names who and when", dialogs.Chosen[0].Message.StartsWith("This schedule was changed 2 min ago by Claude Code while you were editing.", StringComparison.Ordinal));
        T.Eq("the two buttons", "Keep editing|Reload and re-apply my change", string.Join("|", dialogs.Chosen[0].Buttons));
        T.Eq("the editor was replaced", 1, host.Replaced.Count);
        EditorViewModel next = host.Replaced[0];
        T.Eq("my change is on top", "Sync notes 2", next.Name);
        T.Eq("their change is kept", @"C:\Tools\agent-picked.exe", next.Program.Exe);
        T.True("it is dirty (there is something to save)", next.IsDirty);
        T.True("it says what happened", next.Status!.StartsWith("Reloaded. Your change is applied on top of Claude Code's version.", StringComparison.Ordinal) && !next.StatusIsError);
        T.True("the new editor can save", next.CanSave);
        T.Eq("the old one saved once", 1, services.Calls.Count(c => c.StartsWith("save", StringComparison.Ordinal)));
    }

    public static void BothChangedTheSameThingSaysMineWins()
    {
        var (vm, services, dialogs, host) = Editor("Sync notes");
        vm.Name = "My name";
        AgentChangedTheJob(services, j => j with { Name = "Their name" });
        services.SaveResult = Stale();
        dialogs.Choice = 1;
        vm.SaveAsync().GetAwaiter().GetResult();
        EditorViewModel next = host.Replaced[0];
        T.Eq("mine wins", "My name", next.Name);
        T.True("and it says so", next.Status!.Contains("You both changed the name, so your version of that is shown.", StringComparison.Ordinal), next.Status);
    }

    public static void KeepEditingChangesNothingAndSaveRefusesAgain()
    {
        var (vm, services, dialogs, host) = Editor("Sync notes");
        vm.Name = "Sync notes 2";
        AgentChangedTheJob(services, j => j with { Run = new RunSpec { Exe = @"C:\Tools\agent-picked.exe" } });
        services.SaveResult = Stale();
        dialogs.Choice = 0;
        vm.SaveAsync().GetAwaiter().GetResult();
        T.True("not replaced", host.Replaced.Count == 0);
        T.Eq("my edit stays", "Sync notes 2", vm.Name);
        T.True("an error line says who changed it", vm.StatusIsError && vm.Status!.Contains("by Claude Code", StringComparison.Ordinal));
        vm.SaveAsync().GetAwaiter().GetResult();
        T.Eq("the next Save is refused again, with the same question", 2, dialogs.Chosen.Count);
        T.Eq("it did try again", 2, services.Calls.Count(c => c.StartsWith("save", StringComparison.Ordinal)));
        dialogs.Choice = -1;
        vm.SaveAsync().GetAwaiter().GetResult();
        T.True("dismissing is keep editing", host.Replaced.Count == 0 && vm.StatusIsError);
    }

    public static void AStaleSaveWhoseWriterIsTaskSchedulerSaysSo()
    {
        var (vm, services, dialogs, _) = Editor("Sync notes");
        vm.Name = "Sync notes 2";
        ManagedEntry e = services.Entries[0];
        services.Entries[0] = e with { Task = e.Task with { Sync = SyncStatus.Drifted } };
        services.SaveResult = Stale();
        dialogs.Choice = 0;
        vm.SaveAsync().GetAwaiter().GetResult();
        T.Eq("message", "This schedule was changed in Task Scheduler while you were editing.", dialogs.Chosen[0].Message.Split('\n')[0]);
    }

    public static void OtherSaveFailuresStayInlineWithNoPanel()
    {
        var (vm, services, dialogs, _) = Editor("Sync notes");
        vm.Name = "Sync notes 2";
        services.SaveResult = new ApplyResult(false, "A task named that already exists.", ValueList<FieldDiff>.Empty, ApplyFailure.Refused);
        vm.SaveAsync().GetAwaiter().GetResult();
        T.True("no question", dialogs.Chosen.Count == 0);
        T.True("the old inline error", vm.StatusIsError && vm.Status == "A task named that already exists.");
    }

    // ---- the list row ----

    public static void ListRowsCarryTheUpdatedLineAndTheLock()
    {
        var dialogs = new RecordingDialogs();
        var vm = new MainViewModel(new DemoServices(DemoSet.Normal), dialogs, dialogs);
        vm.Start();
        vm.InitialLoad.GetAwaiter().GetResult();
        ScheduleRowViewModel Row(string n) => vm.List.Rows.First(r => r.Name == n);
        T.Eq("agent row", "Updated 2 h ago by Claude Code", Row("Sync notes").Updated);
        T.Eq("cli row", "Updated 3 d ago from the command line", Row("Weekly report").Updated);
        T.Eq("unknown row", "Updated 12 Oct by unknown", Row("Nightly backup").Updated);
        T.True("a locked row", Row("Photo backup").IsLocked && Row("Photo backup").LockedTooltip == "Locked: AI agents can't change this");
        T.True("an unlocked row", !Row("Sync notes").IsLocked);
        T.True("the tooltip has the full text", Row("Photo backup").UpdatedTooltip!.Contains("Locked: AI agents can't change this", StringComparison.Ordinal));
        T.True("every managed row has a line", vm.List.Rows.All(r => r.HasUpdated));
    }
}
