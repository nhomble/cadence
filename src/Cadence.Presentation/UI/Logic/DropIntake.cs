using System.Globalization;

namespace Cadence.App.UI.Logic;

/// <summary>One dropped file that Cadence can use, with what it was turned into.</summary>
public sealed record DroppedProgram(string Path, ProgramChoice Choice);

/// <summary>
/// The outcome of a drop of one or more files outside an editor. <see cref="Programs"/> are the usable files in drop order (each becomes a job,
/// one after the other); <see cref="Message"/> is the one sentence to tell the user (what was refused, or that several files are queued), or null.
/// </summary>
public sealed record DropIntakeResult(IReadOnlyList<DroppedProgram> Programs, string? Message, bool AnyRejected);

/// <summary>Pure rules for a multi-file drop (PLAN 7.5: several files give one job each, opened in sequence). No WPF.</summary>
public static class DropIntake
{
    /// <summary>Most files one drop will queue; the rest are ignored (and the message says so).</summary>
    public const int MaxFiles = 10;

    public static DropIntakeResult Process(IReadOnlyList<string> paths, IProgramInterpreter interpreter)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(interpreter);
        var programs = new List<DroppedProgram>();
        var rejections = new List<(string Name, string Reason)>();
        int skipped = Math.Max(0, paths.Count - MaxFiles);
        foreach (string path in paths.Take(MaxFiles))
        {
            ProgramChoice? choice = interpreter.Interpret(path, out string? reject);
            if (choice is null) rejections.Add((EditorFormat.FileName(path), reject ?? "Cadence can't run that file."));
            else programs.Add(new DroppedProgram(path, choice));
        }
        return new DropIntakeResult(programs, Compose(programs.Count, rejections, skipped), rejections.Count > 0);
    }

    static string? Compose(int usable, List<(string Name, string Reason)> rejections, int skipped)
    {
        var parts = new List<string>();
        if (usable == 0 && rejections.Count == 1)
            return rejections[0].Reason;
        foreach ((string name, string reason) in rejections)
            parts.Add("Skipped " + name + ": " + reason);
        if (usable > 1)
            parts.Add(usable.ToString(CultureInfo.InvariantCulture) + " files can be used. Cadence sets them up one at a time, starting with this one.");
        if (skipped > 0)
            parts.Add("Only the first " + MaxFiles.ToString(CultureInfo.InvariantCulture) + " files were used.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
