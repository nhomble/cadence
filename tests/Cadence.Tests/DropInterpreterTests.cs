using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>PLAN 7.5 and Phase 14: every row of the interpreter table, the fallbacks, the shortcut rules and the rejections, against a fake machine.</summary>
public static class DropInterpreterTests
{
    const string Ps = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe";
    const string Cmd = @"%SystemRoot%\System32\cmd.exe";
    const string CScript = @"%SystemRoot%\System32\cscript.exe";
    const string Py = @"C:\Windows\py.exe";
    const string Node = @"C:\Program Files\nodejs\node.exe";

    /// <summary>A pretend PC. Defaults: Python's launcher and Node.js installed, nothing else.</summary>
    sealed class FakeProbe : IFileProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase) { Py };
        public Dictionary<string, string> OnPath { get; } = new(StringComparer.OrdinalIgnoreCase) { ["node.exe"] = Node };
        public Dictionary<string, (string? Target, string? Args, string? Cwd)> Shortcuts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Folders.Contains(path);
        public string? FindOnPath(string exeName) => OnPath.GetValueOrDefault(exeName);
        public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir)
        {
            if (Shortcuts.TryGetValue(lnkPath, out var s)) { args = s.Args; workingDir = s.Cwd; return s.Target; }
            args = null; workingDir = null; return null;
        }
    }

    static DropDraft Ok(string path, FakeProbe? probe = null)
    {
        DropDraft? d = DropInterpreter.Interpret(path, probe ?? new FakeProbe(), out string? reject);
        T.True("accepted: " + path, d is not null, reject);
        return d ?? new DropDraft("", new RunSpec { Exe = "" }, ValueList<string>.Empty);
    }

    static string? Reject(string path, FakeProbe? probe = null)
    {
        DropDraft? d = DropInterpreter.Interpret(path, probe ?? new FakeProbe(), out string? reject);
        T.True("refused: " + path, d is null && !string.IsNullOrEmpty(reject));
        return reject;
    }

    // ---- .exe ----

    public static void ExeIsUsedAsItIs()
    {
        DropDraft d = Ok(@"C:\Tools\yt-dlp.exe");
        T.Eq("name", "Yt dlp", d.Name);
        T.Eq("exe", @"C:\Tools\yt-dlp.exe", d.Run.Exe);
        T.Eq("no arguments", "", d.Run.Args);
        T.Eq("working folder is the program's folder", @"C:\Tools", d.Run.WorkingDirectory);
        T.Eq("oem", OutputEncoding.Oem, d.Run.Encoding);
        T.Eq("source", @"C:\Tools\yt-dlp.exe", d.Run.SourcePath);
        T.Eq("no warnings", 0, d.Warnings.Count());
        T.True("hidden by default", d.Run.Hidden);
        T.Eq("no env", 0, d.Run.Env.Count());
    }

    public static void ExtensionsAreCaseInsensitiveAndThePathIsKeptAsDropped()
    {
        (string Path, string Name)[] rows =
        [
            (@"C:\TOOLS\RUN.EXE", "RUN"),
            (@"C:\Tools\Mixed.ExE", "Mixed"),
            (@"c:\tools\lower.exe", "Lower"),
        ];
        foreach ((string path, string name) in rows)
        {
            DropDraft d = Ok(path);
            T.Eq("exe kept: " + path, path, d.Run.Exe);
            T.Eq("name: " + path, name, d.Name);
        }
        T.True("PS1 upper", Ok(@"C:\s\Morning.PS1").Run.Exe == Ps);
        T.True("BAT upper", Ok(@"C:\s\Sync.BAT").Run.Exe == Cmd);
        T.True("Cmd mixed", Ok(@"C:\s\Sync.Cmd").Run.Exe == Cmd);
        T.True("PY upper", Ok(@"C:\s\Run.PY").Run.Exe == Py);
        T.True("JS upper", Ok(@"C:\s\Run.JS").Run.Exe == Node);
        T.True("VBS upper", Ok(@"C:\s\Run.VBS").Run.Exe == CScript);
    }

    public static void NamesFollowTheRule()
    {
        (string File, string Name)[] rows =
        [
            ("ingest_stocks.py", "Ingest stocks"),
            ("yt-dlp.exe", "Yt dlp"),
            ("morning.ps1", "Morning"),
            ("my--odd__name.cmd", "My odd name"),
            ("_hidden.bat", "Hidden"),
            ("a.exe", "A"),
            ("backup.tar.exe", "Backup.tar"),
            ("\u00dcbersicht_\u65e5\u672c.py", "\u00dcbersicht \u65e5\u672c"),
            ("Already Fine.js", "Already Fine"),
            ("noextension", "Noextension"),
            (".exe", ".exe"),
        ];
        foreach ((string file, string name) in rows) T.Eq("name of " + file, name, DropInterpreter.NameFromFile(file));
    }

    // ---- scripts ----

    public static void PowerShellScript()
    {
        DropDraft d = Ok(@"C:\Users\tester\scripts\morning.ps1");
        T.Eq("name", "Morning", d.Name);
        T.Eq("exe", Ps, d.Run.Exe);
        T.Eq("args", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\Users\\tester\\scripts\\morning.ps1\"", d.Run.Args);
        T.Eq("working folder", @"C:\Users\tester\scripts", d.Run.WorkingDirectory);
        T.Eq("oem", OutputEncoding.Oem, d.Run.Encoding);
        T.Eq("source", @"C:\Users\tester\scripts\morning.ps1", d.Run.SourcePath);
        T.Eq("no warnings", 0, d.Warnings.Count());

        var withPwsh = new FakeProbe();
        withPwsh.OnPath["pwsh.exe"] = @"C:\Program Files\PowerShell\7\pwsh.exe";
        DropDraft w = Ok(@"C:\Users\tester\scripts\morning.ps1", withPwsh);
        T.Eq("pwsh on PATH gives one warning", 1, w.Warnings.Count());
        T.True("warning names pwsh", w.Warnings.First().Contains("pwsh.exe", StringComparison.Ordinal));
        T.Eq("still the same program", Ps, w.Run.Exe);
    }

    public static void BatchFileRunsThroughCmd()
    {
        DropDraft d = Ok(@"C:\Users\tester\scripts\sync.bat");
        T.Eq("exe", Cmd, d.Run.Exe);
        T.Eq("args", "/d /s /c \"\"C:\\Users\\tester\\scripts\\sync.bat\"\"", d.Run.Args);
        T.Eq("working folder", @"C:\Users\tester\scripts", d.Run.WorkingDirectory);
        T.Eq("oem", OutputEncoding.Oem, d.Run.Encoding);
        T.Eq("cmd", "/d /s /c \"\"C:\\x\\b.cmd\"\"", Ok(@"C:\x\b.cmd").Run.Args);
    }

    public static void PythonPrefersTheLauncherThenPathThenWarns()
    {
        DropDraft d = Ok(@"C:\Users\Dad\stocks\ingest_stocks.py");
        T.Eq("name", "Ingest stocks", d.Name);
        T.Eq("launcher", Py, d.Run.Exe);
        T.Eq("args", "-u \"C:\\Users\\Dad\\stocks\\ingest_stocks.py\"", d.Run.Args);
        T.Eq("utf8", OutputEncoding.Utf8, d.Run.Encoding);
        T.Eq("one env var", 1, d.Run.Env.Count());
        T.Eq("env name", "PYTHONIOENCODING", d.Run.Env.First().Key);
        T.Eq("env value", "utf-8", d.Run.Env.First().Value);
        T.Eq("working folder", @"C:\Users\Dad\stocks", d.Run.WorkingDirectory);
        T.Eq("no warnings", 0, d.Warnings.Count());

        var noLauncher = new FakeProbe();
        noLauncher.Files.Clear();
        noLauncher.OnPath["python.exe"] = @"C:\Python312\python.exe";
        DropDraft p = Ok(@"C:\s\a.py", noLauncher);
        T.Eq("python from PATH (full path)", @"C:\Python312\python.exe", p.Run.Exe);
        T.Eq("no warning", 0, p.Warnings.Count());

        var store = new FakeProbe();
        store.Files.Clear();
        store.OnPath["python.exe"] = @"C:\Users\tester\AppData\Local\Microsoft\WindowsApps\python.exe";
        T.Eq("the Store stub warns", 1, Ok(@"C:\s\a.py", store).Warnings.Count());

        var none = new FakeProbe();
        none.Files.Clear();
        DropDraft n = Ok(@"C:\s\a.py", none);
        T.Eq("bare python.exe when nothing is found", "python.exe", n.Run.Exe);
        T.Eq("a warning", 1, n.Warnings.Count());
        T.True("warning mentions Python", n.Warnings.First().Contains("Python", StringComparison.Ordinal));
        T.Eq("still utf8", OutputEncoding.Utf8, n.Run.Encoding);
    }

    public static void JavaScriptPrefersNodeThenCscript()
    {
        DropDraft d = Ok(@"C:\Users\tester\scripts\report.js");
        T.Eq("node (full path)", Node, d.Run.Exe);
        T.Eq("args", "\"C:\\Users\\tester\\scripts\\report.js\"", d.Run.Args);
        T.Eq("utf8", OutputEncoding.Utf8, d.Run.Encoding);
        T.Eq("no warnings", 0, d.Warnings.Count());
        T.Eq("no env needed", 0, d.Run.Env.Count());

        var noNode = new FakeProbe();
        noNode.OnPath.Clear();
        DropDraft c = Ok(@"C:\Users\tester\scripts\report.js", noNode);
        T.Eq("cscript", CScript, c.Run.Exe);
        T.Eq("cscript args", "//NoLogo \"C:\\Users\\tester\\scripts\\report.js\"", c.Run.Args);
        T.Eq("oem", OutputEncoding.Oem, c.Run.Encoding);
        T.Eq("a warning", 1, c.Warnings.Count());
    }

    public static void VbsRunsUnderCscript()
    {
        DropDraft d = Ok(@"C:\s\clean up.vbs");
        T.Eq("name", "Clean up", d.Name);
        T.Eq("exe", CScript, d.Run.Exe);
        T.Eq("args", "//NoLogo \"C:\\s\\clean up.vbs\"", d.Run.Args);
        T.Eq("oem", OutputEncoding.Oem, d.Run.Encoding);
        T.Eq("working folder", @"C:\s", d.Run.WorkingDirectory);
    }

    // ---- awkward paths ----

    public static void AwkwardPaths()
    {
        (string Path, string Args, string Cwd)[] ps =
        [
            (@"C:\My Scripts\a&b (copy)\run it.ps1", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\My Scripts\\a&b (copy)\\run it.ps1\"", @"C:\My Scripts\a&b (copy)"),
            (@"\\server\share\jobs\nightly.ps1", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"\\\\server\\share\\jobs\\nightly.ps1\"", @"\\server\share\jobs"),
            (@"D:\run.ps1", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"D:\\run.ps1\"", @"D:\"),
        ];
        foreach ((string path, string args, string cwd) in ps)
        {
            DropDraft d = Ok(path);
            T.Eq("args for " + path, args, d.Run.Args);
            T.Eq("cwd for " + path, cwd, d.Run.WorkingDirectory);
        }

        DropDraft u = Ok("C:\\Users\\tester\\\u00dcber\u65e5\u672c\\Bericht_f\u00fcr.py");
        T.Eq("unicode name", "Bericht f\u00fcr", u.Name);
        T.Eq("unicode folder", "C:\\Users\\tester\\\u00dcber\u65e5\u672c", u.Run.WorkingDirectory);
        T.Eq("unicode args", "-u \"C:\\Users\\tester\\\u00dcber\u65e5\u672c\\Bericht_f\u00fcr.py\"", u.Run.Args);

        DropDraft bat = Ok(@"C:\Tools & Co\100 %\run (1).bat");
        T.Eq("bat in a hostile folder is one quoted path", "/d /s /c \"\"C:\\Tools & Co\\100 %\\run (1).bat\"\"", bat.Run.Args);
        T.Eq("a % in the path warns", 1, bat.Warnings.Count());
        T.True("the warning is about %", bat.Warnings.First().Contains('%', StringComparison.Ordinal));
        T.Eq("no % no warning", 0, Ok(@"C:\Tools & Co\run.bat").Warnings.Count());
    }

    public static void WhitespaceAndQuotesAroundThePathAreIgnored()
    {
        string[] inputs = ["  C:\\s\\a.ps1  ", "\"C:\\s\\a.ps1\"", " \"C:\\s\\a.ps1 \" ", "C:\\s\\a.ps1\t"];
        foreach (string input in inputs)
        {
            DropDraft d = Ok(input);
            T.Eq("source for '" + input.Replace("\t", "\\t", StringComparison.Ordinal) + "'", @"C:\s\a.ps1", d.Run.SourcePath);
            T.Eq("name", "A", d.Name);
        }
    }

    // ---- rejections ----

    public static void UnsupportedFilesAreRefusedWithAFriendlyMessage()
    {
        string expected = "Cadence can run programs (.exe), shortcuts (.lnk) and scripts (.bat, .cmd, .ps1, .py, .js, .vbs).";
        T.Eq("message text", expected, DropInterpreter.NotRunnable);
        string[] files =
        [
            @"C:\Docs\notes.txt", @"C:\Docs\report.docx", @"C:\Docs\photo.JPG", @"C:\Docs\setup.msi", @"C:\Docs\lib.dll", @"C:\Docs\archive.zip",
            @"C:\Docs\noextension", @"C:\Docs\.gitignore", @"C:\Docs\run.exe.txt", @"C:\Docs\run.ps1.bak", @"C:\Docs\run.ps", @"C:\Docs\x.pyc", @"C:\Docs\x.jsx", @"C:\Docs\x.url",
            @"\\server\share\readme.md",
        ];
        foreach (string f in files) T.Eq("message for " + f, expected, Reject(f));
    }

    public static void FoldersAndEmptyInputAreRefused()
    {
        var probe = new FakeProbe();
        probe.Folders.Add(@"C:\Users\tester\Documents");
        probe.Folders.Add(@"C:\Tools\folder.exe");
        T.Eq("folder", DropInterpreter.FolderRejected, Reject(@"C:\Users\tester\Documents", probe));
        T.True("the folder message says what a folder is and what works", DropInterpreter.FolderRejected.StartsWith("That's a folder.", StringComparison.Ordinal) && DropInterpreter.FolderRejected.EndsWith(DropInterpreter.NotRunnable, StringComparison.Ordinal));
        T.Eq("a folder named like a program is still a folder", DropInterpreter.FolderRejected, Reject(@"C:\Tools\folder.exe", probe));
        T.Eq("empty", DropInterpreter.NotAPath, Reject(""));
        T.Eq("blank", DropInterpreter.NotAPath, Reject("   "));
        T.Eq("only quotes", DropInterpreter.NotAPath, Reject("\"\""));
        T.Throws<ArgumentNullException>("null probe", () => DropInterpreter.Interpret(@"C:\a.exe", null!, out _));
    }

    // ---- shortcuts ----

    static FakeProbe WithLink(string lnk, string? target, string? args = null, string? cwd = null)
    {
        var p = new FakeProbe();
        p.Shortcuts[lnk] = (target, args, cwd);
        return p;
    }

    public static void ShortcutToAScriptTakesTheShortcutsNameAndTheScriptsInterpreter()
    {
        const string lnk = @"C:\Users\tester\Desktop\Stock ingestion.lnk";
        DropDraft d = Ok(lnk, WithLink(lnk, @"C:\Users\Dad\stocks\ingest.py"));
        T.Eq("name from the shortcut", "Stock ingestion", d.Name);
        T.Eq("python launcher", Py, d.Run.Exe);
        T.Eq("args", "-u \"C:\\Users\\Dad\\stocks\\ingest.py\"", d.Run.Args);
        T.Eq("working folder is the target's folder", @"C:\Users\Dad\stocks", d.Run.WorkingDirectory);
        T.Eq("source is the shortcut", lnk, d.Run.SourcePath);
        T.Eq("utf8", OutputEncoding.Utf8, d.Run.Encoding);
        T.Eq("env", "PYTHONIOENCODING", d.Run.Env.First().Key);
    }

    public static void ShortcutToAProgramKeepsArgumentsAndStartIn()
    {
        const string lnk = @"C:\Users\tester\Desktop\YouTube feed.LNK";
        DropDraft d = Ok(lnk, WithLink(lnk, @"C:\Tools\yt-dlp.exe", "--flat-playlist -J :ythome", @"D:\Downloads"));
        T.Eq("name", "YouTube feed", d.Name);
        T.Eq("exe", @"C:\Tools\yt-dlp.exe", d.Run.Exe);
        T.Eq("arguments are kept verbatim", "--flat-playlist -J :ythome", d.Run.Args);
        T.Eq("the shortcut's Start in wins", @"D:\Downloads", d.Run.WorkingDirectory);
        T.Eq("source", lnk, d.Run.SourcePath);

        const string plain = @"C:\Users\tester\Desktop\Notepad.lnk";
        DropDraft n = Ok(plain, WithLink(plain, @"C:\Windows\System32\notepad.exe"));
        T.Eq("without Start in: the target's folder", @"C:\Windows\System32", n.Run.WorkingDirectory);
        T.Eq("no arguments", "", n.Run.Args);
        T.Eq("name", "Notepad", n.Name);
    }

    public static void ShortcutToAPowerShellScriptAppendsItsArguments()
    {
        const string lnk = @"C:\x\Morning.lnk";
        DropDraft d = Ok(lnk, WithLink(lnk, @"C:\s\morning.ps1", "-Day 1 \"a b\""));
        T.Eq("args", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\s\\morning.ps1\" -Day 1 \"a b\"", d.Run.Args);
        DropDraft node = Ok(@"C:\x\Rep.lnk", WithLink(@"C:\x\Rep.lnk", @"C:\s\report.js", "--week 3"));
        T.Eq("node args", "\"C:\\s\\report.js\" --week 3", node.Run.Args);
        DropDraft py = Ok(@"C:\x\Ing.lnk", WithLink(@"C:\x\Ing.lnk", @"C:\s\ingest.py", "--day 1"));
        T.Eq("python args", "-u \"C:\\s\\ingest.py\" --day 1", py.Run.Args);
    }

    /// <summary>[CP-A] A shortcut to a batch file: every argument is quoted for cmd.exe, so none of them can start a second command.</summary>
    public static void ShortcutToABatchFileQuotesEveryArgumentForCmd()
    {
        const string lnk = @"C:\x\Tidy downloads.lnk";
        DropDraft d = Ok(lnk, WithLink(lnk, @"C:\Users\tester\scripts\tidy.bat", "\"C:\\Users\\tester\\My Docs\" --older-than=30 a&b plain"));
        T.Eq("name", "Tidy downloads", d.Name);
        T.Eq("exe", Cmd, d.Run.Exe);
        T.Eq("args", "/d /s /c \"\"C:\\Users\\tester\\scripts\\tidy.bat\" \"C:\\Users\\tester\\My Docs\" \"--older-than=30\" \"a&b\" plain\"", d.Run.Args);
        T.Eq("working folder", @"C:\Users\tester\scripts", d.Run.WorkingDirectory);

        DropDraft hostile = Ok(lnk, WithLink(lnk, @"C:\s\x.cmd", "\"a\"\"b & calc\" c|d e>f g<h i^j (k) !l"));
        T.Eq("hostile",
            "/d /s /c \"\"C:\\s\\x.cmd\" \"a\"\"b & calc\" \"c|d\" \"e>f\" \"g<h\" \"i^j\" \"(k)\" \"!l\"\"", hostile.Run.Args);

        DropDraft pct = Ok(lnk, WithLink(lnk, @"C:\s\x.bat", "%TEMP% 50%"));
        T.Eq("percent", "/d /s /c \"\"C:\\s\\x.bat\" \"%TEMP%\" \"50%\"\"", pct.Run.Args);
        T.Eq("percent warns once", 1, pct.Warnings.Count());

        string? reject;
        DropDraft? withBreak = DropInterpreter.Interpret(lnk, WithLink(lnk, @"C:\s\x.bat", "\"a\nb\""), out reject);
        T.True("a line break can't be passed to a batch file", withBreak is null && reject!.Contains("line break", StringComparison.Ordinal));
    }

    public static void ShortcutRejections()
    {
        const string lnk = @"C:\x\Thing.lnk";
        T.Eq("no target (advertised or Store shortcut)", "This shortcut can't be resolved; drop the program itself.", Reject(lnk, WithLink(lnk, null)));
        T.Eq("unreadable shortcut", DropInterpreter.ShortcutUnresolved, Reject(lnk, new FakeProbe()));
        T.Eq("blank target", DropInterpreter.ShortcutUnresolved, Reject(lnk, WithLink(lnk, "  ")));

        var folder = WithLink(lnk, @"C:\Users\tester\Documents");
        folder.Folders.Add(@"C:\Users\tester\Documents");
        T.Eq("to a folder", DropInterpreter.ShortcutToFolder, Reject(lnk, folder));
        T.Eq("to another shortcut", DropInterpreter.ShortcutToShortcut, Reject(lnk, WithLink(lnk, @"C:\x\Other.lnk")));
        T.Eq("to a document", DropInterpreter.NotRunnable, Reject(lnk, WithLink(lnk, @"C:\Docs\notes.txt")));
        T.Eq("to a web page (.url)", DropInterpreter.NotRunnable, Reject(lnk, WithLink(lnk, @"C:\x\site.url")));
    }

    public static void TheInterpreterRunsNothingAndReadsNothingElse()
    {
        // The fake records every question: a drop only asks about the python launcher, PATH, pwsh/python/node, shortcuts and folders.
        var asked = new List<string>();
        var probe = new Recording(asked);
        DropInterpreter.Interpret(@"C:\s\a.py", probe, out _);
        DropInterpreter.Interpret(@"C:\s\a.ps1", probe, out _);
        DropInterpreter.Interpret(@"C:\s\a.js", probe, out _);
        DropInterpreter.Interpret(@"C:\s\a.exe", probe, out _);
        T.True("asked about the launcher", asked.Contains("exists:C:\\Windows\\py.exe"));
        T.True("never asked whether the dropped file exists", !asked.Any(a => a.StartsWith("exists:C:\\s\\", StringComparison.Ordinal)));
    }

    sealed class Recording(List<string> asked) : IFileProbe
    {
        public bool FileExists(string path) { asked.Add("exists:" + path); return false; }
        public bool DirectoryExists(string path) { asked.Add("dir:" + path); return false; }
        public string? FindOnPath(string exeName) { asked.Add("path:" + exeName); return null; }
        public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir) { asked.Add("lnk:" + lnkPath); args = null; workingDir = null; return null; }
    }
}
