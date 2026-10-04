using Cadence.Core.Execution;
using Cadence.Core.Model;

namespace Cadence.Agent.Tools;

/// <summary>
/// The agent program rules of PLAN 12.5.2 that need the machine: expansion of <c>%VAR%</c> (for the checks only: the job stores the text the agent wrote, like the
/// app), the program's existence (<see cref="IFileProbe"/>), the folders' existence, and the Cadence-reference rules 5, 6 and [CP-R9] on the EXPANDED text. The pure
/// parts (shape, extension, arguments, environment, the Cadence markers) are <see cref="AgentInputRules"/> in Core. Every refusal is a plain sentence and is
/// returned as <c>program_rejected</c> by the save tool; no message ever contains an environment value.
/// <para>
/// Honest limit ([CP-R9]): the Cadence-reference rule is a speed bump. Base64, a script file written elsewhere or a renamed copy of the exe pass a text check; the
/// real mitigations are the owner's tool permissions ([OWNER-Q14]) and [CP-R2]; see <see cref="AgentInputRules"/> for the bypasses Checkpoint D listed.
/// </para>
/// </summary>
internal static class ProgramRules
{
    /// <summary>What the agent gave for the program (null = not given: when changing a job those fields keep their current values and are not checked again).</summary>
    public sealed record Given(string? Path, string? Arguments, string? WorkingFolder, IReadOnlyList<KeyValuePair<string, string>>? Environment)
    {
        public bool Any => Path is not null || Arguments is not null || WorkingFolder is not null || Environment is not null;
    }

    /// <summary>Null when the program is acceptable, otherwise the sentence for <c>program_rejected</c>.</summary>
    /// <param name="given">The fields the call set; only those are checked individually.</param>
    /// <param name="merged">The program as it will be saved (given fields over the current ones); the Cadence rules look at all of it.</param>
    public static string? Check(Given given, RunSpec merged, IFileProbe files, string? ownExePath)
    {
        string expandedPath = Expand(merged.Exe);
        if (given.Path is { } path)
        {
            // The raw text first: a quote or a control character must not be hidden by what a variable expands to.
            if (path.IndexOfAny(ForbiddenInPath) >= 0) return AgentInputRules.CheckProgramShape(path);
            if (AgentInputRules.IsBareName(expandedPath))
            {
                // A path found for the EXPANDED text would show what a variable holds; only a name the agent typed itself gets that help.
                string? found = path.Contains('%', StringComparison.Ordinal) ? null : files.FindOnPath(expandedPath);
                return AgentInputRules.BareNameMessage + (found is null
                    ? ", for example C:\\Windows\\System32\\cmd.exe."
                    : ": the program on your PATH is " + Shown(found) + ".");
            }
            if (AgentInputRules.CheckProgramShape(expandedPath, files) is { } shape) return Unexpanded(shape, path);
        }
        if (given.Arguments is { } args && AgentInputRules.CheckArguments(args) is { } argProblem) return argProblem;
        if (given.Environment is { } env && AgentInputRules.CheckEnvironment(env) is { } envProblem) return envProblem;

        // Rules 5, 6 and [CP-R9] after %VAR% expansion, over everything the saved job will run (also the parts the call did not set when it set any).
        if (given.Any)
        {
            string? cadence = AgentInputRules.CadenceReference(expandedPath, Expand(merged.Args), ownExePath, merged.Env.Select(kv => Expand(kv.Value)));
            if (cadence is not null) return cadence;
        }

        // [RT-1] The messages below name the text the agent GAVE, never what its %VARIABLES% expand to: the value of a variable is the server's own
        // (the agent host's environment) and may be a secret; "C:\%TOKEN%.exe" must not come back as "'C:\<the token>.exe' does not exist".
        if (given.Path is { } rawPath && !files.FileExists(expandedPath))
            return "The program " + Shown(rawPath) + " does not exist on this PC" + (HasVariable(rawPath) ? " once its %VARIABLES% are replaced" : "") + ". Give the full path of an existing .exe.";
        if (given.WorkingFolder is { } wd && CheckFolder(wd, files) is { } folderProblem) return folderProblem;
        return null;
    }

    static readonly char[] ForbiddenInPath = ['\0', '\r', '\n', '"'];

    /// <summary>Rule 4: an absolute local folder that exists (variables are expanded for the check).</summary>
    public static string? CheckFolder(string raw, IFileProbe files)
    {
        if (raw.IndexOfAny(ForbiddenInPath) >= 0) return "The working folder can't contain quotes or control characters.";
        string p = Expand(raw).Trim();
        bool local = p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
        if (!local) return "The working folder must be a full local folder path starting with a drive letter (for example C:\\Users\\Dad\\stocks); network, device and relative paths are not allowed.";
        if (!files.DirectoryExists(p)) return "The working folder " + Shown(raw.Trim()) + " does not exist on this PC" + (HasVariable(raw) ? " once its %VARIABLES% are replaced" : "") + ".";
        return null;
    }

    /// <summary>The folder part of a program path as the agent wrote it ("C:\x\a.exe" is "C:\x", "C:\a.exe" is "C:\"); null when there is none.</summary>
    public static string? FolderOf(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        if (slash < 0) return null;
        string folder = path[..slash];
        if (folder.Length == 2 && folder[1] == ':') folder += "\\";
        return folder.Length == 0 ? null : folder;
    }

    /// <summary>Warnings (never refusals) for a program: variables that expand at run time, a visible console that captures nothing.</summary>
    public static IEnumerable<string> Warnings(RunSpec run)
    {
        if (run.Exe.Contains('%', StringComparison.Ordinal) || run.Args.Contains('%', StringComparison.Ordinal))
            yield return "A %NAME% in the program or its arguments is replaced by the value of that variable each time the job runs, not when it is saved.";
        if (!run.Hidden)
            yield return "This job is visible: a console window appears when it runs, and its output is not captured (no run output in Cadence).";
    }

    static bool HasVariable(string text) => text.Contains('%', StringComparison.Ordinal);

    /// <summary>
    /// [RT-1] A shape message made from the EXPANDED program path. Two of them quote part of it (the interpreter hint for a script, the extension), so when the agent's
    /// text holds a %VARIABLE% they are replaced by their general form: what a variable holds must not reach the agent. Without a variable nothing changes.
    /// </summary>
    static string Unexpanded(string shape, string raw)
    {
        if (!HasVariable(raw)) return shape;
        if (shape.StartsWith("A script can't be the program itself.", StringComparison.Ordinal))
            return "A script can't be the program itself. Use the script's interpreter (for example cmd.exe /d /s /c, powershell.exe -File or py.exe) as the program and the script as its argument.";
        if (shape.StartsWith("The program must be an .exe or .com file;", StringComparison.Ordinal))
            return "The program must be an .exe or .com file; the path you gave does not end in one once its %VARIABLES% are replaced.";
        return shape;
    }

    static string Expand(string text)
    {
        try { return Environment.ExpandEnvironmentVariables(text); }
        catch (ArgumentException) { return text; }
    }

    /// <summary>A path echoed in a message: cleaned of control characters and cut, so a long or hostile string stays one short line.</summary>
    static string Shown(string text)
    {
        var sb = new System.Text.StringBuilder("'");
        foreach (char c in text)
        {
            if (sb.Length >= 200) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.Append('\'').ToString();
    }
}
