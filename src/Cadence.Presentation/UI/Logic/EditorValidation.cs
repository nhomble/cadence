using System.Globalization;
using Cadence.Core.Metadata;
using Cadence.Core.Model;

namespace Cadence.App.UI.Logic;

public enum ProblemSeverity { Error, Warning }

/// <summary>A message under one field of the editor. Errors block Save; warnings don't. <see cref="Field"/> is Name, Program, Args, WorkingDirectory, Timeout or Env.</summary>
public sealed record EditorProblem(string Field, string Message, ProblemSeverity Severity)
{
    public bool IsError => Severity == ProblemSeverity.Error;
}

/// <summary>
/// The checks the editor makes before Save (PLAN 3.9 [CP-A], mirroring <see cref="MetadataCodec"/>'s encode-side validation) so a bad value is
/// explained next to its field and the editor never relies on catching a <c>ComposeException</c>. Plus two warnings about relative paths.
/// </summary>
public static class EditorValidation
{
    public const int MaxNameLength = 100;

    public static IReadOnlyList<EditorProblem> Validate(JobDefinition job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var list = new List<EditorProblem>();
        RunSpec run = job.Run;

        string name = job.Name ?? "";
        if (name.Trim().Length == 0) list.Add(Error("Name", "Give the schedule a name."));
        else if (name.Length > MaxNameLength)
            list.Add(Error("Name", "The name can be at most " + MaxNameLength.ToString(CultureInfo.InvariantCulture) + " characters (it is " + name.Length.ToString(CultureInfo.InvariantCulture) + ")."));
        else if (HasNul(name)) list.Add(Error("Name", "The name can't contain NUL characters."));

        if (string.IsNullOrWhiteSpace(run.Exe)) list.Add(Error("Program", "Choose the program to run."));
        else if (HasNul(run.Exe)) list.Add(Error("Program", "The program path can't contain NUL characters."));
        else if (IsRelativeWithFolder(run.Exe))
            list.Add(Warn("Program", "Use a full path: a relative path is resolved against C:\\Windows\\System32 when the job runs."));

        if (HasNul(run.Args)) list.Add(Error("Args", "The arguments can't contain NUL characters."));

        if (HasNul(run.WorkingDirectory)) list.Add(Error("WorkingDirectory", "The working folder can't contain NUL characters."));
        else if (!string.IsNullOrWhiteSpace(run.WorkingDirectory) && IsRelative(run.WorkingDirectory))
            list.Add(Warn("WorkingDirectory", "Use a full path: a relative folder is resolved against C:\\Windows\\System32 when the job runs."));

        if (run.Timeout is { } t && t.TotalSeconds > MetadataCodec.MaxTimeoutSeconds)
            list.Add(Error("Timeout", "The timeout can be at most 24 days."));
        else if (run.Timeout is { } t0 && t0 <= TimeSpan.Zero)
            list.Add(Error("Timeout", "The timeout must be longer than zero. Choose \"No limit\" for none."));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> kv in run.Env)
        {
            if (string.IsNullOrEmpty(kv.Key)) list.Add(Error("Env", "An environment variable needs a name."));
            else if (kv.Key.Contains('=', StringComparison.Ordinal)) list.Add(Error("Env", "The name '" + kv.Key + "' can't contain '='."));
            else if (HasNul(kv.Key) || HasNul(kv.Value)) list.Add(Error("Env", "Environment variables can't contain NUL characters."));
            else if (!seen.Add(kv.Key)) list.Add(Error("Env", "The variable '" + kv.Key + "' is listed twice."));
        }
        return list;
    }

    static EditorProblem Error(string field, string message) => new(field, message, ProblemSeverity.Error);

    static EditorProblem Warn(string field, string message) => new(field, message, ProblemSeverity.Warning);

    static bool HasNul(string? s) => s is not null && s.Contains('\0', StringComparison.Ordinal);

    /// <summary>A path that is neither rooted (C:\x, \\server\x, \x) nor starts with a %VARIABLE% (the wrapper expands those).</summary>
    public static bool IsRelative(string path)
    {
        string p = path.Trim().Trim('"');
        if (p.Length == 0) return false;
        if (p.StartsWith('%')) return false;
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':') return p.Length < 3 || (p[2] != '\\' && p[2] != '/');
        return !(p.StartsWith('\\') || p.StartsWith('/'));
    }

    /// <summary>A relative program path with a folder part (<c>.\run.bat</c>, <c>tools\x.exe</c>). A bare name such as <c>node.exe</c> is found on PATH and is fine.</summary>
    public static bool IsRelativeWithFolder(string exe)
    {
        string p = exe.Trim().Trim('"');
        return IsRelative(p) && p.IndexOfAny(['\\', '/']) >= 0;
    }
}
