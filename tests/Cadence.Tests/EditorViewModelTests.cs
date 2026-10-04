using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.Core.Execution;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>The schedule editor's view model (PLAN Phase 13): form state, Simple/Cron sync, validation, dirty tracking, Save/Delete rules, preview and runs.</summary>
public static class EditorViewModelTests
{
    sealed class FakeDialogs : IDialogs
    {
        public bool Answer { get; set; }
        public int Asked { get; private set; }
        public string? Picked { get; set; }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger) { Asked++; return Task.FromResult(Answer); }
        public Task<bool> ConfirmTypedAsync(string title, string message, string word, string confirmText) => Task.FromResult(Answer);
        public Task InfoAsync(string title, string message) => Task.CompletedTask;
        public void ShowAbout() { }
        public string? PickProgram() => Picked;
    }

    sealed class Host
    {
        public int Closed, Refreshed;
        public readonly List<string> Toasts = [];
        public readonly List<string> Copied = [];
        public EditorHost Value => new(() => Closed++, () => { Refreshed++; return Task.CompletedTask; }, Toasts.Add, _ => true, Copied.Add);
    }

    static (EditorViewModel Vm, DemoServices Services, FakeDialogs Dialogs, Host Host) New(string templateId, DemoSet set = DemoSet.Normal)
    {
        var services = new DemoServices(set);
        var dialogs = new FakeDialogs();
        var host = new Host();
        return (EditorViewModel.ForTemplate(services, dialogs, host.Value, Templates.All.First(t => t.Id == templateId)), services, dialogs, host);
    }

    static (EditorViewModel Vm, DemoServices Services, FakeDialogs Dialogs, Host Host) Existing(string jobName, DemoSet set = DemoSet.Normal)
    {
        var services = new DemoServices(set);
        var dialogs = new FakeDialogs();
        var host = new Host();
        ManagedEntry entry = services.LoadManagedAsync().GetAwaiter().GetResult().Entries.First(e => e.Task.Job?.Name == jobName);
        return (EditorViewModel.ForEntry(services, dialogs, host.Value, entry), services, dialogs, host);
    }

    public static void NewJobFromTemplateIsPrefilledWithAnEmptyDropZone()
    {
        var (vm, _, _, _) = New("good-morning");
        T.True("is new", vm.IsNew && !vm.IsExisting && !vm.HasRunsPanel);
        T.Eq("name", "Good morning", vm.Name);
        T.True("no program yet (the drop zone shows)", !vm.Program.HasProgram);
        T.Eq("sentence", "Every weekday at 8:00", vm.Schedule.Sentence);
        T.Eq("eight runs", 8, vm.Schedule.PreviewRuns.Count);
        T.Eq("Simple view", HumanFrequency.Weekdays, vm.Schedule.Frequency);
        T.True("simple mode", vm.Schedule.IsSimpleMode && vm.Schedule.ShowSimpleControls);
        T.Eq("one time", 1, vm.Schedule.Times.Count);
        T.Eq("time text", "8:00", vm.Schedule.Times[0].Text);
        T.Eq("cron text", "0 8 * * 1-5", vm.Schedule.CronText);
        T.True("cannot save without a program", !vm.CanSave && vm.BlockingProblem == "Choose a program to run, then save.");
        T.True("the empty program is not shown as an error", vm.ProgramNote is null);
        T.True("enabled", vm.Enabled);
    }

    public static void TemplateSkipHolidaysArePrefilled()
    {
        var (vm, _, _, _) = New("weekdays-6pm");
        T.Eq("nyse", HolidayCalendar.Nyse, vm.Schedule.SkipHolidays);
        T.True("tooltip lists holidays", vm.Schedule.HolidayTooltip.Contains("Thanksgiving Day", StringComparison.Ordinal));
        T.Eq("sentence", "Every weekday at 18:00, except NYSE market holidays", vm.Schedule.Sentence);
    }

    public static void ChoosingAProgramEnablesSaveAndDefaultsTheWorkingFolder()
    {
        var (vm, _, dialogs, _) = New("good-morning");
        dialogs.Picked = @"C:\Tools\backup.exe";
        vm.Program.BrowseCommand.Execute(null);
        T.True("has program", vm.Program.HasProgram);
        T.Eq("file name", "backup.exe", vm.Program.ProgramName);
        T.Eq("folder", @"C:\Tools", vm.Program.ProgramFolder);
        T.True("can save", vm.CanSave && vm.BlockingProblem is null);
        T.Eq("job exe", @"C:\Tools\backup.exe", vm.BuildJob().Run.Exe);
        T.Eq("working folder defaults to the program's folder", @"C:\Tools", vm.BuildJob().Run.WorkingDirectory);
        T.True("placeholder says automatic", vm.Program.WorkingDirectoryPlaceholder.Contains("(automatic)", StringComparison.Ordinal));
        vm.Program.WorkingDirectory = @"D:\Data";
        T.Eq("an explicit folder wins", @"D:\Data", vm.BuildJob().Run.WorkingDirectory);
        T.Eq("template name stays", "Good morning", vm.Name);
    }

    public static void ScriptsAreAcceptedAndUnknownFilesAreRefusedFriendly()
    {
        var (vm, _, _, _) = New("good-morning");
        T.True("unknown refused", !vm.Program.UseFile(@"C:\s\notes.txt"));
        T.Eq("unknown message", DropInterpreter.NotRunnable, vm.Program.ProgramMessage);
        T.True("shown as an error note", vm.ProgramNote is { IsError: true } && vm.Program.HasProgramMessage);
        T.True("still no program", !vm.Program.HasProgram);
        T.True("script accepted and clears the message", vm.Program.UseFile(@"C:\s\morning.ps1") && vm.Program.ProgramMessage is null && !vm.Program.HasProgramMessage);
        T.True("powershell runs it", vm.Program.Exe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) && vm.Program.Args.EndsWith(@"-File ""C:\s\morning.ps1""", StringComparison.Ordinal));
        T.True("exe accepted", vm.Program.UseFile(@"C:\Tools\x.exe") && vm.Program.Exe == @"C:\Tools\x.exe" && vm.Program.Args == "");
    }

    public static void TheInterpreterSeamIsUsedForEveryFile()
    {
        var (vm, _, _, _) = New("blank");
        vm.Program.ProgramInterpreter = new Fake();
        T.True("accepted", vm.Program.UseFile(@"C:\s\ingest.py"));
        T.Eq("exe", @"C:\Windows\py.exe", vm.Program.Exe);
        T.Eq("args", @"-u ""C:\s\ingest.py""", vm.Program.Args);
        T.True("utf8", vm.Program.Utf8Output);
        T.Eq("env row", 1, vm.Program.Env.Count);
        T.Eq("working folder shown as automatic (script folder)", @"C:\s", vm.BuildJob().Run.WorkingDirectory);
        T.Eq("blank template takes the suggested name", "Ingest", vm.Name);
    }

    sealed class Fake : IProgramInterpreter
    {
        public ProgramChoice? Interpret(string path, out string? rejectReason)
        {
            rejectReason = null;
            return new ProgramChoice(@"C:\Windows\py.exe", @"-u """ + path + @"""", null, OutputEncoding.Utf8,
                ValueList.Of(new KeyValuePair<string, string>("PYTHONIOENCODING", "utf-8")), path, "Ingest", ValueList<string>.Empty);
        }
    }

    public static void SimpleChangesWriteCronAndBack()
    {
        var (vm, _, _, _) = New("good-morning");
        vm.Schedule.Frequency = HumanFrequency.EveryNMinutes;
        T.Eq("every 15 minutes", "*/15 * * * *", vm.Schedule.CronText);
        T.Eq("sentence", "Every 15 minutes", vm.Schedule.Sentence);
        vm.Schedule.Interval = 30;
        T.Eq("every 30", "*/30 * * * *", vm.Schedule.CronText);
        vm.Schedule.LimitHours = true;
        T.Eq("window", "*/30 9-17 * * *", vm.Schedule.CronText);
        vm.Schedule.IsCronMode = true;
        T.True("cron mode", vm.Schedule.IsCronMode && !vm.Schedule.IsSimpleMode && !vm.Schedule.ShowSimpleControls);
        vm.Schedule.CronText = "0 6 * * 1,3,5";
        vm.Schedule.IsSimpleMode = true;
        T.Eq("specific days", HumanFrequency.SpecificDays, vm.Schedule.Frequency);
        T.Eq("time", "6:00", vm.Schedule.Times[0].Text);
        T.True("monday, wednesday, friday chips", vm.Schedule.Days.Where(c => c.IsOn).Select(c => c.Label).SequenceEqual(["Mon", "Wed", "Fri"]));
        vm.Schedule.Days[1].IsOn = true;
        T.Eq("Tuesday added", "0 6 * * 1,2,3,5", vm.Schedule.CronText);
    }

    public static void CronOnlyScheduleShowsReadOnlySimple()
    {
        var (vm, _, _, _) = New("blank");
        vm.Schedule.IsCronMode = true;
        vm.Schedule.CronText = "0,30 9 * * *";
        vm.Schedule.IsSimpleMode = true;
        T.True("read-only", vm.Schedule.ShowSimpleReadOnly && !vm.Schedule.ShowSimpleControls);
        T.Eq("reason", ScheduleEditorState.CronOnlyMessage, vm.Schedule.SimpleReadOnlyReason);
        T.Eq("sentence still shown", "Every day at 9:00 and 9:30", vm.Schedule.Sentence);
        vm.Schedule.EditInCronCommand.Execute(null);
        T.True("back in cron, text intact", vm.Schedule.IsCronMode && vm.Schedule.CronText == "0,30 9 * * *");
    }

    public static void BadTimeTextIsReportedNotDropped()
    {
        var (vm, _, _, _) = New("good-morning");
        vm.Program.UseFile(@"C:\Tools\x.exe");
        vm.Schedule.Times[0].Text = "25:00";
        T.True("blocked", !vm.CanSave && vm.BlockingProblem!.Contains("isn't a time", StringComparison.Ordinal));
        T.True("diagnostic shown", vm.Schedule.Diagnostics.Any(d => d.IsError));
        vm.Schedule.Times[0].Text = "8";
        T.Eq("canonical", "8:00", vm.Schedule.Times[0].Text);
        T.True("fixed", vm.CanSave);
        vm.Schedule.AddTimeCommand.Execute(null);
        T.Eq("second time follows", "9:00", vm.Schedule.Times[1].Text);
        T.Eq("two times in the cron", "0 8,9 * * 1-5", vm.Schedule.CronText);
        vm.Schedule.Times[1].RemoveCommand.Execute(null);
        T.Eq("one again", "0 8 * * 1-5", vm.Schedule.CronText);
    }

    public static void InvalidCronBlocksSaveWithAPreciseMessage()
    {
        var (vm, _, _, _) = New("good-morning");
        vm.Program.UseFile(@"C:\Tools\x.exe");
        vm.Schedule.IsCronMode = true;
        vm.Schedule.CronText = "0 9 15W * *";
        T.True("cannot save", !vm.CanSave);
        T.True("message names the text", vm.BlockingProblem!.Contains("15W", StringComparison.Ordinal));
        T.True("footer shows it as an error", vm.FooterIsError && vm.FooterText == vm.BlockingProblem);
        T.True("preview invalid", !vm.Schedule.PreviewIsValid && vm.Schedule.PreviewHeadline.StartsWith("Fix", StringComparison.Ordinal));
        vm.Schedule.CronText = "@reboot";
        T.True("alternative offered", vm.Schedule.HasAlternative && vm.Schedule.AlternativeText == "Use @logon instead");
        vm.Schedule.UseAlternativeCommand.Execute(null);
        T.True("applied explicitly", vm.Schedule.CronText == "@logon" && vm.CanSave && vm.Schedule.ShowAtSignIn);
    }

    public static void ValidationMirrorsTheMetadataRules()
    {
        var (vm, _, _, _) = New("good-morning");
        vm.Program.UseFile(@"C:\Tools\x.exe");
        vm.Name = "   ";
        T.True("empty name", vm.NameNote is { IsError: true } && !vm.CanSave);
        vm.Name = new string('x', 101);
        T.True("long name", vm.NameNote is { IsError: true } && !vm.CanSave);
        vm.Name = "Fine";
        T.True("name ok", vm.NameNote is null && vm.CanSave);

        vm.Program.AddEnvCommand.Execute(null);
        vm.Program.Env[0].Name = "A=B";
        T.True("env name with =", vm.EnvNote is { IsError: true } && !vm.CanSave);
        vm.Program.Env[0].Name = "";
        vm.Program.Env[0].Value = "v";
        T.True("env name empty", vm.EnvNote is { IsError: true } && !vm.CanSave);
        vm.Program.Env[0].Name = "A";
        vm.Program.AddEnvCommand.Execute(null);
        vm.Program.Env[1].Name = "a";
        T.True("duplicate (case-insensitive)", vm.EnvNote is { IsError: true } && !vm.CanSave);
        vm.Program.Env[1].RemoveCommand.Execute(null);
        T.True("env ok", vm.EnvNote is null && vm.CanSave);
        vm.Program.AddEnvCommand.Execute(null);
        T.True("a blank row is ignored", vm.CanSave && vm.BuildJob().Run.Env.Count == 1);

        vm.Program.SelectedTimeout = ProgramSectionViewModel.TimeoutChoices.First(c => c.IsCustom);
        T.True("custom empty", vm.TimeoutNote is { IsError: true } && !vm.CanSave);
        vm.Program.CustomTimeoutMinutes = "abc";
        T.True("custom text", vm.TimeoutNote is { IsError: true });
        vm.Program.CustomTimeoutMinutes = "40000";
        T.True("over 24 days", vm.TimeoutNote is { IsError: true });
        vm.Program.CustomTimeoutMinutes = "90";
        T.True("90 minutes ok", vm.TimeoutNote is null && vm.BuildJob().Run.Timeout == TimeSpan.FromMinutes(90) && vm.CanSave);
        vm.Program.SelectedTimeout = ProgramSectionViewModel.TimeoutChoices[1];
        T.Eq("5 minutes", TimeSpan.FromMinutes(5), vm.BuildJob().Run.Timeout);

        vm.Program.Exe = "tools\\run.exe";
        T.True("relative program warns but does not block", vm.ProgramNote is { IsError: false } && vm.CanSave);
        vm.Program.Exe = "node.exe";
        T.True("a bare name is fine", vm.ProgramNote is null);
        vm.Program.WorkingDirectory = "data";
        T.True("relative folder warns", vm.FolderNote is { IsError: false } && vm.CanSave);
    }

    public static void ValidationRulesTable()
    {
        JobDefinition Job(Func<RunSpec, RunSpec> f, string name = "n") => new()
        {
            Id = "abcd1234", Name = name,
            Schedule = new ScheduleSpec(ValueList.Of("0 9 * * *"), HolidayCalendar.None, new DateOnly(2026, 1, 1)),
            Run = f(new RunSpec { Exe = @"C:\a.exe" }),
        };
        T.Eq("clean", 0, EditorValidation.Validate(Job(r => r)).Count);
        T.True("nul in args", EditorValidation.Validate(Job(r => r with { Args = "a\0b" })).Any(p => p.Field == "Args" && p.IsError));
        T.True("nul in exe", EditorValidation.Validate(Job(r => r with { Exe = "a\0b" })).Any(p => p.Field == "Program" && p.IsError));
        T.True("timeout 25 days", EditorValidation.Validate(Job(r => r with { Timeout = TimeSpan.FromDays(25) })).Any(p => p.Field == "Timeout"));
        T.True("timeout 24 days ok", !EditorValidation.Validate(Job(r => r with { Timeout = TimeSpan.FromDays(24) })).Any(p => p.Field == "Timeout"));
        T.True("relative", EditorValidation.IsRelative(@"a\b") && EditorValidation.IsRelative("C:x") && !EditorValidation.IsRelative(@"C:\x") && !EditorValidation.IsRelative(@"\\srv\x") && !EditorValidation.IsRelative("%SystemRoot%\\x"));
        T.True("relative with folder", EditorValidation.IsRelativeWithFolder(@".\x.exe") && !EditorValidation.IsRelativeWithFolder("x.exe"));
    }

    public static void DirtyTrackingAndSaveEnablementForAnExistingJob()
    {
        var (vm, _, _, _) = Existing("Stock ingest (Dad)");
        T.True("existing", vm.IsExisting && vm.HasRunsPanel && !vm.IsNew);
        T.True("clean: not dirty and save is off", !vm.IsDirty && !vm.CanSave);
        vm.Name = "Stock ingest (Dad) 2";
        T.True("name edit makes it dirty and saveable", vm.IsDirty && vm.CanSave);
        vm.Name = "Stock ingest (Dad)";
        T.True("reverted", !vm.IsDirty && !vm.CanSave);
        vm.Schedule.SkipHolidays = HolidayCalendar.None;
        T.True("skip edit", vm.IsDirty && vm.CanSave);
        vm.Schedule.SkipHolidays = HolidayCalendar.Nyse;
        vm.Enabled = false;
        T.True("enabled edit", vm.IsDirty);
        vm.Enabled = true;
        vm.Schedule.Times[0].Text = "18:30";
        T.True("time edit", vm.IsDirty && vm.Schedule.CronText == "30 18 * * 1-5");
        vm.Schedule.IsCronMode = true;
        vm.Schedule.CronText = "0 18 * * 1-5";
        T.True("typing the original back", !vm.IsDirty);
        T.True("an existing job is not locked", !vm.Notice.IsLocked);
    }

    public static void DriftedJobIsLockedAgainstSaving()
    {
        var (vm, _, _, _) = Existing("Weekly report");
        T.True("locked", vm.Notice.IsLocked && vm.Notice.LockedNote is not null);
        vm.Name = "Weekly report 2";
        T.True("cannot save", !vm.CanSave);
    }

    public static void ThePlannerSummaryIsShownInTheFooter()
    {
        var (vm, _, _, _) = Existing("Stock ingest (Dad)");
        vm.RefreshPlanAsync().GetAwaiter().GetResult();
        T.Eq("no changes", "No changes to save", vm.FooterText);
        vm.Schedule.Times[0].Text = "18:30";
        vm.RefreshPlanAsync().GetAwaiter().GetResult();
        T.True("update summary", vm.FooterText.StartsWith("Will update:", StringComparison.Ordinal) && vm.FooterText.Contains("18:30", StringComparison.Ordinal));
        var (n, _, _, _) = New("good-morning");
        n.Program.UseFile(@"C:\Tools\x.exe");
        n.RefreshPlanAsync().GetAwaiter().GetResult();
        T.Eq("create summary", @"Will create \Cadence\Good morning", n.FooterText);
    }

    public static void SavingAnExistingJobShowsTheNextRunAndResetsDirty()
    {
        var (vm, services, _, host) = Existing("Stock ingest (Dad)");
        vm.Name = "Stock ingest (Dad) v2";
        vm.SaveAsync().GetAwaiter().GetResult();
        T.True("saved text", vm.Status is { } s && s.StartsWith("Saved - next run ", StringComparison.Ordinal), vm.Status);
        T.True("not an error", !vm.StatusIsError);
        T.True("not dirty after save", !vm.IsDirty && !vm.CanSave);
        T.Eq("list refreshed", 1, host.Refreshed);
        T.True("stored", services.LoadManagedAsync().GetAwaiter().GetResult().Entries.Any(e => e.Task.Job?.Name == "Stock ingest (Dad) v2"));
        T.Eq("still the same job id", "d4dst0ck", vm.BuildJob().Id);
    }

    public static void SavingANewJobMakesItExistingWithARunsPanel()
    {
        var (vm, services, _, _) = New("every-15-minutes", DemoSet.Empty);
        vm.Program.UseFile(@"C:\Windows\System32\cmd.exe");
        vm.Program.Args = "/d /c echo hi";
        T.True("can save", vm.CanSave);
        vm.SaveAsync().GetAwaiter().GetResult();
        T.True("now existing", vm.IsExisting && vm.HasRunsPanel && !vm.IsNew);
        T.True("no runs yet", vm.Runs.NoRuns && vm.Runs.AverageText == "-" && vm.Runs.SuccessText == "-");
        T.Eq("shown to the list", 1, services.LoadManagedAsync().GetAwaiter().GetResult().Entries.Count);
        T.True("status", vm.Status!.StartsWith("Saved - next run ", StringComparison.Ordinal));
        T.True("delete is now possible", vm.CanDelete);
    }

    public static void SavedTextForDisabledAndSignInJobs()
    {
        var (vm, _, _, _) = New("at-login", DemoSet.Empty);
        vm.Program.UseFile(@"C:\Tools\x.exe");
        vm.SaveAsync().GetAwaiter().GetResult();
        T.Eq("sign in", "Saved - runs when you sign in.", vm.Status);
        vm.Enabled = false;
        vm.SaveAsync().GetAwaiter().GetResult();
        T.True("disabled", vm.Status!.Contains("switched off", StringComparison.Ordinal));
    }

    public static void DeleteAsksThenRemovesAndLeaves()
    {
        var (vm, services, dialogs, host) = Existing("Good morning");
        dialogs.Answer = false;
        vm.DeleteAsync().GetAwaiter().GetResult();
        T.True("declined: nothing happens", host.Closed == 0 && services.LoadManagedAsync().GetAwaiter().GetResult().Entries.Any(e => e.Task.Job?.Name == "Good morning"));
        dialogs.Answer = true;
        vm.DeleteAsync().GetAwaiter().GetResult();
        T.True("gone", !services.LoadManagedAsync().GetAwaiter().GetResult().Entries.Any(e => e.Task.Job?.Name == "Good morning"));
        T.True("left the editor with a toast", host.Closed == 1 && host.Toasts.Any(t => t.Contains("Deleted", StringComparison.Ordinal)));
        var (n, _, _, _) = New("blank");
        T.True("a new job has no delete", !n.CanDelete);
    }

    public static void BackAsksToDiscardOnlyWhenDirty()
    {
        var (vm, _, dialogs, host) = Existing("Good morning");
        vm.RequestCloseAsync().GetAwaiter().GetResult();
        T.True("clean: closes without asking", host.Closed == 1 && dialogs.Asked == 0);
        vm.Name = "Changed";
        dialogs.Answer = false;
        vm.RequestCloseAsync().GetAwaiter().GetResult();
        T.True("dirty, declined: stays", host.Closed == 1 && dialogs.Asked == 1);
        dialogs.Answer = true;
        vm.RequestCloseAsync().GetAwaiter().GetResult();
        T.True("dirty, accepted: closes", host.Closed == 2 && dialogs.Asked == 2);
    }

    public static void RunsPanelShowsSummaryAndExpandsRows()
    {
        var (vm, services, _, host) = Existing("Stock ingest (Dad)");
        ManagedEntry entry = services.LoadManagedAsync().GetAwaiter().GetResult().Entries.First(e => e.Task.Job?.Name == "Stock ingest (Dad)");
        T.Eq("rows", entry.History.Recent.Count, vm.Runs.RunRows.Count);
        T.Eq("average", EditorFormat.AverageText(entry.History.Summary), vm.Runs.AverageText);
        T.Eq("success", EditorFormat.SuccessText(entry.History.Summary), vm.Runs.SuccessText);
        T.Eq("newest is the holiday skip", "Skipped · Thanksgiving Day", vm.Runs.RunRows[0].Text.Title);
        T.True("failed row", vm.Runs.RunRows.Any(r => r.Text.Title == "Failed · exit 1" && r.Text.Note == "HTTPError: 429 Too Many Requests"));
        RunRowViewModel ok = vm.Runs.RunRows[1];
        T.True("collapsed", !ok.IsExpanded);
        ok.ToggleCommand.Execute(null);
        T.True("expanded with output", ok.IsExpanded && ok.ShownText.Contains("started", StringComparison.Ordinal));
        T.Eq("exit", "Exit code 0", ok.Detail.Exit);
        ok.ShowErrors = true;
        T.True("errors tab", ok.ShownText == "(nothing on the error stream)");
        vm.Runs.CopyRunCommand.Execute(ok);
        T.True("copied", host.Copied.Count == 1 && host.Copied[0].Contains("Exit code 0", StringComparison.Ordinal));
        RunRowViewModel skipped = vm.Runs.RunRows[0];
        skipped.ToggleCommand.Execute(null);
        T.True("skipped row has no output files", !skipped.HasOutputFiles && skipped.Detail.Exit == "Not run (skipped)");
    }

    public static void BlockedAndSkippedRowsReadCorrectly()
    {
        var (vm, _, _, _) = Existing("Nightly photo sync", DemoSet.RunsShowcase);
        string[] titles = vm.Runs.RunRows.Select(r => r.Text.Title).ToArray();
        T.True("skipped", titles[0] == "Skipped · Thanksgiving Day");
        T.True("failed", titles[1] == "Failed · exit 3");
        T.True("blocked", titles.Contains("Couldn't start (access denied)"));
        T.True("timed out", titles.Contains("Timed out"));
        RunRowViewModel blocked = vm.Runs.RunRows.First(r => r.Run.Status == RunStatus.NotReported);
        T.Eq("blocked note is the code's sentence only (no guessed cause)", TaskResultCodes.AccessDeniedLaunch, blocked.Text.Note);
        T.True("the possibilities sit in the expanded detail, labelled", blocked.Detail.Message!.Contains(TaskResultCodes.AccessDeniedPossibilities, StringComparison.Ordinal)
            && TaskResultCodes.AccessDeniedPossibilities.StartsWith("Things that can cause this:", StringComparison.Ordinal));
        T.Eq("blocked detail exit", "No exit code (the job never reported back)", blocked.Detail.Exit);
        T.True("skipped flagged", vm.Runs.RunRows[0].Text.IsSkipped);
    }

    public static void RunNowStartsTheJobAndRefreshesRuns()
    {
        var (vm, _, _, host) = Existing("Good morning");
        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        T.True("started toast", host.Toasts.Any(t => t.StartsWith("Started", StringComparison.Ordinal)));
        T.True("the first look found the new run", vm.Runs.IsRunning && vm.Runs.RunRows[0].Run.Status == RunStatus.Running);
    }

    public static void EditorFormatHelpers()
    {
        T.Eq("exe folder", @"C:\Tools", EditorFormat.DefaultWorkingFolder(@"C:\Tools\yt-dlp.exe", ""));
        T.Eq("script folder wins", @"C:\Users\Dad\stocks", EditorFormat.DefaultWorkingFolder(@"C:\Windows\py.exe", @"-u ""C:\Users\Dad\stocks\ingest.py"""));
        T.Eq("root of a drive", @"C:\", EditorFormat.DefaultWorkingFolder(@"C:\run.exe", ""));
        T.Eq("bare name", null, EditorFormat.DefaultWorkingFolder("node.exe", "report.js"));
        T.Eq("bare interpreter, rooted script", @"C:\s", EditorFormat.DefaultWorkingFolder("node.exe", @"C:\s\report.js"));
        T.Eq("env path", null, EditorFormat.DefaultWorkingFolder(@"%SystemRoot%\System32\cmd.exe", "/c dir"));
        T.Eq("file name", "yt-dlp.exe", EditorFormat.FileName(@"C:\Tools\yt-dlp.exe"));
        T.Eq("folder part", "", EditorFormat.FolderPart("node.exe"));

        string[] good = ["8", "08:00", "8:05", "17:30", "0:00", "23:59", "8am", "5:30 pm", "12am", "12pm"];
        TimeOnly[] expect = [new(8, 0), new(8, 0), new(8, 5), new(17, 30), new(0, 0), new(23, 59), new(8, 0), new(17, 30), new(0, 0), new(12, 0)];
        for (int i = 0; i < good.Length; i++)
        {
            T.True("parses " + good[i], EditorFormat.TryParseTime(good[i], out TimeOnly t) && t == expect[i]);
        }
        foreach (string bad in new[] { "", "24:00", "8:60", "x", "8:5", "13pm", "0pm", "1:2:3", "-1" })
            T.True("rejects '" + bad + "'", !EditorFormat.TryParseTime(bad, out _));

        var now = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.FromHours(-5));
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("T", TimeSpan.FromHours(-5), "T", "T");
        T.Eq("today", "Today 7:00:01", EditorFormat.PastWhen(new DateTimeOffset(2026, 11, 27, 7, 0, 1, TimeSpan.FromHours(-5)), now, zone));
        T.Eq("yesterday", "Yesterday 18:00:02", EditorFormat.PastWhen(new DateTimeOffset(2026, 11, 26, 18, 0, 2, TimeSpan.FromHours(-5)), now, zone));
        T.Eq("older", "Mon 23 Nov 18:00", EditorFormat.PastWhen(new DateTimeOffset(2026, 11, 23, 18, 0, 2, TimeSpan.FromHours(-5)), now, zone));
        T.Eq("relative today", "today", EditorFormat.RelativeDay(now.AddHours(5), now, zone));
        T.Eq("relative tomorrow", "tomorrow", EditorFormat.RelativeDay(now.AddDays(1), now, zone));
        T.Eq("relative 3 days", "in 3 days", EditorFormat.RelativeDay(now.AddDays(3), now, zone));
        T.Eq("relative far", "", EditorFormat.RelativeDay(now.AddDays(30), now, zone));
        T.Eq("exit code", "Exit code 3", EditorFormat.ExitText(3));
        T.True("hresult exit", EditorFormat.ExitText(unchecked((int)0x80070005)).Contains("0x80070005", StringComparison.Ordinal));
        T.Eq("success", "90%", EditorFormat.SuccessText(new JobSummary(RunStatus.Ok, now, TimeSpan.FromSeconds(12), 0.9, 10)));
        T.Eq("success detail", "of the last 10 runs", EditorFormat.SuccessDetail(new JobSummary(RunStatus.Ok, now, TimeSpan.FromSeconds(12), 0.9, 10)));
        T.Eq("none", "-", EditorFormat.SuccessText(JobSummary.Empty));
        T.Eq("none avg", "-", EditorFormat.AverageText(JobSummary.Empty));
        T.True("holiday list", EditorFormat.HolidayList(HolidayCalendar.Nyse, 2026).Contains("Thu 26 Nov", StringComparison.Ordinal));
        T.True("holiday list none", EditorFormat.HolidayList(HolidayCalendar.None, 2026).Contains("holidays included", StringComparison.Ordinal));
        T.Eq("pretty name", "Ingest stocks", Cadence.Core.Execution.DropInterpreter.NameFromFile("ingest_stocks.py"));
    }
}
