using Cadence.Core.Holidays;
using Cadence.Core.Model;

namespace Cadence.Core.Metadata;

/// <summary>
/// The holiday list embedded in a task (`run.skip`): the calendar, the last covered day, and the observed dates with names.
/// The wrapper looks up the local date at wrapper start in this list (PLAN 3.7).
/// </summary>
public sealed record SkipList(HolidayCalendar Calendar, DateOnly Until, ValueList<Holiday> Dates);

/// <summary>
/// Decoded `cadence/1` metadata (PLAN 3.9). <c>Job</c> carries everything the user configured; <c>Skip</c> is the embedded holiday list
/// (null when the job does not skip holidays). <c>Created</c>/<c>Updated</c> have whole-second resolution (the JSON has no fraction).
/// </summary>
public sealed record CadenceMetadata(
    int Schema,                         // JSON format version (1)
    int Gen,                            // TaskComposer.GeneratorVersion that produced the task
    int Wv,                             // WrapperScript.Version the task's action uses
    JobDefinition Job,
    SkipList? Skip,
    string Fp,                          // fingerprint of the task definition this metadata lives in
    DateTimeOffset Created,
    DateTimeOffset Updated,
    ValueList<string> Prev,             // previous task names (renames), oldest first
    string? CreatedBy = null,           // PLAN 12.4.1: Actor token of the writer that created the job (null = unknown, e.g. written by v0.1.0)
    string? UpdatedBy = null);          // Actor token of the writer of the last change (null = unknown)
