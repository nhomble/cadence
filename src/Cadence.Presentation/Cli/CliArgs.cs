using System.Globalization;
using System.Text;

namespace Cadence.App.Cli;

/// <summary>What the command line asks for. <see cref="Gui"/> (no arguments) opens the window; everything else is headless.</summary>
public enum CliAction { Gui, Help, SelfTest, Probe, Apply, Delete, Reapply, Untrack, Run, History, MeasureStartup, Mcp, Agents }

/// <summary>A parsed command line (PLAN 7.6). <c>Target</c> is the job file (apply) or the job id; <c>Folder</c> is already validated.
/// For <c>--agents</c>, <c>Verb</c> is status, suspend, resume, config, log, lock or unlock; <c>MinInterval</c> and <c>MaxJobs</c> are the validated values of <c>--agents config</c>.</summary>
public sealed record CliCommand(CliAction Action, string? Target, string Folder, bool All, bool Yes, int Max, bool Json,
    string? Verb = null, int? MinInterval = null, int? MaxJobs = null)
{
    public const int DefaultMax = 20;
    public const int MaxMax = 500;

    /// <summary>True for the switches that change Task Scheduler (they refuse to run without --yes).</summary>
    public bool Writes => Action is CliAction.Apply or CliAction.Delete or CliAction.Reapply or CliAction.Untrack;
}

/// <summary>Parse result: exactly one of <see cref="Command"/> and <see cref="Error"/> is set.</summary>
public sealed record CliParseResult(CliCommand? Command, string? Error);

/// <summary>
/// The only two managed folders Cadence may ever be pointed at (PLAN 8.0 / Checkpoint B). The folder decides what Cadence may write and
/// delete, so a typo or a hostile value must never reach <c>\Microsoft</c> or another product's folder.
/// </summary>
public static class FolderPolicy
{
    public const string Product = Cadence.Scheduler.ManagedFolder.Product;
    public const string Test = Cadence.Scheduler.ManagedFolder.Test;

    /// <summary>The canonical spelling of an allowed folder name (case-insensitive match), or false. [Review R] One rule: <see cref="Cadence.Scheduler.ManagedFolder"/>.</summary>
    public static bool TryNormalize(string? text, out string folder) => Cadence.Scheduler.ManagedFolder.TryNormalize(text, out folder);

    /// <summary>The folder for the value of <c>CADENCE_FOLDER</c>: unset or blank gives Cadence; an invalid value is an error text.</summary>
    public static string? FromEnvironment(string? value, out string folder)
    {
        folder = Product;
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TryNormalize(value.Trim(), out folder) ? null : "CADENCE_FOLDER must be Cadence or CadenceTest, not '" + value + "'.";
    }
}

public static class CliParser
{
    static readonly Dictionary<string, CliAction> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--help"] = CliAction.Help, ["-h"] = CliAction.Help, ["-?"] = CliAction.Help, ["/?"] = CliAction.Help,
        ["--selftest"] = CliAction.SelfTest,
        ["--probe"] = CliAction.Probe,
        ["--apply"] = CliAction.Apply,
        ["--delete"] = CliAction.Delete,
        ["--reapply"] = CliAction.Reapply,
        ["--untrack"] = CliAction.Untrack,
        ["--run"] = CliAction.Run,
        ["--history"] = CliAction.History,
        ["--measure-startup"] = CliAction.MeasureStartup,
        ["--mcp"] = CliAction.Mcp,
        ["--agents"] = CliAction.Agents,
    };

    /// <summary>The verbs of <c>--agents</c> (PLAN 12.6).</summary>
    public static readonly string[] AgentsVerbs = ["status", "suspend", "resume", "config", "log", "lock", "unlock"];

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0) return new(new CliCommand(CliAction.Gui, null, FolderPolicy.Product, false, false, CliCommand.DefaultMax, false), null);

        CliAction? action = null;
        string? folder = null;
        bool all = false, yes = false, json = false, haveMax = false;
        int max = CliCommand.DefaultMax;
        int? minInterval = null, maxJobs = null;
        var positional = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? inline = null;
            int eq = a.StartsWith("--", StringComparison.Ordinal) ? a.IndexOf('=', StringComparison.Ordinal) : -1;
            string key = a;
            if (eq > 0) { key = a[..eq]; inline = a[(eq + 1)..]; }

            if (Switches.TryGetValue(key, out CliAction act) && inline is null)
            {
                if (action is not null) return Fail("Only one command per run: found both '" + Name(action.Value) + "' and '" + a + "'.");
                action = act;
            }
            else if (string.Equals(key, "--folder", StringComparison.OrdinalIgnoreCase))
            {
                if (folder is not null) return Fail("--folder was given twice.");
                string? v = inline ?? (i + 1 < args.Count ? args[++i] : null);
                if (v is null) return Fail("--folder needs a value (Cadence or CadenceTest).");
                if (!FolderPolicy.TryNormalize(v, out string f)) return Fail("--folder must be Cadence or CadenceTest, not '" + v + "'.");
                folder = f;
            }
            else if (string.Equals(key, "--max", StringComparison.OrdinalIgnoreCase))
            {
                if (haveMax) return Fail("--max was given twice.");
                string? v = inline ?? (i + 1 < args.Count ? args[++i] : null);
                if (v is null) return Fail("--max needs a number.");
                if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out max) || max < 1 || max > CliCommand.MaxMax)
                    return Fail("--max must be a whole number from 1 to " + CliCommand.MaxMax.ToString(CultureInfo.InvariantCulture) + ", not '" + v + "'.");
                haveMax = true;
            }
            else if (string.Equals(key, "--min-interval", StringComparison.OrdinalIgnoreCase))
            {
                if (minInterval is not null) return Fail("--min-interval was given twice.");
                string? v = inline ?? (i + 1 < args.Count ? args[++i] : null);
                if (v is null) return Fail("--min-interval needs a number of minutes.");
                if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int mi) || !Cadence.Agent.Policy.AgentPolicy.IsValidMinInterval(mi))
                    return Fail("--min-interval must be a whole number of minutes from 1 to 1440, not '" + v + "'.");
                minInterval = mi;
            }
            else if (string.Equals(key, "--max-jobs", StringComparison.OrdinalIgnoreCase))
            {
                if (maxJobs is not null) return Fail("--max-jobs was given twice.");
                string? v = inline ?? (i + 1 < args.Count ? args[++i] : null);
                if (v is null) return Fail("--max-jobs needs a number.");
                if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int mj) || !Cadence.Agent.Policy.AgentPolicy.IsValidMaxAgentJobs(mj))
                    return Fail("--max-jobs must be a whole number from 0 to 500, not '" + v + "'.");
                maxJobs = mj;
            }
            else if (inline is null && string.Equals(key, "--all", StringComparison.OrdinalIgnoreCase)) all = true;
            else if (inline is null && string.Equals(key, "--yes", StringComparison.OrdinalIgnoreCase)) yes = true;
            else if (inline is null && string.Equals(key, "--json", StringComparison.OrdinalIgnoreCase)) json = true;
            else if (a.StartsWith('-') || a.StartsWith('/')) return Fail("Unknown switch '" + a + "'.");
            else positional.Add(a);
        }

        if (action is null) return Fail(positional.Count > 0 ? "Unexpected argument '" + positional[0] + "'." : "No command given.");
        CliAction act2 = action.Value;
        if (act2 == CliAction.Help) return Ok(new CliCommand(CliAction.Help, null, FolderPolicy.Product, false, false, max, false));

        if ((minInterval is not null || maxJobs is not null) && act2 != CliAction.Agents) return Fail("--min-interval and --max-jobs are only valid with --agents config.");
        if (act2 == CliAction.Agents)
        {
            if (all) return Fail("--all does not apply to --agents.");
            return ParseAgents(positional, folder ?? FolderPolicy.Product, yes, json, haveMax, max, minInterval, maxJobs);
        }

        bool takesTarget = act2 is CliAction.Apply or CliAction.Delete or CliAction.Reapply or CliAction.Untrack or CliAction.Run or CliAction.History;
        if (takesTarget && positional.Count == 0)
            return Fail(Name(act2) + " needs " + (act2 == CliAction.Apply ? "a job file" : "a job id") + ".");
        if (positional.Count > (takesTarget ? 1 : 0)) return Fail("Unexpected argument '" + positional[takesTarget ? 1 : 0] + "'.");

        if (act2 == CliAction.SelfTest && folder is not null) return Fail("--folder does not apply to " + Name(act2) + ".");
        if (all && act2 != CliAction.Probe) return Fail("--all is only valid with --probe.");
        if (json && act2 != CliAction.History) return Fail("--json is only valid with --history.");
        if (haveMax && act2 != CliAction.History) return Fail("--max is only valid with --history.");

        var cmd = new CliCommand(act2, takesTarget ? positional[0] : null, folder ?? FolderPolicy.Product, all, yes, max, json);
        if (cmd.Writes && !yes) return Fail(Name(act2) + " changes Task Scheduler; add --yes to confirm.");
        if (!cmd.Writes && yes) return Fail("--yes is only valid with --apply, --delete, --reapply and --untrack.");
        return Ok(cmd);
    }

    /// <summary>
    /// <c>--agents status|suspend|resume|config|log|lock|unlock</c> (PLAN 12.6): the verb is the first positional argument, lock and unlock take a job id.
    /// <c>lock</c> and <c>unlock</c> need <c>--yes</c> (without it the parse fails, exit 2); <c>resume</c> is a plain command ([OWNER-Q14]: no confirmation window; agents can resume through their own distinct tool).
    /// </summary>
    static CliParseResult ParseAgents(List<string> positional, string folder, bool yes, bool json, bool haveMax, int max, int? minInterval, int? maxJobs)
    {
        string verbs = string.Join(", ", AgentsVerbs);
        if (positional.Count == 0) return Fail("--agents needs one of: " + verbs + ".");
        string verb = AgentsVerbs.FirstOrDefault(v => string.Equals(v, positional[0], StringComparison.OrdinalIgnoreCase)) ?? "";
        if (verb.Length == 0) return Fail("--agents: unknown command '" + positional[0] + "'. Use one of: " + verbs + ".");

        bool takesJob = verb is "lock" or "unlock";
        if (takesJob && positional.Count < 2) return Fail("--agents " + verb + " needs a job id.");
        if (positional.Count > (takesJob ? 2 : 1)) return Fail("Unexpected argument '" + positional[takesJob ? 2 : 1] + "'.");

        if (verb == "config")
        {
            if (minInterval is null && maxJobs is null) return Fail("--agents config needs --min-interval N and/or --max-jobs N.");
        }
        else if (minInterval is not null || maxJobs is not null) return Fail("--min-interval and --max-jobs are only valid with --agents config.");

        if (json && verb is not ("status" or "log")) return Fail("--json is only valid with --agents status and --agents log, and with --history.");
        if (haveMax && verb != "log") return Fail("--max is only valid with --agents log and with --history.");

        // [OWNER-Q14] resume is a plain, distinct command (no confirmation window, no --yes); an old --yes is accepted and ignored.
        bool needsYes = verb is "lock" or "unlock";
        if (needsYes && !yes)
            return Fail("--agents " + verb + " changes the job's metadata in Task Scheduler; add --yes to confirm.");
        if (!needsYes && yes && verb != "resume") return Fail("--yes is only valid with --agents lock and unlock, and with --apply, --delete, --reapply and --untrack (--agents resume accepts and ignores it).");

        return Ok(new CliCommand(CliAction.Agents, takesJob ? positional[1] : null, folder, false, yes, max, json, Verb: verb, MinInterval: minInterval, MaxJobs: maxJobs));
    }

    static CliParseResult Ok(CliCommand c) => new(c, null);

    static CliParseResult Fail(string error) => new(null, error);

    public static string Name(CliAction a) => a switch
    {
        CliAction.SelfTest => "--selftest",
        CliAction.Probe => "--probe",
        CliAction.Apply => "--apply",
        CliAction.Delete => "--delete",
        CliAction.Reapply => "--reapply",
        CliAction.Untrack => "--untrack",
        CliAction.Run => "--run",
        CliAction.History => "--history",
        CliAction.MeasureStartup => "--measure-startup",
        CliAction.Mcp => "--mcp",
        CliAction.Agents => "--agents",
        CliAction.Help => "--help",
        _ => "(window)",
    };

    /// <summary>The usage text (CRLF line endings, as written to the console).</summary>
    public static string Usage()
    {
        string[] lines =
        [
            "Cadence - schedule programs with cron expressions (Windows Task Scheduler is the database).",
            "",
            "Usage:",
            "  Cadence.exe                                   open the app",
            "  Cadence.exe --selftest                        run the built-in checks (read-only); exit 0 when all pass",
            "  Cadence.exe --probe [--folder F] [--all]      print the jobs as JSON (--all also lists other tasks, read-only)",
            "  Cadence.exe --apply <job.json> --yes [--folder F]   create or update a job from a JSON file",
            "  Cadence.exe --delete <jobId> --yes [--folder F]     delete a job and its run history",
            "  Cadence.exe --reapply <jobId> --yes [--folder F]    rewrite a drifted job the way Cadence wrote it",
            "  Cadence.exe --untrack <jobId> --yes [--folder F]    stop managing a job; the task stays in Task Scheduler",
            "  Cadence.exe --run <jobId> [--folder F]              start a job now",
            "  Cadence.exe --history <jobId> [--folder F] [--max N] [--json]   recent runs (default 20, at most 500)",
            "  Cadence.exe --measure-startup                  print the milliseconds from process start to the first frame",
            "  Cadence.exe --mcp [--folder F]                run the MCP server for AI agents on stdin/stdout (logs go to stderr)",
            "  Cadence.exe --agents status [--folder F] [--json]   AI agents: allowed or suspended, limits, job count, log path",
            "  Cadence.exe --agents suspend [--folder F]           stop AI agents changing, running or deleting (they can still read)",
            "  Cadence.exe --agents resume [--folder F]            let AI agents change schedules again",
            "  Cadence.exe --agents config [--min-interval N] [--max-jobs N] [--folder F]",
            "                                                      limits for agents: shortest gap between runs in minutes (1-1440,",
            "                                                      default 5) and most jobs they may create (0-500, default 20)",
            "  Cadence.exe --agents log [--max N] [--json] [--folder F]   newest agent audit lines (default 20, at most 500)",
            "  Cadence.exe --agents lock <jobId> --yes [--folder F]     never let AI agents change or run this job",
            "  Cadence.exe --agents unlock <jobId> --yes [--folder F]   let AI agents change this job again",
            "  Cadence.exe --help",
            "",
            "F is Cadence (the default) or CadenceTest; nothing else is accepted.",
            "Exit codes: 0 done, 1 the operation failed, 2 the command line is wrong. --mcp runs until stdin closes (0).",
        ];
        var sb = new StringBuilder();
        foreach (string l in lines) sb.Append(l).Append("\r\n");
        return sb.ToString();
    }
}
