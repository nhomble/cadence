namespace Cadence.Core.History;

/// <summary>
/// What the editor shows above the run list.
/// <para><see cref="LastResult"/>: status of the newest NON-skipped run (null when there is none). The UI shows "Skipped (holiday)" in grey when the
/// newest row of <see cref="RunHistory.Recent"/> is a skip.</para>
/// <para><see cref="LastRunAt"/>: wrapper start of the newest run of any kind, skipped included.</para>
/// <para><see cref="AverageRuntime"/>: mean duration of the Ok and Failed runs in the window (timeouts and launch errors excluded); null when there are none.</para>
/// <para><see cref="SuccessRate"/>: a FRACTION from 0.0 to 1.0 (multiply by 100 for %), Ok / (Ok + Failed + TimedOut + LaunchError + NotReported)
/// over the window; null when no counted run exists (no runs, or only skipped / running ones).</para>
/// <para><see cref="SampleSize"/>: how many runs the success rate is based on.</para>
/// </summary>
public sealed record JobSummary(RunStatus? LastResult, DateTimeOffset? LastRunAt, TimeSpan? AverageRuntime, double? SuccessRate, int SampleSize)
{
    public static JobSummary Empty { get; } = new(null, null, null, null, 0);
}
