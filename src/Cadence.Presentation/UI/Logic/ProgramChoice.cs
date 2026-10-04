using Cadence.Core.Execution;
using Cadence.Core.Model;

namespace Cadence.App.UI.Logic;

/// <summary>
/// What choosing a file (Browse or a drop) means for the job's program card: the program, its arguments and folder, the output encoding and
/// environment it needs, and warnings to show. It is the editor's view of a <see cref="DropDraft"/> (<see cref="From"/>).
/// </summary>
public sealed record ProgramChoice(
    string Exe,
    string Args,
    string? WorkingDirectory,
    OutputEncoding Encoding,
    ValueList<KeyValuePair<string, string>> Env,
    string? SourcePath,
    string? SuggestedName,
    ValueList<string> Warnings)
{
    /// <summary>Converts the Core interpreter's draft into the editor's choice.</summary>
    public static ProgramChoice From(DropDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        RunSpec r = draft.Run;
        return new ProgramChoice(r.Exe, r.Args, r.WorkingDirectory, r.Encoding, r.Env, r.SourcePath, draft.Name, draft.Warnings);
    }
}

/// <summary>Turns a file the user picked or dropped into a <see cref="ProgramChoice"/>, or rejects it with a friendly sentence.</summary>
public interface IProgramInterpreter
{
    ProgramChoice? Interpret(string path, out string? rejectReason);
}

/// <summary>The real interpreter: <see cref="DropInterpreter"/> (PLAN 7.5) over a machine probe. Safe to call from any thread when the probe is.</summary>
public sealed class DropProgramInterpreter(IFileProbe probe) : IProgramInterpreter
{
    public ProgramChoice? Interpret(string path, out string? rejectReason)
    {
        DropDraft? draft = DropInterpreter.Interpret(path, probe, out rejectReason);
        return draft is null ? null : ProgramChoice.From(draft);
    }
}
