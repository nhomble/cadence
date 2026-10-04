using Cadence.Core.Model;

namespace Cadence.Core.Execution;

/// <summary>What <see cref="DropInterpreter"/> needs to know about the machine. The App implements it; tests use a fake.</summary>
public interface IFileProbe
{
    /// <summary>True when the file exists. <c>%VAR%</c> in the path is expanded first.</summary>
    bool FileExists(string path);

    /// <summary>The full path of <paramref name="exeName"/> (for example <c>node.exe</c>) found on PATH, or null.</summary>
    string? FindOnPath(string exeName);

    /// <summary>
    /// Reads a .lnk file: its target, arguments and "Start in" folder. Returns null when the shortcut can't be read or has no target path
    /// (advertised and Store shortcuts). <paramref name="args"/> and <paramref name="workingDir"/> are null when empty.
    /// </summary>
    string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir);

    /// <summary>True when the path is a folder (only used to give a folder its own friendly message).</summary>
    bool DirectoryExists(string path) => false;
}

/// <summary>A job's program as worked out from one dropped file: a suggested name, the run spec, and warnings to show beside the program card.</summary>
public sealed record DropDraft(string Name, RunSpec Run, ValueList<string> Warnings);

/// <summary>
/// PLAN 7.5: turns a dropped (or browsed) file into a <see cref="DropDraft"/>. Pure: the machine is asked through <see cref="IFileProbe"/>,
/// nothing is run, nothing is read from the file itself. The scripts' interpreters, their arguments and the output encoding follow the 7.5 table;
/// the working folder is always the folder of the file the program is started from (a shortcut's own "Start in" wins).
/// </summary>
public static class DropInterpreter
{
    public const string NotRunnable = "Cadence can run programs (.exe), shortcuts (.lnk) and scripts (.bat, .cmd, .ps1, .py, .js, .vbs).";
    public const string FolderRejected = "That's a folder. " + NotRunnable;
    public const string ShortcutUnresolved = "This shortcut can't be resolved; drop the program itself.";
    public const string ShortcutToShortcut = "This shortcut points to another shortcut; drop the program itself.";
    public const string ShortcutToFolder = "This shortcut opens a folder, not a program. " + NotRunnable;
    public const string NotAPath = "Choose a program, shortcut or script.";

    public const string PowerShellExe = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe";
    public const string CmdExe = @"%SystemRoot%\System32\cmd.exe";
    public const string CScriptExe = @"%SystemRoot%\System32\cscript.exe";
    public const string PyLauncher = @"C:\Windows\py.exe";

    public static DropDraft? Interpret(string path, IFileProbe probe, out string? rejectReason)
    {
        ArgumentNullException.ThrowIfNull(probe);
        rejectReason = null;
        string p = (path ?? "").Trim().Trim('"').Trim();
        if (p.Length == 0) { rejectReason = NotAPath; return null; }
        if (probe.DirectoryExists(p)) { rejectReason = FolderRejected; return null; }

        string fileName = FileNameOf(p);
        string ext = ExtensionOf(fileName).ToLowerInvariant();
        string name = NameFromFile(fileName);

        if (ext == ".lnk") return FromShortcut(p, name, probe, out rejectReason);
        return FromTarget(p, ext, name, p, extraArgs: null, workingDir: null, probe, out rejectReason);
    }

    /// <summary>"ingest_stocks.py" gives "Ingest stocks": no extension, <c>_</c> and <c>-</c> as spaces, first letter upper-case.</summary>
    public static string NameFromFile(string fileName)
    {
        string stem = fileName.Trim();
        int dot = stem.LastIndexOf('.');
        if (dot > 0) stem = stem[..dot];
        stem = stem.Replace('_', ' ').Replace('-', ' ').Trim();
        while (stem.Contains("  ", StringComparison.Ordinal)) stem = stem.Replace("  ", " ", StringComparison.Ordinal);
        if (stem.Length == 0) return fileName.Trim();
        return char.ToUpperInvariant(stem[0]) + stem[1..];
    }

    static DropDraft? FromShortcut(string lnk, string name, IFileProbe probe, out string? reject)
    {
        reject = null;
        string? target = probe.ResolveShortcut(lnk, out string? args, out string? cwd);
        target = target?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(target)) { reject = ShortcutUnresolved; return null; }
        if (probe.DirectoryExists(target)) { reject = ShortcutToFolder; return null; }
        string ext = ExtensionOf(FileNameOf(target)).ToLowerInvariant();
        if (ext == ".lnk") { reject = ShortcutToShortcut; return null; }
        // The program is the shortcut's target; the dropped file is the shortcut (display only).
        return FromTarget(target, ext, name, lnk, string.IsNullOrWhiteSpace(args) ? null : args.Trim(), string.IsNullOrWhiteSpace(cwd) ? null : cwd.Trim(), probe, out reject);
    }

    /// <summary>Builds the draft for a program or script <paramref name="target"/>; <paramref name="source"/> is what the user dropped.</summary>
    static DropDraft? FromTarget(string target, string ext, string name, string source, string? extraArgs, string? workingDir, IFileProbe probe, out string? reject)
    {
        reject = null;
        var warnings = new List<string>();
        string exe;
        string args;
        var encoding = OutputEncoding.Oem;
        var env = new List<KeyValuePair<string, string>>();
        string quoted = "\"" + target + "\"";
        string tail = extraArgs is null ? "" : " " + extraArgs;

        switch (ext)
        {
            case ".exe":
                exe = target;
                args = extraArgs ?? "";
                break;

            case ".ps1":
                exe = PowerShellExe;
                args = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + quoted + tail;
                if (probe.FindOnPath("pwsh.exe") is not null)
                    warnings.Add("PowerShell 7 (pwsh.exe) is installed, but this schedule uses Windows PowerShell 5.1, which every PC has. Change the program under Advanced if the script needs PowerShell 7.");
                break;

            case ".bat" or ".cmd":
                exe = CmdExe;
                // cmd.exe parses the line again, so every argument is quoted for cmd (QuoteForCmd) and the whole line sits in one more pair of quotes.
                IReadOnlyList<string> parts = extraArgs is null ? [] : CommandLine.Split(extraArgs);
                if (parts.Any(a => a.AsSpan().IndexOfAny("\r\n\0") >= 0)) { reject = "This shortcut's arguments contain a line break, which a batch file can't be given."; return null; }
                args = "/d /s /c \"" + quoted + (parts.Count == 0 ? "" : " " + CommandLine.JoinForCmd(parts)) + "\"";
                if (parts.Any(a => a.Contains('%', StringComparison.Ordinal)))
                    warnings.Add("An argument contains a % sign. Windows replaces %NAME% in it with the value of that variable before the batch file sees it.");
                break;

            case ".py":
                string? python = null;
                if (probe.FileExists(PyLauncher)) python = PyLauncher;
                else if (probe.FindOnPath("python.exe") is { } found) python = found;
                if (python is null)
                {
                    python = "python.exe";
                    warnings.Add("Python wasn't found on this PC. Install it from python.org (or the Python install manager) before this schedule runs, or change the program under Advanced.");
                }
                else if (python.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add("This is the Microsoft Store shortcut for Python, which may not work from a schedule. A normal Python install is more reliable.");
                }
                exe = python;
                args = "-u " + quoted + tail;
                encoding = OutputEncoding.Utf8;
                env.Add(new KeyValuePair<string, string>("PYTHONIOENCODING", "utf-8"));
                break;

            case ".js":
                if (probe.FindOnPath("node.exe") is { } node)
                {
                    exe = node;
                    args = quoted + tail;
                    encoding = OutputEncoding.Utf8;
                }
                else
                {
                    exe = CScriptExe;
                    args = "//NoLogo " + quoted + tail;
                    warnings.Add("Node.js wasn't found on this PC, so Windows Script Host (cscript) will run the script. It only understands old-style JScript, not modern JavaScript.");
                }
                break;

            case ".vbs":
                exe = CScriptExe;
                args = "//NoLogo " + quoted + tail;
                break;

            default:
                reject = NotRunnable;
                return null;
        }

        if (target.Contains('%', StringComparison.Ordinal))
            warnings.Add("The file's path contains a % sign. Cadence replaces %NAME% in paths with the value of that variable when the schedule runs.");

        string? folder = workingDir ?? FolderOf(target);
        var run = new RunSpec
        {
            Exe = exe,
            Args = args,
            WorkingDirectory = folder,
            Encoding = encoding,
            Env = new ValueList<KeyValuePair<string, string>>(env),
            SourcePath = source,
        };
        return new DropDraft(name, run, new ValueList<string>(warnings));
    }

    static string FileNameOf(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    static string ExtensionOf(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[dot..].Trim() : "";
    }

    static string? FolderOf(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        if (slash < 0) return null;
        string folder = path[..slash];
        if (folder.Length == 2 && folder[1] == ':') folder += "\\";   // the root of a drive keeps its backslash
        return folder.Length == 0 ? null : folder;
    }
}
