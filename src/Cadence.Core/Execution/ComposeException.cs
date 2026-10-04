using Cadence.Core.Cron;
using Cadence.Core.Model;

namespace Cadence.Core.Execution;

/// <summary>Thrown by <see cref="TaskComposer"/> when a task cannot be built (the cron does not compile, a name or path is unsafe, the metadata is too big).</summary>
public sealed class ComposeException : Exception
{
    public ComposeException(string message, ValueList<CronDiagnostic>? diagnostics = null, Exception? inner = null) : base(message, inner)
    {
        Diagnostics = diagnostics ?? ValueList<CronDiagnostic>.Empty;
    }

    /// <summary>The compiler's diagnostics when the cron failed (empty otherwise).</summary>
    public ValueList<CronDiagnostic> Diagnostics { get; }
}
