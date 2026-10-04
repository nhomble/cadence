using Cadence.Core.Model;

namespace Cadence.Core.Planning;

public enum ChangeKind { Create, Update, Rename, Delete, Reapply, Untrack, Upgrade }

/// <summary>
/// One write the Applier can execute. <c>NewXml</c> is the definition to register (null for Delete, and for an Update that changes nothing).
/// <c>Summary</c> is a single English line for the editor footer ("Will update: time 18:00 → 18:30").
/// <c>ExpectedRevision</c> (PLAN 12.1.3) is the <see cref="JobRevision"/> the writer last saw; when set, the Applier refuses the write (Stale)
/// if the live task has another revision. Null = no check (last writer wins).
/// </summary>
public sealed record PlannedChange(ChangeKind Kind, string JobId, string? OldTaskName, string? NewTaskName, string? NewXml, ValueList<FieldDiff> Changes, string Summary,
    string? ExpectedRevision = null);
