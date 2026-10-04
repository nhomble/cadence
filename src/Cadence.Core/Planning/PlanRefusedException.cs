namespace Cadence.Core.Planning;

/// <summary>Why a plan was refused (the Applier maps it to an <c>ApplyFailure</c>).</summary>
public enum PlanRefusalKind { Refused, NotFound, Stale, Locked }

/// <summary>
/// Thrown by the <see cref="Planner"/> when the requested change must never happen: anything on a Foreign task, anything but Delete on a
/// CorruptMetadata task, anything on a NewerVersion task. The message is plain English for the UI.
/// </summary>
public sealed class PlanRefusedException : InvalidOperationException
{
    public PlanRefusedException(string message, PlanRefusalKind kind = PlanRefusalKind.Refused) : base(message) => Kind = kind;

    public PlanRefusalKind Kind { get; }
}
