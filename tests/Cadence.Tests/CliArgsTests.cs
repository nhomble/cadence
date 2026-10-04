using Cadence.App.Cli;
using Cadence.Core;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>Command-line parsing (PLAN 7.6, Phase 11): switches, the folder allow-list, usage text, version text.</summary>
public static class CliArgsTests
{
    static CliParseResult P(params string[] args) => CliParser.Parse(args);

    static CliCommand Ok(string label, params string[] args)
    {
        CliParseResult r = P(args);
        T.True(label + ": parses (" + (r.Error ?? "") + ")", r.Command is not null && r.Error is null);
        return r.Command ?? new CliCommand(CliAction.Gui, null, "Cadence", false, false, 0, false);
    }

    static void Bad(string label, string expectedPart, params string[] args)
    {
        CliParseResult r = P(args);
        T.True(label + ": rejected", r.Command is null && r.Error is not null, r.Command?.ToString());
        T.True(label + ": error mentions '" + expectedPart + "' (was: " + r.Error + ")", r.Error?.Contains(expectedPart, StringComparison.Ordinal) == true);
    }

    public static void NoArgumentsOpensTheWindow()
    {
        CliCommand c = Ok("no args");
        T.Eq("action", CliAction.Gui, c.Action);
        T.Eq("folder", "Cadence", c.Folder);
    }

    public static void EachSwitchParses()
    {
        CliCommand c = Ok("selftest", "--selftest");
        T.Eq("selftest", CliAction.SelfTest, c.Action);

        c = Ok("probe", "--probe");
        T.Eq("probe", CliAction.Probe, c.Action);
        T.Eq("probe: default folder", "Cadence", c.Folder);
        T.Eq("probe: not --all", false, c.All);
        T.Eq("probe: no writes", false, c.Writes);

        c = Ok("probe all", "--probe", "--all", "--folder", "CadenceTest");
        T.Eq("probe all: all", true, c.All);
        T.Eq("probe all: folder", "CadenceTest", c.Folder);

        c = Ok("apply", "--apply", @"C:\jobs\a b.json", "--yes", "--folder", "CadenceTest");
        T.Eq("apply", CliAction.Apply, c.Action);
        T.Eq("apply: target keeps spaces", @"C:\jobs\a b.json", c.Target);
        T.Eq("apply: yes", true, c.Yes);
        T.Eq("apply: writes", true, c.Writes);

        foreach ((string sw, CliAction a) in new[] { ("--delete", CliAction.Delete), ("--reapply", CliAction.Reapply), ("--untrack", CliAction.Untrack) })
        {
            c = Ok(sw, sw, "abcd1234", "--yes");
            T.Eq(sw + ": action", a, c.Action);
            T.Eq(sw + ": target", "abcd1234", c.Target);
            T.Eq(sw + ": writes", true, c.Writes);
        }

        c = Ok("run", "--run", "abcd1234", "--folder", "CadenceTest");
        T.Eq("run", CliAction.Run, c.Action);
        T.Eq("run: no writes flag (it starts a task, it does not change one)", false, c.Writes);

        c = Ok("history", "--history", "abcd1234");
        T.Eq("history", CliAction.History, c.Action);
        T.Eq("history: default max", CliCommand.DefaultMax, c.Max);
        T.Eq("history: text by default", false, c.Json);

        c = Ok("history json", "--history", "abcd1234", "--max", "5", "--json", "--folder=cadencetest");
        T.Eq("history json: max", 5, c.Max);
        T.Eq("history json: json", true, c.Json);
        T.Eq("history json: --folder=value, canonical case", "CadenceTest", c.Folder);
    }

    public static void OrderAndCaseDoNotMatter()
    {
        CliCommand c = Ok("options first", "--folder", "CadenceTest", "--yes", "--delete", "abcd1234");
        T.Eq("action", CliAction.Delete, c.Action);
        T.Eq("target", "abcd1234", c.Target);
        T.Eq("folder", "CadenceTest", c.Folder);

        c = Ok("upper case", "--PROBE", "--ALL", "--Folder", "CADENCE");
        T.Eq("action", CliAction.Probe, c.Action);
        T.Eq("canonical folder spelling", "Cadence", c.Folder);
        T.Eq("all", true, c.All);

        T.Eq("-h", CliAction.Help, Ok("-h", "-h").Action);
        T.Eq("--help", CliAction.Help, Ok("--help", "--help").Action);
        T.Eq("/?", CliAction.Help, Ok("/?", "/?").Action);
    }

    public static void OnlyCadenceAndCadenceTestAreAcceptedAsFolders()
    {
        foreach (string folder in new[]
        {
            "Microsoft", @"\Cadence", "Cadence\\", "CadenceTest\\Sub", @"Cadence\..\Microsoft", "..", ".", "", " ", "Cadence ", "Cadence2", "CadenceTes", "Cad",
            @"Microsoft\Windows", "C:\\Windows", "Cadence/x", "Cadence\0",
        })
            Bad("--folder '" + folder + "'", "must be Cadence or CadenceTest", "--probe", "--folder", folder);

        Bad("inline form", "must be Cadence or CadenceTest", "--probe", "--folder=Microsoft");
        Bad("a write with a bad folder", "must be Cadence or CadenceTest", "--delete", "abcd1234", "--yes", "--folder", "Microsoft");
        Bad("--folder without a value", "needs a value", "--probe", "--folder");
        Bad("--folder given twice", "twice", "--probe", "--folder", "Cadence", "--folder", "CadenceTest");

        T.True("TryNormalize Cadence", FolderPolicy.TryNormalize("cadence", out string f1) && f1 == "Cadence");
        T.True("TryNormalize CadenceTest", FolderPolicy.TryNormalize("CADENCETEST", out string f2) && f2 == "CadenceTest");
        T.True("TryNormalize null", !FolderPolicy.TryNormalize(null, out _));
        T.True("TryNormalize other", !FolderPolicy.TryNormalize("Microsoft", out _));

        string? err = FolderPolicy.FromEnvironment(null, out string e1);
        T.True("env unset: default", err is null && e1 == "Cadence");
        err = FolderPolicy.FromEnvironment("  ", out e1);
        T.True("env blank: default", err is null && e1 == "Cadence");
        err = FolderPolicy.FromEnvironment(" cadencetest ", out e1);
        T.True("env CadenceTest (trimmed, any case)", err is null && e1 == "CadenceTest");
        err = FolderPolicy.FromEnvironment("Microsoft", out e1);
        T.True("env Microsoft is an error", err is not null && err.Contains("Microsoft", StringComparison.Ordinal) && e1 == "Cadence", err);
    }

    public static void UsageErrors()
    {
        Bad("unknown switch", "Unknown switch", "--bogus");
        Bad("unknown switch after a command", "Unknown switch", "--probe", "--bogus");
        Bad("a flag with a value", "Unknown switch", "--probe=1");
        Bad("a switch-looking path", "Unknown switch", "--apply", "/x.json", "--yes");
        Bad("a bare word", "Unexpected argument", "probe");
        Bad("options only", "No command", "--folder", "CadenceTest");
        Bad("probe with a stray argument", "Unexpected argument", "--probe", "extra");
        Bad("selftest with a stray argument", "Unexpected argument", "--selftest", "extra");
        Bad("two commands", "Only one command", "--probe", "--selftest");
        Bad("the same command twice", "Only one command", "--probe", "--probe");

        Bad("apply needs a file", "needs a job file", "--apply", "--yes");
        Bad("delete needs an id", "needs a job id", "--delete", "--yes");
        Bad("run needs an id", "needs a job id", "--run");
        Bad("history needs an id", "needs a job id", "--history");
        Bad("two targets", "Unexpected argument", "--delete", "abcd1234", "other000", "--yes");

        foreach (string sw in new[] { "--apply", "--delete", "--reapply", "--untrack" })
            Bad(sw + " without --yes", "add --yes", sw, "abcd1234");
        Bad("--yes on a read-only command", "--yes is only valid", "--run", "abcd1234", "--yes");
        Bad("--yes on probe", "--yes is only valid", "--probe", "--yes");
        Bad("--yes on history", "--yes is only valid", "--history", "abcd1234", "--yes");

        Bad("--folder on selftest", "does not apply", "--selftest", "--folder", "CadenceTest");
        Bad("--all on history", "--all is only valid with --probe", "--history", "abcd1234", "--all");
        Bad("--all on apply", "--all is only valid with --probe", "--apply", "a.json", "--yes", "--all");
        Bad("--json on probe", "--json is only valid with --history", "--probe", "--json");
        Bad("--max on probe", "--max is only valid with --history", "--probe", "--max", "3");

        foreach (string max in new[] { "0", "-1", "501", "x", "", "1.5", "99999999999", "+3" })
            Bad("--max '" + max + "'", "--max must be", "--history", "abcd1234", "--max", max);
        Bad("--max without a value", "needs a number", "--history", "abcd1234", "--max");
        Bad("--max twice", "twice", "--history", "abcd1234", "--max", "1", "--max", "2");
        T.Eq("--max 500 is the largest", 500, Ok("max 500", "--history", "abcd1234", "--max", "500").Max);
        T.Eq("--max 1 is the smallest", 1, Ok("max 1", "--history", "abcd1234", "--max", "1").Max);
    }

    public static void MeasureSwitchAndRemovedDevSwitches()
    {
        CliCommand c = Ok("measure", "--measure-startup");
        T.Eq("measure action", CliAction.MeasureStartup, c.Action);
        c = Ok("measure on the dev folder", "--measure-startup", "--folder", "CadenceTest");
        T.Eq("measure folder", "CadenceTest", c.Folder);

        // The demo and screenshot switches are not part of the product (the screenshots come from tools\Cadence.Render).
        foreach (string gone in new[] { "--demo", "--render", "--out", "--width", "--height" })
            Bad(gone + " is not a switch of the product", "Unknown switch", gone);
        Bad("--demo is not accepted with --measure-startup", "Unknown switch", "--measure-startup", "--demo");
        Bad("--render is not accepted with a job command", "Unknown switch", "--probe", "--render");
    }

    public static void UsageText()
    {
        string u = CliParser.Usage();
        foreach (string sw in new[] { "--selftest", "--probe", "--apply", "--delete", "--reapply", "--untrack", "--run", "--history", "--measure-startup", "--help", "--yes", "--folder", "--all", "--max", "--json", "CadenceTest", "Exit codes" })
            T.True("usage mentions " + sw, u.Contains(sw, StringComparison.Ordinal));
        foreach (string gone in new[] { "--demo", "--render", "--out" })
            T.True("usage does not mention " + gone, !u.Contains(gone, StringComparison.Ordinal));
        T.True("usage uses CRLF only", !u.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal));
        T.True("usage ends with a line break", u.EndsWith("\r\n", StringComparison.Ordinal));
        T.True("usage is ASCII (the console may not be UTF-8)", u.All(ch => ch < 128));
        T.True("every line fits 120 columns", u.Split("\r\n").All(l => l.Length <= 120));
        T.Eq("name of --probe", "--probe", CliParser.Name(CliAction.Probe));
    }

    public static void VersionText()
    {
        T.Eq("null", "dev build", AppVersion.Format(null));
        T.Eq("empty", "dev build", AppVersion.Format(""));
        T.Eq("blank", "dev build", AppVersion.Format("   "));
        T.Eq("dev", "dev build", AppVersion.Format("dev"));
        T.Eq("dev with a source revision", "dev build", AppVersion.Format("dev+1a2b3c4"));
        T.Eq("release", "v0.1.1", AppVersion.Format("0.1.1"));
        T.Eq("release with a source revision", "v0.1.1", AppVersion.Format("0.1.1+abcdef0"));
        T.Eq("release candidate", "v0.2.0-rc1", AppVersion.Format("0.2.0-rc1"));
        T.Eq("something else is shown as it is", "nightly", AppVersion.Format("nightly"));
        T.Eq("trimmed", "v1.0.0", AppVersion.Format(" 1.0.0 "));
        T.True("Display is never empty", AppVersion.Display().Length > 0);
    }
}
