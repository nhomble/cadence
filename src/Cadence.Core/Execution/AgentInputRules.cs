namespace Cadence.Core.Execution;

/// <summary>
/// The pure agent input rules that need no IO (PLAN 12.5.2 rules 5 and 6, [CP-R9]): an AI agent may not put Cadence itself, or a command
/// that changes Cadence's settings, into a job. The caller expands <c>%VAR%</c> first (that needs the environment) and passes the expanded
/// text; Phase 21's save pipeline calls <see cref="CadenceReference"/> and refuses with <c>program_rejected</c>.
/// <para>
/// This is a SPEED BUMP, not a boundary: base64, a script file the agent writes elsewhere, a renamed copy of the exe or a variable that is
/// assembled at run time all get past a text check ([CP-D] seen in Checkpoint D: cmd's <c>%VAR:~0,0%</c> substring form, which the save-time
/// expansion leaves alone, hides any marker; PowerShell <c>-EncodedCommand</c>; an 8.3 short name of the exe; <c>--apply</c> and <c>--delete</c> are not
/// markers). The real mitigations are the owner's tool-permission rules ([OWNER-Q14]), [CP-R2] (a switch cleared outside Cadence is detected and
/// shown), and the README's honest limit: a job an agent schedules runs as the owner and can change any Cadence setting.
/// </para>
/// </summary>
public static partial class AgentInputRules
{
    /// <summary>The message for every refusal of this rule.</summary>
    public const string CadenceReferenceMessage = "Agents can't schedule Cadence itself or change its settings.";

    // Matched case-insensitively against the text with quotes, carets and backticks removed and '/' turned into '\'.
    static readonly string[] Markers = ["--agents", "--mcp", "cadence.exe", "cadence-standalone.exe", "software\\cadence"];

    /// <summary>
    /// True when the (already %VAR%-expanded) program path names Cadence itself: it equals <paramref name="runningProcessPath"/> (the exe that is
    /// running this code), or its file name starts with "Cadence" and ends with ".exe" or ".com" (trailing dots and blanks are ignored, as Windows does).
    /// </summary>
    public static bool IsCadenceProgram(string? expandedPath, string? runningProcessPath)
    {
        if (string.IsNullOrWhiteSpace(expandedPath)) return false;
        string p = Normalize(expandedPath).TrimEnd(' ', '.');
        if (!string.IsNullOrWhiteSpace(runningProcessPath) && string.Equals(p, Normalize(runningProcessPath).TrimEnd(' ', '.'), StringComparison.OrdinalIgnoreCase))
            return true;
        int cut = p.LastIndexOf('\\');
        string leaf = cut >= 0 ? p[(cut + 1)..] : p;
        return leaf.StartsWith("Cadence", StringComparison.OrdinalIgnoreCase)
            && (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the (already expanded) text contains <c>--agents</c>, <c>--mcp</c>, <c>Cadence.exe</c>, <c>Cadence-standalone.exe</c> or <c>Software\Cadence</c>.</summary>
    public static bool MentionsCadence(string? expandedText)
    {
        if (string.IsNullOrEmpty(expandedText)) return false;
        string t = Normalize(expandedText);
        foreach (string m in Markers)
            if (t.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The one check Phase 21 calls: null when fine, otherwise <see cref="CadenceReferenceMessage"/>. <paramref name="environmentValues"/> are the values of the
    /// job's environment variables (they can carry a command line that the arguments expand to at run time).
    /// </summary>
    public static string? CadenceReference(string? expandedPath, string? expandedArguments, string? runningProcessPath, IEnumerable<string>? environmentValues = null)
    {
        if (IsCadenceProgram(expandedPath, runningProcessPath) || MentionsCadence(expandedPath) || MentionsCadence(expandedArguments)) return CadenceReferenceMessage;
        if (environmentValues is not null)
            foreach (string v in environmentValues)
                if (MentionsCadence(v)) return CadenceReferenceMessage;
        return null;
    }

    static string Normalize(string text) =>
        text.Replace("\"", "", StringComparison.Ordinal).Replace("^", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Replace('/', '\\');
}
