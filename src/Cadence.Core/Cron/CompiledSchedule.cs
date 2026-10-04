using Cadence.Core.Model;

namespace Cadence.Core.Cron;

/// <summary>
/// Result of <see cref="CronCompiler.Compile"/>. On failure (Success = false) Groups and Triggers are empty and
/// Diagnostics holds at least one Error; nothing is approximated.
/// </summary>
public sealed record CompiledSchedule(bool Success, ValueList<ScheduleGroup> Groups, bool AtLogon,
    ValueList<TaskTrigger> Triggers, ValueList<CronDiagnostic> Diagnostics,
    ValueList<string>? ClosestAlternative, string? AlternativeExplanation);

/// <summary>UserId is "DOMAIN\user" for the LogonTrigger.</summary>
public sealed record MapContext(string UserId);
