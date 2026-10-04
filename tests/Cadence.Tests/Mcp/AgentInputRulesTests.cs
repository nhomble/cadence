using Cadence.Core.Execution;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>Phase 21: the pure shape rules of PLAN 12.5.2 (rules 1, 2, 3, 6, 7) in <c>AgentInputRules</c>: exact messages, boundaries, and "never a value in a message".</summary>
public static class AgentInputRulesTests
{
    public static void ProgramShapeAcceptsOnlyAFullLocalExe()
    {
        foreach (string ok in new[] { @"C:\Windows\System32\cmd.exe", @"d:\x\y.EXE", @"C:\a.com", @"C:\Program Files\Tool\tool.exe", @"C:\x\y.exe. ", @"C:\x\sub.dir\y.exe" })
            T.True("accepted: " + ok, AgentInputRules.CheckProgramShape(ok) is null, AgentInputRules.CheckProgramShape(ok));

        (string Path, string Starts)[] bad =
        [
            ("", "The program path is empty."),
            ("   ", "The program path is empty."),
            ("python.exe", "Task Scheduler does not use your PATH. Give the full path of the program, for example"),
            ("cmd", "Task Scheduler does not use your PATH."),
            (@"tools\x.exe", "The program path must be a full local path"),
            (@".\x.exe", "The program path must be a full local path"),
            (@"..\x.exe", "The program path must be a full local path"),
            (@"C:x.exe", "The program path must be a full local path"),
            (@"\x.exe", "The program path must be a full local path"),
            (@"%SystemRoot%\x.exe", "The program path must be a full local path"),
            (@"\\server\share\x.exe", "Network paths"),
            ("//server/share/x.exe", "Network paths"),
            (@"\\?\C:\x.exe", "Device paths"),
            (@"\\.\C:\x.exe", "Device paths"),
            ("//?/C:/x.exe", "Device paths"),
            (@"C:\x" + "\0" + ".exe", "The program path contains a control character"),
            ("C:\\x\r.exe", "The program path contains a control character"),
            ("C:\\x\n.exe", "The program path contains a control character"),
            ("C:\\\"x\\y.exe", "The program path can't contain a quote character"),
            (@"C:\x\run.bat", "A script can't be the program itself. Use %SystemRoot%\\System32\\cmd.exe with arguments /d /s /c \"\"C:\\x\\run.bat\"\"."),
            (@"C:\x\run.CMD", "A script can't be the program itself."),
            (@"C:\x\run.ps1", "A script can't be the program itself. Use %SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe with arguments -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\x\\run.ps1\"."),
            (@"C:\x\run.py", "A script can't be the program itself."),
            (@"C:\x\run.js", "A script can't be the program itself."),
            (@"C:\x\run.vbs", "A script can't be the program itself. Use %SystemRoot%\\System32\\cscript.exe with arguments //NoLogo \"C:\\x\\run.vbs\"."),
            (@"C:\x\a.lnk", "A shortcut (.lnk) can't be used."),
            (@"C:\x\a.txt", "The program must be an .exe or .com file; '.txt' is not allowed."),
            (@"C:\x\a", "The program must be an .exe or .com file; a file without an extension is not allowed."),
            (@"C:\x\a.exe.txt", "The program must be an .exe or .com file; '.txt' is not allowed."),
            (@"C:\x\run.bat.", "A script can't be the program itself."),
        ];
        foreach ((string path, string starts) in bad)
        {
            string? r = AgentInputRules.CheckProgramShape(path);
            T.True("refused: " + path.Replace("\0", "\\0", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal), r is not null && r.StartsWith(starts, StringComparison.Ordinal), r);
        }
        T.True("null is refused", AgentInputRules.CheckProgramShape(null) is not null);
    }

    public static void BareNamesAndHints()
    {
        T.True("python.exe is bare", AgentInputRules.IsBareName("python.exe"));
        T.True("a path is not", !AgentInputRules.IsBareName(@"C:\x\python.exe") && !AgentInputRules.IsBareName(@"x\python.exe") && !AgentInputRules.IsBareName("x/python.exe") && !AgentInputRules.IsBareName("C:python.exe"));
        T.True("empty is not a bare name", !AgentInputRules.IsBareName("") && !AgentInputRules.IsBareName(null));
        T.Eq("the hint for a batch file is the cmd form", "%SystemRoot%\\System32\\cmd.exe with arguments /d /s /c \"\"C:\\x\\run.bat\"\"", AgentInputRules.HintFor(@"C:\x\run.bat"));
        T.True("the hint for a Python script names the launcher or python", AgentInputRules.HintFor(@"C:\x\run.py").Contains("-u \"C:\\x\\run.py\"", StringComparison.Ordinal));
        T.True("a probe is asked about Python", AgentInputRules.HintFor(@"C:\x\run.py", new FakeProbe()).StartsWith(@"C:\Windows\py.exe with arguments -u", StringComparison.Ordinal));
        T.True("an unknown extension still returns a sentence", AgentInputRules.HintFor(@"C:\x\run.zzz").Length > 10);
    }

    public static void ArgumentsRule()
    {
        T.True("null, empty and plain text are fine", AgentInputRules.CheckArguments(null) is null && AgentInputRules.CheckArguments("") is null && AgentInputRules.CheckArguments("/d /c echo \"a b\" %TEMP%") is null);
        T.True("8000 characters are fine", AgentInputRules.CheckArguments(new string('a', 8000)) is null);
        T.Eq("8001 are not", "The arguments are longer than 8000 characters.", AgentInputRules.CheckArguments(new string('a', 8001)));
        T.Eq("a line feed", "The arguments can't contain line breaks; put each command in a script file and run that.", AgentInputRules.CheckArguments("a\nb"));
        T.Eq("a carriage return", "The arguments can't contain line breaks; put each command in a script file and run that.", AgentInputRules.CheckArguments("a\rb"));
        T.Eq("NUL", "The arguments contain a NUL character.", AgentInputRules.CheckArguments("a\0b"));
        T.True("a tab is fine", AgentInputRules.CheckArguments("a\tb") is null);
    }

    static KeyValuePair<string, string> E(string k, string v) => new(k, v);

    public static void EnvironmentRule()
    {
        T.True("none and empty are fine", AgentInputRules.CheckEnvironment(null) is null && AgentInputRules.CheckEnvironment([]) is null);
        T.True("typical names", AgentInputRules.CheckEnvironment([E("PYTHONIOENCODING", "utf-8"), E("_x", ""), E("A.b(c)-d", "v"), E("a1", "v")]) is null);
        T.True("a 100-character name is fine", AgentInputRules.CheckEnvironment([E("A" + new string('b', 99), "v")]) is null);
        T.True("a 101-character name is not", AgentInputRules.CheckEnvironment([E("A" + new string('b', 100), "v")]) is not null);
        T.True("30 entries are fine", AgentInputRules.CheckEnvironment([.. Enumerable.Range(0, 30).Select(i => E("V" + i, "x"))]) is null);
        T.Eq("31 are not", "At most 30 environment variables are allowed, but 31 were given.", AgentInputRules.CheckEnvironment([.. Enumerable.Range(0, 31).Select(i => E("V" + i, "x"))]));
        foreach (string name in new[] { "", "A=B", "=A", "1A", "A B", "A\0", "A\n", "A;B", "A$", "ü", "-A", ".A" })
            T.True("name refused: '" + name.Replace("\0", "\\0", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "'", AgentInputRules.CheckEnvironment([E(name, "v")]) is { } m && m.StartsWith("Environment variable name number 1 is not valid", StringComparison.Ordinal), AgentInputRules.CheckEnvironment([E(name, "v")]));
        T.Eq("a duplicate ignoring case names the second spelling", "The environment variable PATH is given twice (names are not case-sensitive).", AgentInputRules.CheckEnvironment([E("Path", "1"), E("PATH", "2")]));
        T.True("2000-character values are fine", AgentInputRules.CheckEnvironment([E("A", new string('v', 2000))]) is null);
        T.Eq("2001 are not", "The value of the environment variable A is longer than 2000 characters.", AgentInputRules.CheckEnvironment([E("A", new string('v', 2001))]));
        T.Eq("NUL in a value", "The value of the environment variable A contains a NUL character.", AgentInputRules.CheckEnvironment([E("A", "a\0b")]));
        T.True("a line break in a value is allowed (JSON escapes it)", AgentInputRules.CheckEnvironment([E("A", "a\nb")]) is null);

        const string secret = "s3cr3t-v4lue";
        string?[] messages =
        [
            AgentInputRules.CheckEnvironment([E("TOKEN", secret + new string('x', 2100))]),
            AgentInputRules.CheckEnvironment([E("TOKEN", secret + "\0")]),
            AgentInputRules.CheckEnvironment([E("TOKEN", secret), E("token", secret)]),
            AgentInputRules.CheckEnvironment([E(secret + "=", "x")]),
            AgentInputRules.CheckEnvironment([.. Enumerable.Range(0, 31).Select(i => E("V" + i, secret))]),
        ];
        T.True("no message ever contains a value or an invalid name", messages.All(m => m is not null && !m.Contains(secret, StringComparison.Ordinal)));
    }

    public static void TheNameRuleAndTheLimitsAreThePlans()
    {
        T.True("valid names", AgentInputRules.ValidEnvironmentName("A") && AgentInputRules.ValidEnvironmentName("_") && AgentInputRules.ValidEnvironmentName("A.B(C)-D_1"));
        T.True("invalid names", !AgentInputRules.ValidEnvironmentName("") && !AgentInputRules.ValidEnvironmentName(null) && !AgentInputRules.ValidEnvironmentName("1") && !AgentInputRules.ValidEnvironmentName("A=B"));
        T.Eq("arguments limit", 8000, AgentInputRules.MaxArgumentsLength);
        T.Eq("environment entries", 30, AgentInputRules.MaxEnvironmentEntries);
        T.Eq("environment value length", 2000, AgentInputRules.MaxEnvironmentValueLength);
        T.Eq("environment name length", 100, AgentInputRules.MaxEnvironmentNameLength);
    }
}
