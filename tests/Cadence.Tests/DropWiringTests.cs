using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// Phase 14 wiring: what the main window's view model does with a drop (an open editor, the template sheet, the queue for several files, refusals), DropIntake, and Browse
/// going through the same interpreter. Nothing dropped is ever run. The WPF drag events and the shortcut reader are tested in tests\Cadence.App.Tests.
/// </summary>
public static class DropWiringTests
{
    // ---- DropIntake ----

    sealed class Interp : IProgramInterpreter
    {
        readonly DropProgramInterpreter _inner = new(new DemoFileProbe());
        public ProgramChoice? Interpret(string path, out string? rejectReason) => _inner.Interpret(path, out rejectReason);
    }

    public static void IntakeUsesEveryUsableFileAndExplainsTheRest()
    {
        var one = DropIntake.Process([@"C:\s\a.ps1"], new Interp());
        T.True("one usable file, no message", one.Programs.Count == 1 && one.Message is null && !one.AnyRejected);

        var bad = DropIntake.Process([@"C:\Docs\notes.txt"], new Interp());
        T.True("one refused file: just the reason", bad.Programs.Count == 0 && bad.Message == DropInterpreter.NotRunnable && bad.AnyRejected);

        var mixed = DropIntake.Process([@"C:\s\a.ps1", @"C:\Docs\notes.txt", @"C:\s\b.py"], new Interp());
        T.Eq("two usable", 2, mixed.Programs.Count);
        T.Eq("in drop order", @"C:\s\a.ps1", mixed.Programs[0].Path);
        T.True("message names the refused file and the queue", mixed.Message!.Contains("Skipped notes.txt", StringComparison.Ordinal) && mixed.Message.Contains("one at a time", StringComparison.Ordinal), mixed.Message);

        var many = DropIntake.Process(Enumerable.Range(0, 14).Select(i => @"C:\s\f" + i + ".ps1").ToList(), new Interp());
        T.Eq("capped", DropIntake.MaxFiles, many.Programs.Count);
        T.True("says so", many.Message!.Contains("first 10", StringComparison.Ordinal), many.Message);

        var none = DropIntake.Process([], new Interp());
        T.True("empty drop", none.Programs.Count == 0 && none.Message is null);
    }

    // ---- the main window's view model ----

    sealed class Dialogs : IDialogs, IShell
    {
        public bool Answer { get; set; } = true;
        public string? Picked { get; set; }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger) => Task.FromResult(Answer);
        public Task<bool> ConfirmTypedAsync(string title, string message, string word, string confirmText) => Task.FromResult(Answer);
        public Task InfoAsync(string title, string message) => Task.CompletedTask;
        public void ShowAbout() { }
        public string? PickProgram() => Picked;
        public void OpenTaskScheduler() { }
        public bool OpenFolder(string path) => false;
        public void CopyText(string text) { }
    }

    static MainViewModel NewMain(DemoSet set = DemoSet.Empty)
    {
        var vm = new MainViewModel(new DemoServices(set), new Dialogs(), new Dialogs());
        vm.Start();
        vm.List.InitialLoad.GetAwaiter().GetResult();
        return vm;
    }

    // [CP-C] A drop outside an editor goes straight to a pre-filled draft (name from the file, program set, "Every day at 9:00");
    // the template cards stay one click away inside the draft ("Use a preset…"), where they only change the schedule.

    public static void DroppingOnTheListOpensADraftForThatProgramStraightAway()
    {
        MainViewModel vm = NewMain(DemoSet.Normal);
        vm.HandleDropAsync([@"C:\Users\Dad\stocks\ingest_stocks.py"]).GetAwaiter().GetResult();
        T.True("no sheet in between", !vm.IsSheetOpen);
        T.True("an editor is open", vm.CurrentPage is EditorViewModel);
        var editor = (EditorViewModel)vm.CurrentPage;
        T.True("a new, unsaved job", editor.IsNew);
        T.Eq("name from the file", "Ingest stocks", editor.Name);
        T.Eq("python launcher", @"C:\Windows\py.exe", editor.Program.Exe);
        T.Eq("args", "-u \"C:\\Users\\Dad\\stocks\\ingest_stocks.py\"", editor.Program.Args);
        T.True("utf8 and the env var", editor.Program.Utf8Output && editor.Program.Env.Count == 1);
        T.Eq("working folder is the script's folder", @"C:\Users\Dad\stocks", editor.BuildJob().Run.WorkingDirectory);
        T.Eq("the default schedule is Start from scratch's", "0 9 * * *", editor.Schedule.CronText);
        T.True("the presets are one click away", editor.Schedule.HasPresets);
        T.True("ready to save", editor.CanSave && editor.BlockingProblem is null);
    }

    public static void UseAPresetChangesOnlyTheScheduleOfTheDraft()
    {
        MainViewModel vm = NewMain(DemoSet.Normal);
        vm.HandleDropAsync([@"C:\Users\Dad\stocks\ingest_stocks.py"]).GetAwaiter().GetResult();
        var editor = (EditorViewModel)vm.CurrentPage;
        string exe = editor.Program.Exe, args = editor.Program.Args;

        editor.Schedule.PickPresetCommand.Execute(null);
        T.True("the sheet opens over the draft", vm.IsSheetOpen && vm.IsPresetSheet && ReferenceEquals(vm.PresetTarget, editor));
        T.Eq("it asks about this job", "When should Ingest stocks run?", vm.SheetTitle);
        T.True("it says what stays", vm.SheetSubtitle.Contains("name and the program stay", StringComparison.Ordinal), vm.SheetSubtitle);
        T.True("no drop area on that sheet", !vm.ShowSheetDropArea);

        vm.PickTemplateById("weekdays-6pm");
        T.True("sheet closed, same editor", !vm.IsSheetOpen && !vm.IsPresetSheet && ReferenceEquals(vm.CurrentPage, editor));
        T.Eq("the preset's cron", "0 18 * * 1-5", editor.Schedule.CronText);
        T.Eq("the preset's holiday rule", Templates.All.First(t => t.Id == "weekdays-6pm").SuggestedSkip, editor.Schedule.SkipHolidays);
        T.Eq("Simple view follows", HumanFrequency.Weekdays, editor.Schedule.Frequency);
        T.True("name and program untouched", editor.Name == "Ingest stocks" && editor.Program.Exe == exe && editor.Program.Args == args);
        T.True("still ready to save", editor.CanSave);

        // In Cron view the preset lands in the text box too.
        editor.Schedule.IsCronMode = true;
        editor.Schedule.PickPresetCommand.Execute(null);
        vm.PickTemplateById("every-15-minutes");
        T.Eq("cron view shows the preset", "*/15 * * * *", editor.Schedule.CronText);

        // Closing the preset sheet changes nothing.
        editor.Schedule.PickPresetCommand.Execute(null);
        vm.HandleEscape();
        T.True("escape closes it", !vm.IsSheetOpen && !vm.IsPresetSheet && editor.Schedule.CronText == "*/15 * * * *");
        T.True("the next PLUS sheet is the plain one", vm.SheetTitle == "New schedule" && vm.ShowSheetDropArea);
    }

    public static void ADropOnTheEmptyStateStartsTheSameWayAndThePlusSheetStillWorks()
    {
        MainViewModel vm = NewMain(DemoSet.Empty);
        vm.HandleDropAsync([@"C:\Users\Dad\Desktop\Stock ingestion.lnk"]).GetAwaiter().GetResult();
        var editor = (EditorViewModel)vm.CurrentPage;
        T.Eq("name from the shortcut", "Stock ingestion", editor.Name);
        T.Eq("the shortcut's target runs", @"C:\Windows\py.exe", editor.Program.Exe);
        T.True("source recorded", editor.BuildJob().Run.SourcePath == @"C:\Users\Dad\Desktop\Stock ingestion.lnk");

        vm.BackCommand.Execute(null);   // the test dialogs say "discard"
        T.True("back on the list", vm.IsListShowing);
        vm.NewCommand.Execute(null);
        T.True("a plain PLUS sheet is plain", vm.IsSheetOpen && vm.SheetTitle == "New schedule" && vm.ShowSheetDropArea);
        vm.HandleEscape();
        vm.PickTemplateById("blank");
        T.True("and gives an empty editor", vm.CurrentPage is EditorViewModel { Program.HasProgram: false });
    }

    public static void ADropWhileThePlusSheetIsOpenOpensTheDraftInstead()
    {
        MainViewModel vm = NewMain();
        vm.NewCommand.Execute(null);
        T.True("sheet open", vm.IsSheetOpen);
        vm.HandleDropAsync([@"C:\s\a.ps1"]).GetAwaiter().GetResult();
        T.True("sheet closed, draft open", !vm.IsSheetOpen && vm.CurrentPage is EditorViewModel { Name: "A" });
    }

    public static void SeveralFilesBecomeOneDraftEachInSequence()
    {
        MainViewModel vm = NewMain();
        vm.HandleDropAsync([@"C:\s\first_job.ps1", @"C:\Docs\notes.txt", @"C:\s\second_job.py", @"C:\s\third-job.bat"]).GetAwaiter().GetResult();
        T.True("toast tells what was refused and that there is a queue", vm.Toast is { } t && t.Contains("Skipped notes.txt", StringComparison.Ordinal) && t.Contains("one at a time", StringComparison.Ordinal), vm.Toast);
        var first = (EditorViewModel)vm.CurrentPage;
        T.Eq("first draft", "First job", first.Name);
        T.Eq("two more wait", 2, vm.QueuedDrops);

        vm.BackCommand.Execute(null);   // leave (the test dialogs say "discard")
        var second = (EditorViewModel)vm.CurrentPage;
        T.True("the next draft opens by itself", !ReferenceEquals(first, second) && second.Name == "Second job" && second.Program.Exe == @"C:\Windows\py.exe");
        T.True("and says so", vm.Toast is { } n && n.Contains("second_job.py", StringComparison.Ordinal) && n.Contains("1 more", StringComparison.Ordinal), vm.Toast);

        vm.BackCommand.Execute(null);
        var third = (EditorViewModel)vm.CurrentPage;
        T.True("third is cmd", third.Name == "Third job" && third.Program.Exe == DropInterpreter.CmdExe && third.Program.Args == "/d /s /c \"\"C:\\s\\third-job.bat\"\"");
        vm.BackCommand.Execute(null);
        T.True("queue is empty: just the list", vm.IsListShowing && !vm.IsSheetOpen && vm.QueuedDrops == 0);
    }

    public static void ADropOnAnOpenEditorReplacesTheProgram()
    {
        MainViewModel vm = NewMain();
        vm.PickTemplateById("good-morning");
        var editor = (EditorViewModel)vm.CurrentPage;
        T.True("no program yet", !editor.Program.HasProgram);
        vm.HandleDropAsync([@"C:\Tools\backup.exe"]).GetAwaiter().GetResult();
        T.True("program set", editor.Program.HasProgram && editor.Program.Exe == @"C:\Tools\backup.exe");
        T.True("the editor stays", ReferenceEquals(vm.CurrentPage, editor) && !vm.IsSheetOpen);
        T.Eq("name stays the template's", "Good morning", editor.Name);

        vm.HandleDropAsync([@"C:\Users\tester\scripts\sync.bat"]).GetAwaiter().GetResult();
        T.True("replaced by the script's interpreter", editor.Program.Exe == DropInterpreter.CmdExe && editor.Program.Args.Contains("sync.bat", StringComparison.Ordinal));

        vm.HandleDropAsync([@"C:\Docs\notes.txt"]).GetAwaiter().GetResult();
        T.True("a refused file leaves the program alone and says why", editor.Program.Exe == DropInterpreter.CmdExe && editor.Program.ProgramMessage == DropInterpreter.NotRunnable && vm.Toast == DropInterpreter.NotRunnable);

        vm.HandleDropAsync([@"C:\s\one.ps1", @"C:\s\two.ps1"]).GetAwaiter().GetResult();
        T.True("several files: the first one is used", editor.Program.Args.Contains("one.ps1", StringComparison.Ordinal));
        T.True("and the user is told", vm.Toast is { } t && t.Contains("first file only", StringComparison.Ordinal), vm.Toast);

        editor.Schedule.PickPresetCommand.Execute(null);
        vm.HandleDropAsync([@"C:\Tools\backup.exe"]).GetAwaiter().GetResult();
        T.True("a drop while the preset sheet is open closes it and sets the program", !vm.IsSheetOpen && editor.Program.Exe == @"C:\Tools\backup.exe");
    }

    public static void ADropThatIsRefusedOnTheListOnlyShowsAMessage()
    {
        MainViewModel vm = NewMain();
        vm.HandleDropAsync([@"C:\Docs\notes.txt"]).GetAwaiter().GetResult();
        T.True("no sheet, no draft", !vm.IsSheetOpen && vm.QueuedDrops == 0 && vm.IsListShowing);
        T.Eq("friendly message", DropInterpreter.NotRunnable, vm.Toast);
        vm.HandleDropAsync([]).GetAwaiter().GetResult();
        T.True("an empty drop does nothing", !vm.IsSheetOpen && vm.IsListShowing);
    }

    public static void HoverLightsTheRightThing()
    {
        MainViewModel vm = NewMain();
        vm.SetDropHover(true);
        T.True("list: the overlay", vm.ShowDropOverlay);
        vm.SetDropHover(false);
        T.True("off", !vm.ShowDropOverlay);
        vm.PickTemplateById("blank");
        var editor = (EditorViewModel)vm.CurrentPage;
        vm.SetDropHover(true);
        T.True("editor: the drop zone, not the overlay", editor.Program.IsDropHover && !vm.ShowDropOverlay);
        vm.HandleDropAsync([@"C:\Tools\x.exe"]).GetAwaiter().GetResult();
        T.True("a drop turns it off", !editor.Program.IsDropHover);
    }

    // ---- the editor: Browse and UseFile through the same interpreter ----

    public static void BrowseAndUseFileUseTheInterpreterAndShowItsWarnings()
    {
        var services = new DemoServices(DemoSet.Normal);
        var dialogs = new Dialogs();
        EditorViewModel ed = EditorViewModel.ForTemplate(services, dialogs, EditorHost.None, Templates.All.First(t => t.Id == "blank"));
        dialogs.Picked = @"C:\Users\tester\scripts\report.js";
        ed.Program.BrowseCommand.Execute(null);
        T.Eq("browse picked a script: node", @"C:\Program Files\nodejs\node.exe", ed.Program.Exe);
        T.Eq("blank template takes the file's name", "Report", ed.Name);
        T.True("no warning", !ed.Program.HasProgramWarnings);

        dialogs.Picked = @"C:\Docs\notes.txt";
        ed.Program.BrowseCommand.Execute(null);
        T.True("browse picked a document: refused, program kept", ed.Program.ProgramMessage == DropInterpreter.NotRunnable && ed.Program.Exe == @"C:\Program Files\nodejs\node.exe");

        // a Python script with no Python on the (pretend) machine shows a warning that clears when the program is edited by hand
        EditorViewModel ed2 = EditorViewModel.ForTemplate(services, dialogs, EditorHost.None, Templates.All.First(t => t.Id == "blank"));
        ed2.Program.ProgramInterpreter = new DropProgramInterpreter(new EmptyProbe());
        T.True("python script accepted", ed2.Program.UseFile(@"C:\s\a.py"));
        T.True("with a warning", ed2.Program.HasProgramWarnings && ed2.Program.ProgramWarnings[0].Contains("Python", StringComparison.Ordinal));
        ed2.Program.Exe = @"C:\Other\python.exe";
        T.True("editing the program clears the warning", !ed2.Program.HasProgramWarnings);

        // a working folder equal to the automatic one stays automatic; a shortcut's own Start in is kept
        EditorViewModel ed3 = EditorViewModel.ForTemplate(services, dialogs, EditorHost.None, Templates.All.First(t => t.Id == "blank"));
        ed3.Program.UseFile(@"C:\Users\tester\Desktop\YouTube feed.lnk");
        T.Eq("Start in kept", @"C:\Users\tester\Videos", ed3.Program.WorkingDirectory);
        T.Eq("shortcut args", "--flat-playlist -J :ythome", ed3.Program.Args);
        ed3.Program.UseFile(@"C:\Tools\other.exe");
        T.Eq("automatic again", "", ed3.Program.WorkingDirectory);
    }

    public static void ADriftedScheduleIgnoresDropsAndBrowse()
    {
        var services = new DemoServices(DemoSet.Normal);
        ManagedEntry entry = services.LoadManagedAsync().GetAwaiter().GetResult().Entries.First(e => e.Task.Job?.Name == "Weekly report");
        var dialogs = new Dialogs { Picked = @"C:\Tools\other.exe" };
        EditorViewModel ed = EditorViewModel.ForEntry(services, dialogs, EditorHost.None, entry);
        string before = ed.Program.Exe;
        T.True("the schedule is locked", ed.Notice.IsLocked);
        T.True("a file is refused", !ed.Program.UseFile(@"C:\Tools\other.exe") && ed.Program.Exe == before);
        T.True("with an explanation", ed.Program.ProgramMessage is { } m && m.Contains("changed outside Cadence", StringComparison.Ordinal));
        ed.Program.BrowseCommand.Execute(null);
        T.Eq("browse does not change it either", before, ed.Program.Exe);
        T.True("an async drop is refused too", !ed.Program.UseFileAsync(@"C:\Tools\other.exe").GetAwaiter().GetResult() && ed.Program.Exe == before);
    }

    /// <summary>Whatever a drop produces must compose into a valid task (metadata encode and wrapper rules) and read back unchanged.</summary>
    public static void EveryKindOfDropComposesIntoAValidTaskAndReadsBack()
    {
        var services = new DemoServices(DemoSet.Normal);
        string[] files =
        [
            @"C:\Tools\yt-dlp.exe", @"C:\Users\tester\scripts\morning.ps1", @"C:\Users\tester\scripts\sync.bat", @"C:\Users\tester\scripts\sync.cmd", @"C:\Users\Dad\stocks\ingest_stocks.py",
            @"C:\Users\tester\scripts\report.js", @"C:\Users\tester\scripts\clean.vbs", @"C:\My Scripts\a&b (copy)\run it.ps1", "C:\\Users\\tester\\\u00dcber\u65e5\u672c\\Bericht.py",
            @"\\server\share\jobs\nightly.bat", @"C:\Users\Dad\Desktop\Stock ingestion.lnk", @"C:\Users\Dad\Desktop\YouTube feed.lnk", @"C:\Users\tester\Desktop\Tidy downloads.lnk",
        ];
        foreach (string file in files)
        {
            EditorViewModel ed = EditorViewModel.ForTemplate(services, new Dialogs(), EditorHost.None, Templates.All.First(t => t.Id == "blank"));
            T.True("accepted " + file, ed.Program.UseFile(file), ed.Program.ProgramMessage);
            T.True("no blocking problem for " + file, ed.BlockingProblem is null, ed.BlockingProblem);
            JobDefinition job = ed.BuildJob();
            string xml = TaskComposer.ComposeXml(job, new ComposeContext("CadenceTest", "CadenceTest", "S-1-5-21-1-2-3-1001", "Drop test", services.Now, null, ValueList<string>.Empty));
            T.True("composed " + file, xml.Contains("cadence/1", StringComparison.Ordinal));
            TaskDefinitionModel model = TaskXmlReader.Read(xml);
            MetadataProblem problem = MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out CadenceMetadata? meta);
            T.True("metadata reads back for " + file, problem == MetadataProblem.None && meta is not null, problem.ToString());
            if (meta is not null)
            {
                T.Eq("exe round trip for " + file, job.Run.Exe, meta.Job.Run.Exe);
                T.Eq("args round trip for " + file, job.Run.Args, meta.Job.Run.Args);
                T.Eq("cwd round trip for " + file, job.Run.WorkingDirectory, meta.Job.Run.WorkingDirectory);
                T.Eq("source round trip for " + file, job.Run.SourcePath, meta.Job.Run.SourcePath);
            }
        }
    }

    /// <summary>Real Start Menu shortcuts carry "Start in" values such as <c>%windir%\system32</c> or <c>%HOMEDRIVE%%HOMEPATH%</c>; they must save (the wrapper expands %VARS%).</summary>
    public static void ShortcutStartInFoldersWithEnvironmentVariablesCanBeSaved()
    {
        var services = new DemoServices(DemoSet.Normal);
        EditorViewModel ed = EditorViewModel.ForTemplate(services, new Dialogs(), EditorHost.None, Templates.All.First(t => t.Id == "blank"));
        var choice = new ProgramChoice(@"C:\Windows\system32\notepad.exe", "", @"%HOMEDRIVE%%HOMEPATH%", OutputEncoding.Oem, ValueList<KeyValuePair<string, string>>.Empty,
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Accessories\Notepad.lnk", "Notepad", ValueList<string>.Empty);
        ed.Program.ApplyChoice(choice, nameFromProgram: true);
        T.True("no blocking problem", ed.BlockingProblem is null && ed.CanSave, ed.BlockingProblem);
        T.Eq("Start in kept as written", @"%HOMEDRIVE%%HOMEPATH%", ed.BuildJob().Run.WorkingDirectory);
        T.Eq("name", "Notepad", ed.Name);
    }

    public static void TheProgramCardNamesTheScriptNotTheInterpreter()
    {
        ProgramCardText py = EditorFormat.ProgramCard(@"C:\Windows\py.exe", "-u \"C:\\Users\\Dad\\stocks\\ingest.py\"", @"C:\Users\Dad\Desktop\Stock ingestion.lnk");
        T.Eq("python title", "ingest.py", py.Title);
        T.Eq("python folder", @"C:\Users\Dad\stocks", py.Folder);
        T.Eq("python via", "Runs with py.exe \u00b7 from the shortcut Stock ingestion", py.Via);

        ProgramCardText ps = EditorFormat.ProgramCard(DropInterpreter.PowerShellExe, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\s\\morning.ps1\"", @"C:\s\morning.ps1");
        T.True("ps1", ps.Title == "morning.ps1" && ps.Folder == @"C:\s" && ps.Via == "Runs with powershell.exe", ps.Via);

        ProgramCardText bat = EditorFormat.ProgramCard(DropInterpreter.CmdExe, "/d /s /c \"\"C:\\My Scripts\\sync it.bat\" \"a b\" c\"", null);
        T.True("bat with spaces and arguments", bat.Title == "sync it.bat" && bat.Folder == @"C:\My Scripts" && bat.Via == "Runs with Windows batch (cmd.exe)", bat.Title + "|" + bat.Folder + "|" + bat.Via);

        ProgramCardText exe = EditorFormat.ProgramCard(@"C:\Tools\yt-dlp.exe", "--flat-playlist -J :ythome", null);
        T.True("plain program", exe.Title == "yt-dlp.exe" && exe.Folder == @"C:\Tools" && exe.Via == "");

        ProgramCardText lnkExe = EditorFormat.ProgramCard(@"C:\Windows\System32\notepad.exe", "", @"C:\x\Notepad.lnk");
        T.True("program from a shortcut", lnkExe.Title == "notepad.exe" && lnkExe.Via == "From the shortcut Notepad", lnkExe.Via);

        T.Eq("script of a plain program", null, EditorFormat.ScriptOf(@"C:\Tools\a.exe", "--x"));
        T.Eq("cmd without a batch path", null, EditorFormat.ScriptOf(DropInterpreter.CmdExe, "/c echo hi"));
        T.Eq("working folder for a batch line", @"C:\My Scripts", EditorFormat.DefaultWorkingFolder(DropInterpreter.CmdExe, "/d /s /c \"\"C:\\My Scripts\\a.bat\" x\""));
    }

    public static void UseFileAsyncRunsTheInterpreterOffTheCallersThread()
    {
        var services = new DemoServices(DemoSet.Normal);
        EditorViewModel ed = EditorViewModel.ForTemplate(services, new Dialogs(), EditorHost.None, Templates.All.First(t => t.Id == "blank"));
        int callerThread = Environment.CurrentManagedThreadId;
        int interpretThread = -1;
        ed.Program.ProgramInterpreter = new ThreadRecorder(() => interpretThread = Environment.CurrentManagedThreadId);
        T.True("accepted", ed.Program.UseFileAsync(@"C:\s\a.exe").GetAwaiter().GetResult());
        T.True("interpreted on another thread", interpretThread != -1 && interpretThread != callerThread);
        T.Eq("applied", @"C:\s\a.exe", ed.Program.Exe);
    }

    sealed class ThreadRecorder(Action onInterpret) : IProgramInterpreter
    {
        public ProgramChoice? Interpret(string path, out string? rejectReason)
        {
            onInterpret();
            rejectReason = null;
            return new ProgramChoice(path, "", null, OutputEncoding.Oem, ValueList<KeyValuePair<string, string>>.Empty, path, "A", ValueList<string>.Empty);
        }
    }

    sealed class EmptyProbe : IFileProbe
    {
        public bool FileExists(string path) => false;
        public string? FindOnPath(string exeName) => null;
        public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir) { args = null; workingDir = null; return null; }
    }
}
