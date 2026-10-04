using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.ViewModels;
using Cadence.Core;
using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>"Open folder" / "Copy path" on the program card and in the list menu, the command line shown on the card, and the build version in the status bar. Explorer is never started: the shell is a recording stub.</summary>
public static class ProgramRevealTests
{
    sealed class Probe(IEnumerable<string> files, IEnumerable<string> dirs) : IFileProbe
    {
        readonly HashSet<string> _files = new(files, StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _dirs = new(dirs, StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string path) => _files.Contains(path);
        public string? FindOnPath(string exeName) => null;
        public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir) { args = null; workingDir = null; return null; }
        public bool DirectoryExists(string path) => _dirs.Contains(path);
    }

    static void Lst(string name, string[] expected, IEnumerable<string> actual) => T.Eq(name, string.Join(" | ", expected), string.Join(" | ", actual));

    // ---- which file ----

    public static void ScriptIsRecognisedOnlyInTheKnownInterpreterForms()
    {
        T.Eq("py -u", @"C:\x\ingest.py", ProgramReveal.ScriptOf(@"C:\Windows\py.exe", @"-u ""C:\x\ingest.py"""));
        T.Eq("py launcher flag then script", @"C:\x\a.py", ProgramReveal.ScriptOf("py.exe", @"-3.11 -u C:\x\a.py arg"));
        T.Eq("python", @"C:\x y\a.py", ProgramReveal.ScriptOf(@"C:\Python312\python.exe", @"""C:\x y\a.py"" --flag"));
        T.Eq("pythonw", @"C:\x\a.pyw", ProgramReveal.ScriptOf("PYTHONW.EXE", @"C:\x\a.pyw"));
        T.Eq("node", @"C:\Users\tester\scripts\report.js", ProgramReveal.ScriptOf("node.exe", @"C:\Users\tester\scripts\report.js"));
        T.Eq("node option first", @"C:\x\a.mjs", ProgramReveal.ScriptOf(@"C:\Program Files\nodejs\node.exe", @"--no-warnings C:\x\a.mjs"));
        T.Eq("powershell -File", @"C:\Users\tester\scripts\morning.ps1",
            ProgramReveal.ScriptOf(DropInterpreter.PowerShellExe, @"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""C:\Users\tester\scripts\morning.ps1"""));
        T.Eq("pwsh -File", @"C:\x\a.ps1", ProgramReveal.ScriptOf("pwsh.exe", @"-NoProfile -File C:\x\a.ps1 one"));
        T.Eq("cmd /d /s /c (Cadence's own line)", @"C:\Users\tester\scripts\sync.bat", ProgramReveal.ScriptOf(DropInterpreter.CmdExe, @"/d /s /c ""C:\Users\tester\scripts\sync.bat"""));
        T.Eq("cmd with arguments", @"C:\a b\x.cmd", ProgramReveal.ScriptOf(@"C:\Windows\System32\cmd.exe", @"/d /s /c """"C:\a b\x.cmd"" ""one two"""""));
        T.Eq("cmd /c plain", @"C:\x\a.bat", ProgramReveal.ScriptOf("cmd.exe", @"/c C:\x\a.bat"));
        T.Eq("cscript", @"C:\x\a.vbs", ProgramReveal.ScriptOf("cscript.exe", @"//NoLogo C:\x\a.vbs"));
        T.Eq("a %VAR% path is kept as stored", @"%USERPROFILE%\a.py", ProgramReveal.ScriptOf("py.exe", @"-u %USERPROFILE%\a.py"));
    }

    public static void ForeignFormsFallBackToTheProgram()
    {
        T.True("python -c", ProgramReveal.ScriptOf("py.exe", @"-c ""import C:\x\a.py""") is null);
        T.True("python -m", ProgramReveal.ScriptOf("python.exe", @"-m C:\x\a.py") is null);
        T.True("a relative script says nothing about where it is", ProgramReveal.ScriptOf("py.exe", "-u ingest.py") is null);
        T.True("an unknown interpreter is not guessed", ProgramReveal.ScriptOf("ruby.exe", @"C:\x\a.rb") is null);
        T.True("a program that merely gets a script-looking argument", ProgramReveal.ScriptOf(@"C:\Tools\restic.exe", @"backup C:\x\a.py") is null);
        T.True("powershell -Command", ProgramReveal.ScriptOf("powershell.exe", @"-Command ""& C:\x\a.ps1""") is null);
        T.True("powershell -File with something else", ProgramReveal.ScriptOf("powershell.exe", @"-File C:\x\a.txt") is null);
        T.True("cmd /c echo", ProgramReveal.ScriptOf("cmd.exe", "/c echo hi") is null);
        T.True("cmd with an option we don't know", ProgramReveal.ScriptOf("cmd.exe", @"/q /c C:\x\a.bat") is null);
        T.True("node with a flag that takes a value", ProgramReveal.ScriptOf("node.exe", @"-r preload C:\x\a.js") is null);
        T.True("no arguments", ProgramReveal.ScriptOf("py.exe", "") is null);

        RevealTarget? tool = ProgramReveal.Resolve(@"C:\Tools\restic.exe", @"backup C:\x\a.py", null);
        T.True("falls back to the program", tool is { Kind: RevealKind.Program } && tool.Path == @"C:\Tools\restic.exe");
        T.True("nothing without a program", ProgramReveal.Resolve("", "", null) is null);
    }

    public static void ResolvePrefersTheDroppedShortcutThenTheScriptThenTheProgram()
    {
        RevealTarget? lnk = ProgramReveal.Resolve(@"C:\Windows\py.exe", @"-u ""C:\x\a.py""", @"C:\Users\tester\Desktop\Stock ingestion.lnk");
        T.True("shortcut", lnk is { Kind: RevealKind.Shortcut } && lnk.Path == @"C:\Users\tester\Desktop\Stock ingestion.lnk");
        RevealTarget? dropped = ProgramReveal.Resolve(@"C:\Windows\py.exe", @"-u ""C:\x\a.py""", @"C:\x\a.py");
        T.True("a dropped script (not a shortcut) is the script", dropped is { Kind: RevealKind.Script } && dropped.Path == @"C:\x\a.py");
        RevealTarget? exe = ProgramReveal.Resolve(@"""C:\Program Files\x\x.exe""", "", null);
        T.True("program, quotes dropped", exe is { Kind: RevealKind.Program } && exe.Path == @"C:\Program Files\x\x.exe");
    }

    public static void CommandTextIsTheStoredProgramAndArguments()
    {
        T.Eq("plain", @"C:\Tools\restic.exe backup D:\x", ProgramReveal.CommandText(@"C:\Tools\restic.exe", @"backup D:\x"));
        T.Eq("spaces are quoted", @"""C:\Program Files\x\x.exe"" --a ""b c""", ProgramReveal.CommandText(@"C:\Program Files\x\x.exe", @"--a ""b c"""));
        T.Eq("no arguments", @"C:\Tools\x.exe", ProgramReveal.CommandText(@"C:\Tools\x.exe", "  "));
        T.Eq("%VAR% is not expanded", @"%SystemRoot%\x.exe %TEMP%\a", ProgramReveal.CommandText(@"%SystemRoot%\x.exe", @"%TEMP%\a"));
    }

    public static void NearestExistingFolderWalksUp()
    {
        Func<string, bool> has = p => p is @"C:\Users\tester" or @"C:\";
        T.Eq("own folder first", @"C:\Users\tester", ProgramReveal.NearestExistingFolder(@"C:\Users\tester\a.py", has));
        T.Eq("a few levels up", @"C:\Users\tester", ProgramReveal.NearestExistingFolder(@"C:\Users\tester\gone\deeper\a.py", has));
        T.Eq("the drive", @"C:\", ProgramReveal.NearestExistingFolder(@"C:\nothing\a.py", has));
        T.True("none", ProgramReveal.NearestExistingFolder(@"D:\nothing\a.py", has) is null);
    }

    // ---- the flow ----

    public static void RevealHighlightsTheFileWhenItExists()
    {
        var revealed = new List<string>();
        var opened = new List<string>();
        var toasts = new List<string>();
        var probe = new Probe([@"C:\x\ingest.py"], [@"C:\x"]);
        ProgramReveal.Reveal(@"C:\Windows\py.exe", @"-u ""C:\x\ingest.py""", null, probe, p => { revealed.Add(p); return true; }, p => { opened.Add(p); return true; }, toasts.Add);
        Lst("the script is revealed", [@"C:\x\ingest.py"], revealed);
        T.True("no plain folder open, no toast", opened.Count == 0 && toasts.Count == 0);
    }

    public static void RevealOfAMissingFileOpensTheNearestFolderAndSaysSo()
    {
        var revealed = new List<string>();
        var opened = new List<string>();
        var toasts = new List<string>();
        var probe = new Probe([], [@"C:\x"]);
        ProgramReveal.Reveal("py.exe", @"-u C:\x\gone\ingest.py", null, probe, p => { revealed.Add(p); return true; }, p => { opened.Add(p); return true; }, toasts.Add);
        T.True("nothing highlighted", revealed.Count == 0);
        Lst("the nearest folder opened", [@"C:\x"], opened);
        Lst("toast", [ProgramReveal.NotFoundToast], toasts);
        T.Eq("the wording", "File not found; opened its folder", ProgramReveal.NotFoundToast);

        toasts.Clear(); opened.Clear();
        ProgramReveal.Reveal(@"Z:\gone\a.exe", "", null, new Probe([], []), _ => true, p => { opened.Add(p); return true; }, toasts.Add);
        T.True("no folder to open", opened.Count == 0 && toasts.Count == 1 && toasts[0].Contains("neither is its folder", StringComparison.Ordinal));
    }

    public static void RevealWithoutAFolderToGoToSaysWhy()
    {
        var toasts = new List<string>();
        var revealed = new List<string>();
        ProgramReveal.Reveal("node.exe", "", null, new Probe([], []), revealed.Add2, _ => true, toasts.Add);
        T.True("a bare program name has no folder: nothing opened", revealed.Count == 0 && toasts.Count == 1 && toasts[0].Contains("node.exe", StringComparison.Ordinal));
        toasts.Clear();
        ProgramReveal.Reveal("", "", null, new Probe([], []), revealed.Add2, _ => true, toasts.Add);
        Lst("no program", ["No program is set yet."], toasts);
    }

    static bool Add2(this List<string> list, string item) { list.Add(item); return true; }

    // ---- the editor card ----

    static (EditorViewModel Vm, RecordingHost Host) Card(string exe, string args, string? working = null)
    {
        var services = new DemoServices(DemoSet.Normal);
        var host = new RecordingHost();
        EditorViewModel vm = EditorViewModel.ForTemplate(services, new RecordingDialogs(), host.Value, Templates.All.First(t => t.Id == "good-morning"));
        vm.Program.Exe = exe;
        vm.Program.Args = args;
        if (working is not null) vm.Program.WorkingDirectory = working;
        return (vm, host);
    }

    public static void EditorShowsTheFullCommandLineAndWorkingFolder()
    {
        var (vm, _) = Card(@"C:\Windows\py.exe", @"-u ""C:\Users\Dad\stocks\ingest.py""");
        T.Eq("command line", @"C:\Windows\py.exe -u ""C:\Users\Dad\stocks\ingest.py""", vm.Program.CommandLineText);
        T.Eq("an empty folder says what it means", @"Working folder: C:\Users\Dad\stocks  (automatic)", vm.Program.WorkingFolderText);
        vm.Program.WorkingDirectory = @"%USERPROFILE%\work";
        T.Eq("a set folder is shown as stored", @"Working folder: %USERPROFILE%\work", vm.Program.WorkingFolderText);
        var (bare, _) = Card("node.exe", "");
        T.Eq("no folder at all", "Working folder: Starts in C:\\Windows\\System32 unless you set a folder", bare.Program.WorkingFolderText);
        T.True("no shortcut line for a plain program", !vm.Program.HasShortcut && vm.Program.ShortcutText.Length == 0);
    }

    public static void OpenFolderRevealsTheProgramOrNearestFolderWithoutStartingExplorer()
    {
        // The demo probe knows C:\Windows\py.exe (a file) and any folder named Documents; nothing else exists.
        var (py, host) = Card(DropInterpreter.PyLauncher, "");
        py.Program.OpenProgramFolderCommand.Execute(null);
        Lst("an existing program is highlighted", [DropInterpreter.PyLauncher], host.Revealed);

        var (script, host2) = Card(DropInterpreter.PyLauncher, @"-u ""C:\Users\tester\Documents\gone\ingest.py""");
        script.Program.OpenProgramFolderCommand.Execute(null);
        T.True("nothing highlighted", host2.Revealed.Count == 0);
        Lst("the script's nearest existing folder opened", [@"C:\Users\tester\Documents"], host2.OpenedFolders);
        Lst("toast", ["File not found; opened its folder"], host2.Toasts);
    }

    public static void CopyPathCopiesTheScriptPath()
    {
        var (vm, host) = Card(@"C:\Windows\py.exe", @"-u ""C:\Users\Dad\stocks\ingest.py""");
        vm.Program.CopyProgramPathCommand.Execute(null);
        Lst("the script, not the interpreter", [@"C:\Users\Dad\stocks\ingest.py"], host.Copied);
        var (exe, host2) = Card(@"C:\Tools\restic.exe", "backup D:\\x");
        exe.Program.CopyProgramPathCommand.Execute(null);
        Lst("the program when there is no script", [@"C:\Tools\restic.exe"], host2.Copied);
    }

    public static void ADroppedShortcutIsRevealedAndShown()
    {
        var services = new DemoServices(DemoSet.Normal);
        var host = new RecordingHost();
        var draft = new JobDefinition
        {
            Id = "t3st0000", Name = "From a shortcut", Schedule = new ScheduleSpec(ValueList.Of("0 9 * * *"), HolidayCalendar.None, new DateOnly(2026, 9, 1)),
            Run = new RunSpec { Exe = DropInterpreter.PyLauncher, Args = @"-u ""C:\Users\Dad\stocks\ingest.py""", SourcePath = @"C:\Users\Dad\Desktop\Stock ingestion.lnk" },
        };
        var vm = new EditorViewModel(services, new RecordingDialogs(), host.Value, draft, null);
        T.Eq("shortcut shown", @"Shortcut: C:\Users\Dad\Desktop\Stock ingestion.lnk", vm.Program.ShortcutText);
        T.True("has shortcut", vm.Program.HasShortcut);
        T.Eq("the command line is the target", @"C:\Windows\py.exe -u ""C:\Users\Dad\stocks\ingest.py""", vm.Program.CommandLineText);
        vm.Program.OpenProgramFolderCommand.Execute(null);
        T.True("a shortcut that is gone: its nearest folder is asked for, nothing real is started", host.Revealed.Count == 0);
        vm.Program.CopyProgramPathCommand.Execute(null);
        Lst("copies the shortcut's path", [@"C:\Users\Dad\Desktop\Stock ingestion.lnk"], host.Copied);
    }

    // ---- the list menu ----

    public static void ListMenuOpenFolderActsOnTheOneSelectedRow()
    {
        var services = new DemoServices(DemoSet.Normal);
        var dialogs = new RecordingDialogs();
        var vm = new MainViewModel(services, dialogs, dialogs);
        vm.Start();
        vm.InitialLoad.GetAwaiter().GetResult();
        ScheduleRowViewModel stock = vm.List.Rows.First(r => r.Name == "Stock ingest (Dad)");
        ScheduleRowViewModel other = vm.List.Rows.First(r => r.Name != "Stock ingest (Dad)");

        vm.List.SetSelection([]);
        T.True("nothing selected: disabled", !vm.List.OpenFolderSelectedCommand.CanExecute(null));
        vm.List.SetSelection([stock, other]);
        T.True("two selected: disabled", !vm.List.OpenFolderSelectedCommand.CanExecute(null));
        vm.List.SetSelection([stock]);
        T.True("one selected: enabled", vm.List.OpenFolderSelectedCommand.CanExecute(null));
        vm.List.OpenFolderSelectedCommand.Execute(null);
        // The demo machine has no such script and no such folder: the answer is a toast, and the stub saw no request.
        T.True("nothing was highlighted or opened", dialogs.Revealed.Count == 0 && dialogs.OpenedFolders.Count == 0);
        T.True("the toast says the file is not there", vm.Toast is { } toast && toast.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the version in the status bar ----

    public static void StatusBarShowsTheSameVersionTextInDemoAndReal()
    {
        var demoDialogs = new RecordingDialogs();
        var realDialogs = new RecordingDialogs();
        var demo = new MainViewModel(new DemoServices(DemoSet.Normal), demoDialogs, demoDialogs);
        var real = new MainViewModel(new ScriptedServices(), realDialogs, realDialogs);
        T.Eq("demo text is the build's display text", AppVersion.Display(), demo.VersionText);
        T.Eq("real text is the same", demo.VersionText, real.VersionText);
        T.True("never empty", demo.VersionText.Length > 0);
        T.True("the tooltip names the raw version", demo.VersionTooltip.StartsWith("Cadence ", StringComparison.Ordinal));
    }

    public static void VersionDisplayRule()
    {
        T.Eq("local build", "dev build", AppVersion.Format(null));
        T.Eq("dev", "dev build", AppVersion.Format("dev"));
        T.Eq("stamped", "v0.2.0", AppVersion.Format("0.2.0"));
        T.Eq("source revision dropped", "v0.2.0", AppVersion.Format("0.2.0+abc123"));
    }
}
