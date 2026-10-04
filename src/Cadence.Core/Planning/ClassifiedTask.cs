using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;

namespace Cadence.Core.Planning;

/// <summary>
/// The result of <see cref="Classifier.Classify"/>. <c>Actual</c> is the parsed live definition (null when the XML could not be parsed),
/// <c>Metadata</c>/<c>Job</c> are set for Managed tasks and for NewerVersion tasks whose metadata still decodes.
/// <c>DriftDiffs</c> lists what is different when the task is Drifted (Before = what Cadence wrote, After = what Task Scheduler holds now).
/// <c>Problem</c> is one English sentence for the UI (null when InSync or Foreign).
/// <c>DuplicateOf</c> is set (by <see cref="DuplicateJobIds.Resolve"/>) on a managed task that shares its job id with an older task: it holds that original's name.
/// </summary>
public sealed record ClassifiedTask(ObservedTask Observed, Ownership Ownership, SyncStatus Sync,
    TaskDefinitionModel? Actual, CadenceMetadata? Metadata, JobDefinition? Job, ValueList<FieldDiff> DriftDiffs, string? Problem, string? DuplicateOf = null);
