namespace Cadence.Core.Execution;

/// <summary>
/// [AG-P21] The pure shape rules of PLAN 12.5.2 (rules 1, 2, 3, 6 and 7), the other half of <see cref="AgentInputRules"/>: no IO, plain messages,
/// shared by the save tool and its tests. Rules 4 (the folders exist) and the existence of the program need the machine and live in the agent project.
/// </summary>
public static partial class AgentInputRules
{
    public const int MaxArgumentsLength = 8000;
    public const int MaxEnvironmentEntries = 30;
    public const int MaxEnvironmentValueLength = 2000;
    public const int MaxEnvironmentNameLength = 100;

    /// <summary>The start of the message for a program given without a folder ("python.exe"); the tool appends the full path found on PATH, when there is one.</summary>
    public const string BareNameMessage = "Task Scheduler does not use your PATH. Give the full path of the program";

    const string Example = "C:\\Windows\\System32\\cmd.exe";

    /// <summary>True for a name without any folder part ("python.exe"): no backslash, slash or drive colon.</summary>
    public static bool IsBareName(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(['\\', '/', ':']) < 0;

    /// <summary>
    /// Rules 1-3 on the (already %VAR%-expanded) program path: no NUL, CR, LF or quote; an absolute local path (<c>C:\...</c>: no UNC, no device path,
    /// no relative path, no bare name); the extension is .exe or .com (a script or shortcut gets the interpreter form from <see cref="HintFor"/>; <paramref name="probe"/>, when given, is asked where Python or Node are).
    /// Null when fine, otherwise the plain message. Existence is the caller's business (it needs the machine).
    /// </summary>
    public static string? CheckProgramShape(string? expandedPath, IFileProbe? probe = null)
    {
        if (string.IsNullOrWhiteSpace(expandedPath)) return "The program path is empty. Give the full path of an existing .exe, for example " + Example + ".";
        foreach (char c in expandedPath)
            if (c is '\0' or '\r' or '\n') return "The program path contains a control character (NUL or a line break).";
        if (expandedPath.Contains('"', StringComparison.Ordinal)) return "The program path can't contain a quote character; give the plain path without quotes.";
        string p = expandedPath.Trim();
        if (IsBareName(p)) return BareNameMessage + ", for example " + Example + ".";
        if (p.StartsWith("\\\\?\\", StringComparison.Ordinal) || p.StartsWith("\\\\.\\", StringComparison.Ordinal) || p.StartsWith("//?/", StringComparison.Ordinal) || p.StartsWith("//./", StringComparison.Ordinal))
            return "Device paths (starting with \\\\?\\ or \\\\.\\) are not allowed. Give a normal full path such as " + Example + ".";
        if (p.StartsWith("\\\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal))
            return "Network paths (\\\\server\\share) are not allowed: the program must be on this PC's local disk. Give a full path such as " + Example + ".";
        if (!(p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\'))
            return "The program path must be a full local path starting with a drive letter and a backslash (for example " + Example + "); a relative path can't be used.";

        string ext = ExtensionOf(p);
        if (ext is ".exe" or ".com") return null;
        if (ext is ".lnk") return "A shortcut (.lnk) can't be used. Give the full path of the program it points to.";
        if (ext is ".bat" or ".cmd" or ".ps1" or ".py" or ".js" or ".vbs")
            return "A script can't be the program itself. Use " + HintFor(p, probe) + ".";
        return "The program must be an .exe or .com file; " + (ext.Length == 0 ? "a file without an extension" : "'" + ext + "'") + " is not allowed.";
    }

    /// <summary>
    /// The interpreter form for a script, from <see cref="DropInterpreter"/>'s table (PLAN 7.5), for example
    /// <c>%SystemRoot%\System32\cmd.exe with arguments /d /s /c ""C:\x\run.bat""</c>. <paramref name="probe"/> decides between Python locations and Node or
    /// Windows Script Host; without one nothing on the machine is looked at.
    /// </summary>
    public static string HintFor(string expandedPathWithScriptExtension, IFileProbe? probe = null)
    {
        DropDraft? draft = DropInterpreter.Interpret(expandedPathWithScriptExtension, probe ?? NoMachine.Instance, out string? reason);
        if (draft is null) return reason ?? "an interpreter such as cmd.exe or powershell.exe with the script as its argument";
        return draft.Run.Exe + " with arguments " + draft.Run.Args;
    }

    /// <summary>Rule 6 on the arguments: at most 8000 characters, no NUL, and no line breaks (agents put each command in a script file).</summary>
    public static string? CheckArguments(string? args)
    {
        if (string.IsNullOrEmpty(args)) return null;
        if (args.Length > MaxArgumentsLength) return "The arguments are longer than " + MaxArgumentsLength + " characters.";
        foreach (char c in args)
        {
            if (c == '\0') return "The arguments contain a NUL character.";
            if (c is '\r' or '\n') return "The arguments can't contain line breaks; put each command in a script file and run that.";
        }
        return null;
    }

    /// <summary>
    /// Rule 7 on the environment: at most 30 entries; names match <c>[A-Za-z_][A-Za-z0-9_.()-]{0,99}</c>; values at most 2000 characters without NUL; no name twice
    /// (ignoring case). A message names a variable only when its name is a valid one, and it NEVER contains a value (values are often secrets).
    /// </summary>
    public static string? CheckEnvironment(IReadOnlyList<KeyValuePair<string, string>>? env)
    {
        if (env is null || env.Count == 0) return null;
        if (env.Count > MaxEnvironmentEntries) return "At most " + MaxEnvironmentEntries + " environment variables are allowed, but " + env.Count + " were given.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < env.Count; i++)
        {
            string name = env[i].Key, value = env[i].Value;
            if (!ValidEnvironmentName(name))
                return "Environment variable name number " + (i + 1) + " is not valid: use letters, digits and _ . ( ) - only, starting with a letter or _, at most " + MaxEnvironmentNameLength + " characters (no '=').";
            if (!seen.Add(name)) return "The environment variable " + name + " is given twice (names are not case-sensitive).";
            if (value.Length > MaxEnvironmentValueLength) return "The value of the environment variable " + name + " is longer than " + MaxEnvironmentValueLength + " characters.";
            if (value.Contains('\0', StringComparison.Ordinal)) return "The value of the environment variable " + name + " contains a NUL character.";
        }
        return null;
    }

    public static bool ValidEnvironmentName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxEnvironmentNameLength) return false;
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '(' or ')' or '-')) return false;
        return true;
    }

    /// <summary>The lower-case extension of the last path segment, trailing dots and blanks ignored ("run.BAT. " is ".bat"); "" when there is none.</summary>
    static string ExtensionOf(string path)
    {
        string p = path.TrimEnd(' ', '.');
        int slash = p.LastIndexOfAny(['\\', '/']);
        string leaf = slash >= 0 ? p[(slash + 1)..] : p;
        int dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[dot..].ToLowerInvariant() : "";
    }

    sealed class NoMachine : IFileProbe
    {
        public static readonly NoMachine Instance = new();
        public bool FileExists(string path) => false;
        public string? FindOnPath(string exeName) => null;
        public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir) { args = null; workingDir = null; return null; }
    }
}
