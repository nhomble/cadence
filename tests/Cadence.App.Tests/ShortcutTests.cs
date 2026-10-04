using System.IO;
using Cadence.App.Interop;
using Cadence.Core.Execution;
using Cadence.TestHarness;

namespace Cadence.App.Tests;

/// <summary>
/// Phase 14 wiring, the shell half: the shortcut reader and the real-machine file probe on real .lnk fixtures, and shortcuts going through the drop interpreter.
/// They moved here from Cadence.Tests (Phase 6 of docs\REFACTOR-modules.md) because ShortcutResolver lives in the WPF app (COM). Nothing a shortcut points to is ever run.
/// </summary>
public static class ShortcutTests
{
    // ---- the .lnk reader on a real shortcut ----

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(LongName(System.IO.Path.GetTempPath()), "cadence-drop-" + Guid.NewGuid().ToString("N")[..8]);
        public TempDir() { Directory.CreateDirectory(Path); }

        // %TEMP% can be an 8.3 short path (tester~1); shortcuts store long names, so build fixtures from the long form.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);

        static string LongName(string path)
        {
            var sb = new System.Text.StringBuilder(1024);
            uint n = GetLongPathName(path, sb, (uint)sb.Capacity);
            return n > 0 && n < sb.Capacity ? sb.ToString().TrimEnd('\\') : path.TrimEnd('\\');
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* temp: harmless */ } }
    }

    /// <summary>Makes a real .lnk with the shell's IShellLinkW (Unicode, like the reader). The target and arguments are only stored, never run.</summary>
    static void MakeShortcut(string lnk, string target, string? args = null, string? startIn = null)
    {
        object link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShortcutResolver.ShellLinkClass)!)!;
        try
        {
            var shell = (IShellLinkW)link;
            shell.SetPath(target);
            if (args is not null) shell.SetArguments(args);
            if (startIn is not null) shell.SetWorkingDirectory(startIn);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
    }

    public static void ShortcutResolverReadsARealShortcut()
    {
        using var dir = new TempDir();
        string script = Path.Combine(dir.Path, "ingest stocks.py");
        File.WriteAllText(script, "# fixture, never run\n");
        string lnk = Path.Combine(dir.Path, "Stock ingestion.lnk");
        MakeShortcut(lnk, script, "--day 1 \"a b\"", dir.Path);

        string? target = ShortcutResolver.Resolve(lnk, out string? args, out string? cwd);
        T.Eq("target", script, target);
        T.Eq("arguments", "--day 1 \"a b\"", args);
        T.Eq("start in", dir.Path, cwd);

        // Unicode everywhere: the shortcut's name, the target's folder and name, and the arguments
        string jp = Path.Combine(dir.Path, "日本語 Über");
        Directory.CreateDirectory(jp);
        string jpScript = Path.Combine(jp, "レポート_für.py");
        File.WriteAllText(jpScript, "# fixture, never run\n");
        string jpLnk = Path.Combine(dir.Path, "レポート.lnk");
        MakeShortcut(jpLnk, jpScript, "--name \"été 日本\"", jp);
        string? jpTarget = ShortcutResolver.Resolve(jpLnk, out string? jpArgs, out string? jpCwd);
        T.Eq("unicode target", jpScript, jpTarget);
        T.Eq("unicode arguments", "--name \"été 日本\"", jpArgs);
        T.Eq("unicode start in", jp, jpCwd);

        T.True("a missing shortcut gives null", ShortcutResolver.Resolve(Path.Combine(dir.Path, "missing.lnk"), out _, out _) is null);
        T.True("blank path gives null", ShortcutResolver.Resolve("  ", out _, out _) is null);

        var probe = new SystemFileProbe();
        T.Eq("probe resolves the same", script, probe.ResolveShortcut(lnk, out _, out _));
        T.True("probe: file exists", probe.FileExists(script) && !probe.FileExists(script + ".nope"));
        T.True("probe: folder exists", probe.DirectoryExists(dir.Path) && !probe.DirectoryExists(script));
        T.True("probe: expands %VARS%", probe.FileExists(@"%SystemRoot%\System32\cmd.exe"));
        T.True("probe: finds cmd.exe on PATH", probe.FindOnPath("cmd.exe") is { } found && File.Exists(found));
        T.True("probe: nothing for a made-up name", probe.FindOnPath("cadence-no-such-program.exe") is null);
    }

    public static void ShortcutsGoThroughTheInterpreterEndToEnd()
    {
        using var dir = new TempDir();
        var probe = new SystemFileProbe();

        // to a script, with arguments and a Start in folder
        string script = Path.Combine(dir.Path, "ingest_stocks.js");
        File.WriteAllText(script, "// fixture, never run\n");
        string lnk = Path.Combine(dir.Path, "Stock ingestion.lnk");
        MakeShortcut(lnk, script, "--day 1", dir.Path);
        DropDraft? d = DropInterpreter.Interpret(lnk, probe, out string? reject);
        T.True("script shortcut accepted", d is not null, reject);
        T.Eq("name comes from the shortcut", "Stock ingestion", d!.Name);
        T.True("arguments follow the script path", d.Run.Args.EndsWith("\"" + script + "\" --day 1", StringComparison.Ordinal), d.Run.Args);
        T.Eq("start in", dir.Path, d.Run.WorkingDirectory);
        T.Eq("source is the shortcut", lnk, d.Run.SourcePath);

        // to a program, without Start in: the program's folder
        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string lnk2 = Path.Combine(dir.Path, "\u00dcbersicht \u65e5\u672c.lnk");
        MakeShortcut(lnk2, cmdExe, "/d /c exit 0");
        DropDraft? e = DropInterpreter.Interpret(lnk2, probe, out reject);
        T.True("program shortcut accepted", e is not null, reject);
        T.True("exe", string.Equals(cmdExe, e!.Run.Exe, StringComparison.OrdinalIgnoreCase), e.Run.Exe);
        T.Eq("arguments verbatim", "/d /c exit 0", e.Run.Args);
        T.True("working folder defaults to the program's folder", string.Equals(Environment.SystemDirectory, e.Run.WorkingDirectory, StringComparison.OrdinalIgnoreCase), e.Run.WorkingDirectory);
        T.Eq("unicode name", "\u00dcbersicht \u65e5\u672c", e.Name);

        // to a batch file: arguments are quoted for cmd
        string bat = Path.Combine(dir.Path, "tidy.bat");
        File.WriteAllText(bat, "@rem fixture, never run\n");
        string lnk3 = Path.Combine(dir.Path, "Tidy.lnk");
        MakeShortcut(lnk3, bat, "a&b \"c d\"");
        DropDraft? f = DropInterpreter.Interpret(lnk3, probe, out reject);
        T.True("batch shortcut accepted", f is not null, reject);
        T.Eq("args quoted for cmd", "/d /s /c \"\"" + bat + "\" \"a&b\" \"c d\"\"", f!.Run.Args);

        // not a shortcut at all, and a missing one
        string fake = Path.Combine(dir.Path, "broken.lnk");
        File.WriteAllText(fake, "this is not a shortcut");
        T.True("a damaged .lnk is refused, not a crash", DropInterpreter.Interpret(fake, probe, out reject) is null && reject == DropInterpreter.ShortcutUnresolved, reject);
        T.True("a missing .lnk is refused", DropInterpreter.Interpret(Path.Combine(dir.Path, "gone.lnk"), probe, out reject) is null && reject == DropInterpreter.ShortcutUnresolved);
        T.True("a folder is refused", DropInterpreter.Interpret(dir.Path, probe, out reject) is null && reject == DropInterpreter.FolderRejected);
    }
}
