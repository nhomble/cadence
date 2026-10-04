using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cadence.Core.Metadata;
using Cadence.Core.TaskXml;

namespace Cadence.Core.Planning;

/// <summary>
/// The revision stamp of a job for optimistic concurrency (PLAN 12.1.3). Computed from the LIVE task, never stored:
/// the first 12 lowercase hex characters of SHA-256 of (UTF-8) <c>Documentation ?? ""</c> + <c>"\n"</c> + <c>Fingerprint.Of(task)</c>.
/// Every Cadence write changes the Documentation (job data, the <c>updated</c> stamp, who wrote it, the lock), every edit made in Task
/// Scheduler changes the fingerprint, so the revision changes on every change from anywhere. It does not change when only run state
/// (last run time, next run) changes. It works for jobs written by v0.1.0 (no stored field is needed).
/// </summary>
public static class JobRevision
{
    public const int Length = 12;

    /// <summary>The revision of a task definition as read from Task Scheduler; the empty-task value when <paramref name="actual"/> is null.</summary>
    public static string Of(TaskDefinitionModel? actual)
    {
        string doc = actual?.RegistrationInfo.Documentation ?? "";
        string fp = actual is null ? "" : Fingerprint.Of(actual);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(doc + "\n" + fp)))[..Length];
    }

    public static string Of(ClassifiedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Of(task.Actual);
    }

    /// <summary>
    /// The message of a refused save whose revision is out of date. Names who changed the job and when, from the LIVE task:
    /// "This job was changed 2 minutes ago by Cadence (the app) after you read it. Read it again and re-apply your change."
    /// A job that was edited in Task Scheduler (its fingerprint no longer matches) says so instead, because that leaves no time or author.
    /// </summary>
    public static string StaleMessage(ClassifiedTask live, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(live);
        const string tail = " after you read it. Read it again and re-apply your change.";
        if (live.Metadata is not { } m) return "This job was changed" + tail;
        if (live.Sync == SyncStatus.Drifted) return "This job was changed in Task Scheduler (outside Cadence)" + tail;
        return "This job was changed " + Ago(m.Updated, now) + " by " + Actor.Parse(m.UpdatedBy).Describe() + tail;
    }

    /// <summary>"just now", "1 minute ago", "2 minutes ago", "3 hours ago", "2 days ago".</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        TimeSpan d = now - at;
        if (d < TimeSpan.FromSeconds(60)) return "just now";
        if (d < TimeSpan.FromHours(1)) return Unit((long)d.TotalMinutes, "minute");
        if (d < TimeSpan.FromDays(1)) return Unit((long)d.TotalHours, "hour");
        return Unit((long)d.TotalDays, "day");

        static string Unit(long n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s") + " ago";
    }
}
