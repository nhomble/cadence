using System.Diagnostics;
using System.Text;
using Cadence.Core.Execution;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class WrapperScriptTests
{
    const string TaskPath = @"\Cadence\Stock ingest (Dad)";

    // [CP-A] The v1 script text is pinned by a checked-in, reviewable fixture (an edit shows up as a readable diff, unlike the frozen
    // SHA-256 it replaces), embedded in the test binary so a standalone checkout without PLAN.md still tests it. PLAN.md Appendix A is
    // compared too when the repo copy is present.
    static string Joined(string text) =>
        string.Join(' ', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    static string FixtureJoined()
    {
        using var s = typeof(WrapperScriptTests).Assembly.GetManifestResourceStream("Cadence.Tests.Fixtures.wrapper-v1.ps1")
            ?? throw new InvalidOperationException("embedded fixture Fixtures\\wrapper-v1.ps1 is missing");
        using var r = new StreamReader(s, Encoding.UTF8);
        return Joined(r.ReadToEnd());
    }

    static string? FindPlan()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "PLAN.md");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>The Appendix A code block of PLAN.md, trimmed lines joined with single spaces (null when PLAN.md or the block is absent).</summary>
    static string? AppendixAJoined()
    {
        string? plan = FindPlan();
        if (plan is null) return null;
        var lines = File.ReadAllText(plan).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int appendix = Array.FindIndex(lines, l => l.StartsWith("## Appendix A", StringComparison.Ordinal));
        if (appendix < 0) return null;
        int open = Array.FindIndex(lines, appendix, l => l == "```powershell");
        if (open < 0) return null;
        int close = Array.FindIndex(lines, open + 1, l => l == "```");
        if (close < 0) return null;
        return Joined(string.Join('\n', lines.Skip(open + 1).Take(close - open - 1)));
    }

    public static void ByteIdenticalToFixture()
    {
        string fixture = FixtureJoined();
        T.Eq("joined template equals the checked-in fixture (Fixtures\\wrapper-v1.ps1)", fixture, WrapperScript.JoinedTemplate());
        T.True("fixture has the skip block", fixture.Contains("holiday list expired; ran anyway", StringComparison.Ordinal));
        T.Eq("Version", 1, WrapperScript.Version);
    }

    public static void PlanAppendixAMatchesFixture()
    {
        string? appendix = AppendixAJoined();
        if (appendix is null)
        {
            T.True("PLAN.md Appendix A not found above the test binary (standalone checkout): comparison skipped", true);
            return;
        }
        T.Eq("PLAN.md Appendix A equals the fixture", FixtureJoined(), appendix);
    }

    public static void ScriptInvariants()
    {
        string script = WrapperScript.Build(TaskPath, "Cadence");
        T.True("no double quote", !script.Contains('"', StringComparison.Ordinal));
        T.True("no percent sign", !script.Contains('%', StringComparison.Ordinal));
        T.True("no $( subexpression", !script.Contains("$(", StringComparison.Ordinal));
        T.True("single line (no CR, no LF)", script.IndexOfAny(['\r', '\n']) < 0);
        T.True("no tab", !script.Contains('\t', StringComparison.Ordinal));
        T.True("length <= 4,500", script.Length <= 4500, script.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        T.True("length about 3.4 KB", script.Length is > 3000 and < 4000, script.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        T.True("placeholders replaced", !script.Contains("{TASKPATH}", StringComparison.Ordinal) && !script.Contains("{RUNSROOT}", StringComparison.Ordinal));
        T.True("task path embedded in single quotes", script.Contains("$tp='" + TaskPath + "';", StringComparison.Ordinal));
        T.True("runs root embedded in single quotes", script.Contains("$rr='Cadence';", StringComparison.Ordinal));
        T.True("starts as Appendix A starts", script.StartsWith("$ErrorActionPreference='Stop'; $tp='", StringComparison.Ordinal));
        T.True("ends with exit $code", script.EndsWith("exit $code", StringComparison.Ordinal));
        T.True("template has no stray placeholder other than the two", WrapperScript.JoinedTemplate().Split('{').Skip(1).Count(s => s.StartsWith("TASKPATH}", StringComparison.Ordinal) || s.StartsWith("RUNSROOT}", StringComparison.Ordinal)) == 2);
        T.True("the real exit code is recorded by the script itself", script.Contains("$code=$p.ExitCode", StringComparison.Ordinal));
        T.True("run spec is read from the task's own Documentation", script.Contains("RegistrationInfo.Documentation", StringComparison.Ordinal));
        T.True("holiday skip block", script.Contains("$r.skip.dates.PSObject.Properties[$today]", StringComparison.Ordinal));
        T.True("timeout tree-kill", script.Contains("taskkill.exe /PID $p.Id /T /F", StringComparison.Ordinal));
        // [CP-A] regressions: culture-independent dates, and a bounded wait for output held open by a program the job started.
        T.True("holiday date uses the invariant culture", script.Contains("$today=[DateTime]::Now.ToString('yyyy-MM-dd',$ic);", StringComparison.Ordinal));
        T.True("runId uses the invariant culture", script.Contains("ToString('yyyyMMdd-HHmmss-fff',$ic)", StringComparison.Ordinal));
        T.True("no culture-dependent custom date format left", !System.Text.RegularExpressions.Regex.IsMatch(script, @"ToString\('[yMdHmsf-]+'\)"));
        T.True("output wait is bounded", script.Contains("[Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($o,$e),10000)", StringComparison.Ordinal));
        T.True("no unbounded $o.Result before the bounded wait", script.IndexOf("$o.Result", StringComparison.Ordinal) > script.IndexOf("WaitAll(", StringComparison.Ordinal));
    }

    // [CP-A] Behavioural regression for the output-pipe hang, run under the real Windows PowerShell 5.1 with the wrapper's own
    // process-and-capture statements (lifted from the template, so they can't drift): a .bat that starts a long-lived program which
    // inherits the pipes must not keep the capture waiting until that program exits.
    public static void OutputCaptureDoesNotWaitForInheritedPipes()
    {
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(ps)) { T.True("powershell.exe not present: check skipped", true); return; }

        string joined = WrapperScript.JoinedTemplate();
        int from = joined.IndexOf("$psi=New-Object", StringComparison.Ordinal);
        int to = joined.IndexOf("$rec.outBytes=", StringComparison.Ordinal);
        T.True("capture statements found in the template", from > 0 && to > from);
        if (from <= 0 || to <= from) return;
        string capture = joined[from..to].Replace("10000)", "2000)", StringComparison.Ordinal);   // shorter grace keeps the test quick

        string dir = Path.Combine(Path.GetTempPath(), "cadence-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string bat = Path.Combine(dir, "spawn.bat");
            File.WriteAllText(bat, "@echo off\r\necho parent\r\nstart \"\" /b cmd /c \"ping -n 30 127.0.0.1 >nul 2>nul\"\r\nexit /b 4\r\n");
            string script = Path.Combine(dir, "capture.ps1");
            string result = Path.Combine(dir, "result.txt");
            // The result goes to a file: the long-lived child inherits whatever pipes exist, so stdout can't be used to report.
            string body = "$ErrorActionPreference='Stop'; $rec=@{msg=$null;pid=$null;start=$null;exit=$null;end=$null;status=$null}; "
                + "$r=[pscustomobject]@{exe='" + bat.Replace("'", "''", StringComparison.Ordinal) + "';args='';cwd=$null;capture=$true;encoding='oem';env=$null;timeoutSec=0}; "
                + capture + " }"   // closes the template's if($cap){ block, which the slice opens
                + " [IO.File]::WriteAllText('" + result.Replace("'", "''", StringComparison.Ordinal) + "', ('EXIT=' + $code + ' OUT=' + $so.Trim() + ' MSG=' + $rec.msg))";
            File.WriteAllText(script, body, new UTF8Encoding(false));
            var psi = new ProcessStartInfo(ps, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            bool done = p.WaitForExit(20_000);
            if (!done) p.Kill(true);
            string output = File.Exists(result) ? File.ReadAllText(result) : "(no result file)";
            T.True("wrapper capture finished well before the 30 s child", done, output);
            T.True("exit code of the job itself is recorded", output.Contains("EXIT=4", StringComparison.Ordinal), output);
            T.True("message says output was not fully captured", output.Contains("output not fully captured", StringComparison.Ordinal), output);
        }
        finally
        {
            // The ping (stdout/stderr to nul, 30 s) is left to finish on its own; it holds no file in the temp folder.
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static void TaskPathValidation()
    {
        (string? Path, bool Valid)[] table =
        [
            (@"\Cadence\Stock ingest (Dad)", true),
            (@"\CadenceTest\a", true),
            (@"\Cadence\keep _.,()+- ok", true),
            (@"\Cadence\a  b", false),                     // [CP-A] powershell.exe would rejoin it as 'a b'
            (@"\Cadence\ a", false),
            (@"\Cadence\a ", false),
            (@"\Cadence\a.", false),                       // Windows drops a trailing dot from the task's file name
            (@"\Cadence\a.b", true),
            (@"\Cadence\(2)", true),
            (@"\Cadence\" + new string('a', 80), true),
            (@"\Cadence\" + new string('a', 81), false),
            (@"\Cadence\", false),
            (@"\Cadence", false),
            (@"Cadence\x", false),
            (@"\\Cadence\x", false),
            (@"\Cad ence\x", false),                       // folder: letters and digits only
            (@"\Cadence\sub\x", false),                    // no nested folders
            (@"\Cadence\it's", false),                     // would end the single-quoted PowerShell string
            (@"\Cadence\a""b", false),
            (@"\Cadence\100%", false),
            (@"\Cadence\a$b", false),
            (@"\Cadence\a;b", false),
            (@"\Cadence\a`b", false),
            (@"\Cadence\a&b", false),
            ("\\Cadence\\a\nb", false),
            ("\\Cadence\\a\n", false),                     // $ would accept a trailing newline; \z does not
            ("\\Cadence\\caf\u00e9", false),
            ("", false),
            (null, false),
        ];
        foreach (var (path, valid) in table)
            T.Eq("task path '" + (path ?? "null").Replace("\n", "\\n", StringComparison.Ordinal) + "'", valid, WrapperScript.ValidateTaskPath(path));

        foreach (var (root, valid) in new (string?, bool)[] { ("Cadence", true), ("CadenceTest", true), ("a1", true), ("", false), (null, false), ("Cad ence", false), ("a'b", false), ("a\\b", false), ("a\n", false) })
            T.Eq("runs root '" + (root ?? "null").Replace("\n", "\\n", StringComparison.Ordinal) + "'", valid, WrapperScript.ValidateRunsRoot(root));

        T.Throws<ArgumentException>("Build rejects a quote in the task path", () => WrapperScript.Build(@"\Cadence\it's", "Cadence"));
        T.Throws<ArgumentException>("Build rejects a bad runs root", () => WrapperScript.Build(TaskPath, "a'b"));
        T.Throws<ArgumentException>("BuildAction rejects a bad task path", () => WrapperScript.BuildAction(@"\Cadence\a%b", "Cadence", true));
    }

    public static void HiddenAndVisibleShapes()
    {
        string script = WrapperScript.Build(TaskPath, "Cadence");

        var hidden = WrapperScript.BuildAction(TaskPath, "Cadence", hidden: true);
        T.Eq("hidden command", @"%SystemRoot%\System32\conhost.exe", hidden.Command);
        T.Eq("hidden arguments",
            @"--headless %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + script,
            hidden.Arguments);
        T.Eq("hidden working directory", (string?)null, hidden.WorkingDirectory);

        var visible = WrapperScript.BuildAction(TaskPath, "Cadence", hidden: false);
        T.Eq("visible command", @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe", visible.Command);
        T.Eq("visible arguments", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + script, visible.Arguments);
        T.Eq("visible working directory", (string?)null, visible.WorkingDirectory);

        // The only % in an action is the %SystemRoot% that Task Scheduler expands on purpose (Command and Arguments).
        foreach (var (name, a, roots) in new[] { ("hidden", hidden, 1), ("visible", visible, 0) })
        {
            string args = a.Arguments!;
            T.Eq(name + ": %SystemRoot% references in Arguments", roots, args.Split("%SystemRoot%", StringSplitOptions.None).Length - 1);
            T.True(name + ": no other % in Arguments", !args.Replace("%SystemRoot%", "", StringComparison.Ordinal).Contains('%', StringComparison.Ordinal));
            T.True(name + ": no other % in Command", !a.Command.Replace("%SystemRoot%", "", StringComparison.Ordinal).Contains('%', StringComparison.Ordinal));
        }
        T.True("arguments length well under the verified 8 KB", hidden.Arguments!.Length < 4200, hidden.Arguments.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void NothingUserSuppliedIsInTheArguments()
    {
        // Whatever the job's command looks like, the action only depends on the task path, the runs root and the visibility.
        string a = WrapperScript.BuildAction(@"\Cadence\Job one", "Cadence", true).Arguments!;
        string b = WrapperScript.BuildAction(@"\Cadence\Job one", "Cadence", true).Arguments!;
        T.Eq("deterministic", a, b);
        T.True("different task path changes only the path", WrapperScript.Build(@"\Cadence\Job two", "Cadence").Replace("Job two", "Job one", StringComparison.Ordinal) == WrapperScript.Build(@"\Cadence\Job one", "Cadence"));
    }

    // Sanity check with the real parser (Windows PowerShell 5.1): the joined script has no syntax errors.
    public static void ParsesAsPowerShell()
    {
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(ps)) { T.True("powershell.exe not present: parse check skipped", true); return; }

        string script = WrapperScript.Build(TaskPath, "Cadence");
        string tmp = Path.Combine(Path.GetTempPath(), "cadence-wrapper-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(tmp, script, new UTF8Encoding(false));
            // Parse only (nothing executes): report the number of parse errors on stdout.
            string cmd = "$e=$null; $t=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + tmp.Replace("'", "''", StringComparison.Ordinal) + "',[ref]$t,[ref]$e); Write-Output ('ERRORS=' + @($e).Count); foreach($x in @($e)){Write-Output $x.Message}";
            var psi = new ProcessStartInfo(ps, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + cmd + "\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { p.Kill(true); T.True("parser finished in 60 s", false); return; }
            string output = stdout.Result.Trim();
            T.True("PowerShell parser reports no errors", output == "ERRORS=0", output + " " + stderr.Result);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }
}
