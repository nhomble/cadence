using System.IO;
using System.Text;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;

namespace Cadence.App.Cli;

/// <summary>
/// The writing and per-job headless commands (<c>--apply --delete --reapply --untrack --run --history</c>). Every write goes through the
/// same Planner and Applier as the UI, so the live ownership checks apply: a task Cadence does not manage is never touched. Output goes
/// to <see cref="CliEnv"/>; the return value is the exit code (0 done, 1 failed).
/// </summary>
public static class JobCommands
{
    public static int Execute(CliCommand cmd, CliEnv env)
    {
        try
        {
            return cmd.Action switch
            {
                CliAction.Apply => Apply(cmd.Target!, env),
                CliAction.Delete => Delete(cmd.Target!, env),
                CliAction.Reapply => Reapply(cmd.Target!, env),
                CliAction.Untrack => Untrack(cmd.Target!, env),
                CliAction.Run => Run(cmd.Target!, env),
                CliAction.History => History(cmd.Target!, cmd.Max, cmd.Json, env),
                _ => Fail(env, "Not a job command: " + cmd.Action),
            };
        }
        catch (SchedulerException ex) { return Fail(env, "Task Scheduler: " + ex.Message); }
    }

    static int Apply(string file, CliEnv env)
    {
        string text;
        try { text = File.ReadAllText(file, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(env, "Cannot read the job file '" + file + "': " + ex.Message);
        }
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(env.Clock(), env.Zone).DateTime);
        if (!JobJson.TryParse(text, today, out JobJson.Parsed? parsed, out string error)) return Fail(env, error);

        var jobs = new ManagedJobs(env.Gateway, env.Clock, Actor.Cli);
        // Same id as a managed job here: update that task (the Planner renames it when the name changed). Otherwise a new job.
        // (If a copy made outside Cadence shares the id, the original is the one that is updated.)
        ClassifiedTask? existing = jobs.Load(false).Where(c => c.Job is not null && c.Job.Id == parsed!.Job.Id).OrderBy(c => c.DuplicateOf is not null).FirstOrDefault();
        JobDefinition job = parsed!.Job;
        // A file without an anchor date means "when it was first saved": applying it again on a later day must not look like a change.
        if (existing?.Job is { } current && !parsed.AnchorGiven)
            job = job with { Schedule = job.Schedule with { Anchor = current.Schedule.Anchor } };
        // A file that says nothing about agentLock leaves the lock of the job it updates as it is (the owner's lock is not undone by a copied file).
        if (existing?.Job is { } locked && !parsed.AgentLockGiven)
            job = job with { AgentLock = locked.AgentLock };
        ApplyResult r = new Applier(jobs).Save(job, existing?.Observed.Name);
        return Report(env, r, "job " + job.Id);
    }

    static int Delete(string id, CliEnv env)
    {
        if (Find(env, id, out ClassifiedTask? c) is { } missing) return missing;
        ApplyResult r = new Applier(new ManagedJobs(env.Gateway, env.Clock, Actor.Cli)).Delete(c!.Observed.Name);
        int rc = Report(env, r, "job " + id);
        if (r.Success)
        {
            bool gone = env.Store.DeleteJob(id);
            env.Line(gone ? "Run history of " + id + " removed." : "Run history of " + id + " could not be removed completely (files in use); it is pruned later.");
        }
        return rc;
    }

    static int Reapply(string id, CliEnv env)
    {
        if (Find(env, id, out ClassifiedTask? c) is { } missing) return missing;
        return Report(env, new Applier(new ManagedJobs(env.Gateway, env.Clock, Actor.Cli)).Reapply(c!.Observed.Name), "job " + id);
    }

    static int Untrack(string id, CliEnv env)
    {
        if (Find(env, id, out ClassifiedTask? c) is { } missing) return missing;
        return Report(env, new Applier(new ManagedJobs(env.Gateway, env.Clock, Actor.Cli)).Untrack(c!.Observed.Name), "job " + id);
    }

    static int Run(string id, CliEnv env)
    {
        if (Find(env, id, out ClassifiedTask? c) is { } missing) return missing;
        if (c!.Ownership != Ownership.Managed) return Fail(env, "Job " + id + " is not managed by this version of Cadence (" + CliFormat.OwnershipName(c.Ownership) + "); it will not be started from here.");
        env.Gateway.Run(c.Observed.Name);
        env.Line("Started " + c.Observed.Path + " (job " + id + "). See the result with: Cadence.exe --history " + id + " --folder " + env.Gateway.Options.FolderName);
        return 0;
    }

    static int History(string id, int max, bool json, CliEnv env)
    {
        if (Find(env, id, out ClassifiedTask? c) is { } missing) return missing;
        JobHistory h = new HistoryService(env.Store).Load(c!, max);
        env.Raw(json ? CliFormat.HistoryJson(h) + "\r\n" : CliFormat.HistoryText(c!.Job!.Name, id, h));
        return 0;
    }

    /// <summary>Finds a job of the managed folder by id. Returns null when found; otherwise the exit code (an error was printed).</summary>
    static int? Find(CliEnv env, string id, out ClassifiedTask? found)
    {
        found = null;
        if (!JobId.IsValid(id)) return Fail(env, "'" + id + "' is not a job id (8 characters of a-z and 0-9). List the ids with: Cadence.exe --probe --folder " + env.Gateway.Options.FolderName);
        found = new ManagedJobs(env.Gateway, env.Clock).Load(false).Where(c => c.Job is not null && c.Job.Id == id).OrderBy(c => c.DuplicateOf is not null).FirstOrDefault();
        if (found is not null) return null;
        return Fail(env, "No job with id " + id + " in " + env.FolderPath + ".");
    }

    static int Report(CliEnv env, ApplyResult r, string what)
    {
        if (r.Success)
        {
            env.Line("OK: " + r.Message + " (" + what + ")");
            return 0;
        }
        env.Line("ERROR: " + r.Message);
        foreach (FieldDiff d in r.Mismatch) env.Line("  " + CliFormat.Plain(d));
        return 1;
    }

    public static int Fail(CliEnv env, string message)
    {
        env.Line("ERROR: " + message);
        return 1;
    }
}
