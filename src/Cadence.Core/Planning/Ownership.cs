namespace Cadence.Core.Planning;

/// <summary>
/// Who owns a task. Foreign tasks (no Cadence marker) and NewerVersion tasks are read-only under every code path;
/// CorruptMetadata tasks can only be deleted.
/// </summary>
public enum Ownership { Managed, Foreign, CorruptMetadata, NewerVersion }

/// <summary>How a Managed task relates to what Cadence would write today. NotApplicable for everything that is not Managed.</summary>
public enum SyncStatus { InSync, Drifted, Outdated, NotApplicable }
