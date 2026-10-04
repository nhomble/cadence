using System.IO;
using Cadence.Core.Execution;

namespace Cadence.App.UI.Logic;

/// <summary>What "Open folder" shows in Explorer: the file, and which kind of file it is.</summary>
public enum RevealKind { Program, Script, Shortcut }

/// <summary>The file to highlight. <see cref="Path"/> is as stored (it may hold %VAR%).</summary>
public sealed record RevealTarget(string Path, RevealKind Kind);

/// <summary>
/// Which file a job's "Open folder" / "Copy path" mean, the command line the program card shows, and the "Open folder" flow itself. Pure: no WPF, no
/// Explorer (the shell is a delegate), the machine is asked through <see cref="IFileProbe"/>. A script is recognised only in the interpreter forms
/// <see cref="ScriptOf"/> lists; for anything else the program itself is the file (never a guess from some argument that merely looks like a path).
/// </summary>
public static class ProgramReveal
{
    /// <summary>The file for a job: the dropped shortcut, else the script an interpreter runs, else the program.</summary>
    public static RevealTarget? Resolve(string? exe, string? args, string? sourcePath)
    {
        if (sourcePath is { Length: > 0 } src && Extension(src).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return new RevealTarget(src.Trim().Trim('"'), RevealKind.Shortcut);
        if (ScriptOf(exe ?? "", args ?? "") is { } script) return new RevealTarget(script, RevealKind.Script);
        string e = (exe ?? "").Trim().Trim('"');
        return e.Length == 0 ? null : new RevealTarget(e, RevealKind.Program);
    }

    /// <summary>
    /// The script an interpreter runs, only in these forms (program name without .exe, any case): <c>py</c>, <c>pyw</c>, <c>python</c>, <c>pythonw</c>
    /// with options then a .py/.pyw/.pyz file (not -c or -m); <c>node</c> with options then a .js/.mjs/.cjs file; <c>powershell</c> and <c>pwsh</c> with
    /// <c>-File x.ps1</c> (or <c>-f</c>); <c>cmd</c> with <c>/c "x.bat" ...</c> (also <c>/d /s /c ""x.bat" ..."</c>); <c>cscript</c> and <c>wscript</c>
    /// with a .vbs/.vbe/.js/.jse/.wsf file after the <c>//</c> options. The path must be rooted or start with a %VAR%. Null for everything else.
    /// </summary>
    public static string? ScriptOf(string exe, string args)
    {
        string name = EditorFormat.FileName(exe).ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        string a = (args ?? "").Trim();
        string? script = name switch
        {
            "py" or "pyw" or "python" or "pythonw" => FirstOperand(a, ["-c", "-m"], [".py", ".pyw", ".pyz"], optionPrefix: "-"),
            "node" => FirstOperand(a, [], [".js", ".mjs", ".cjs"], optionPrefix: "-"),
            "cscript" or "wscript" => FirstOperand(a, [], [".vbs", ".vbe", ".js", ".jse", ".wsf"], optionPrefix: "//"),
            "powershell" or "pwsh" => AfterSwitch(a, ["-file", "-f"], [".ps1"]),
            "cmd" => BatchOf(a),
            _ => null,
        };
        return script is not null && IsUsablePath(script) ? script : null;
    }

    /// <summary>The first argument that is not an option; it must have one of <paramref name="extensions"/>. Null when an option in <paramref name="stop"/> comes first.</summary>
    static string? FirstOperand(string args, string[] stop, string[] extensions, string optionPrefix)
    {
        foreach (string arg in CommandLine.Split(args))
        {
            if (stop.Contains(arg, StringComparer.OrdinalIgnoreCase)) return null;
            if (arg.StartsWith(optionPrefix, StringComparison.Ordinal)) continue;
            return extensions.Contains(Extension(arg), StringComparer.OrdinalIgnoreCase) ? arg : null;
        }
        return null;
    }

    /// <summary>The argument right after one of <paramref name="switches"/> (PowerShell's <c>-File</c>), when it has one of <paramref name="extensions"/>.</summary>
    static string? AfterSwitch(string args, string[] switches, string[] extensions)
    {
        IReadOnlyList<string> parts = CommandLine.Split(args);
        for (int i = 0; i + 1 < parts.Count; i++)
        {
            if (!switches.Contains(parts[i], StringComparer.OrdinalIgnoreCase)) continue;
            return extensions.Contains(Extension(parts[i + 1]), StringComparer.OrdinalIgnoreCase) ? parts[i + 1] : null;
        }
        return null;
    }

    /// <summary>The batch file of <c>/d /s /c ""x.bat" args"</c> (the line Cadence writes) or <c>/c "x.bat" args</c>.</summary>
    static string? BatchOf(string args)
    {
        string rest = args;
        bool sawC = false;
        while (rest.Length > 0)
        {
            int space = rest.IndexOf(' ', StringComparison.Ordinal);
            string word = space < 0 ? rest : rest[..space];
            if (word.Length == 0 || word[0] != '/') break;
            rest = space < 0 ? "" : rest[(space + 1)..].TrimStart();
            if (word.Equals("/c", StringComparison.OrdinalIgnoreCase)) { sawC = true; break; }
            if (!word.Equals("/d", StringComparison.OrdinalIgnoreCase) && !word.Equals("/s", StringComparison.OrdinalIgnoreCase)) return null;   // an option we don't know: don't guess
        }
        if (!sawC || rest.Length == 0) return null;
        if (rest.StartsWith("\"\"", StringComparison.Ordinal)) rest = rest[1..];   // the outer pair of the /s form
        string file;
        if (rest[0] == '"')
        {
            int close = rest.IndexOf('"', 1);
            if (close < 0) return null;
            file = rest[1..close];
        }
        else
        {
            int space = rest.IndexOf(' ', StringComparison.Ordinal);
            file = space < 0 ? rest : rest[..space];
        }
        return Extension(file).Equals(".bat", StringComparison.OrdinalIgnoreCase) || Extension(file).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ? file : null;
    }

    /// <summary>A full path (<c>C:\x\a.py</c>, <c>\\server\share\a.py</c>) or one that starts with a %VAR%; a bare or relative name says nothing about where the file is.</summary>
    static bool IsUsablePath(string path) => IsRooted(path) || path.StartsWith('%');

    static bool IsRooted(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') || path.StartsWith(@"\\", StringComparison.Ordinal);

    static string Extension(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        int dot = path.LastIndexOf('.');
        return dot > slash && dot >= 0 ? path[dot..] : "";
    }

    /// <summary>The program and its arguments as one line, as stored (nothing expanded): <c>"C:\Program Files\x\a.exe" --flag</c>.</summary>
    public static string CommandText(string? exe, string? args)
    {
        string e = (exe ?? "").Trim().Trim('"');
        string line = e.Length == 0 ? "" : CommandLine.Quote(e);
        string a = (args ?? "").Trim();
        return a.Length == 0 ? line : line.Length == 0 ? a : line + " " + a;
    }

    /// <summary>The nearest folder above <paramref name="path"/> that exists (the path's own folder first), or null.</summary>
    public static string? NearestExistingFolder(string path, Func<string, bool> directoryExists)
    {
        string? dir = SafeParent(path);
        while (dir is not null)
        {
            if (directoryExists(dir)) return dir;
            dir = SafeParent(dir);
        }
        return null;
    }

    static string? SafeParent(string path)
    {
        try { return Path.GetDirectoryName(path.TrimEnd('\\', '/')); }
        catch (ArgumentException) { return null; }
    }

    public const string NotFoundToast = "File not found; opened its folder";

    /// <summary>The path the target stands for once %VAR% is expanded, or null when that is not a full path (a bare program name found through PATH).</summary>
    public static string? ExpandedPath(RevealTarget target)
    {
        string p;
        try { p = Environment.ExpandEnvironmentVariables(target.Path); }
        catch (ArgumentException) { return null; }
        return IsRooted(p) ? p : null;
    }

    /// <summary>
    /// "Open folder": Explorer with the file highlighted. When the file is gone the nearest existing folder opens and the toast says so; when nothing
    /// can be opened the toast says why. <paramref name="revealFile"/> and <paramref name="openFolder"/> return false when Explorer couldn't be asked.
    /// </summary>
    public static void Reveal(string? exe, string? args, string? sourcePath, IFileProbe probe, Func<string, bool> revealFile, Func<string, bool> openFolder, Action<string> toast)
    {
        RevealTarget? target = Resolve(exe, args, sourcePath);
        if (target is null) { toast("No program is set yet."); return; }
        string? path = ExpandedPath(target);
        if (path is null) { toast("Cadence can't tell where " + EditorFormat.FileName(target.Path) + " is: the schedule gives no folder for it."); return; }
        if (probe.FileExists(path))
        {
            if (!revealFile(path)) toast("Couldn't open the folder.");
            return;
        }
        string? folder = NearestExistingFolder(path, probe.DirectoryExists);
        if (folder is null) { toast("File not found, and neither is its folder."); return; }
        toast(openFolder(folder) ? NotFoundToast : "Couldn't open the folder.");
    }
}
