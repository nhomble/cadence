using System.Diagnostics;
using System.IO;
using Cadence.Core.Execution;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// [CP-A] <see cref="CommandLine.QuoteForCmd"/>: a batch file's arguments are parsed again by cmd.exe, where an unquoted metacharacter starts another
/// command. Exact strings first, then real <c>cmd /c echo</c> round trips with hostile text (only ever echoed, never a file of ours).
/// </summary>
public static class QuoteForCmdTests
{
    public static void ExactOutputs()
    {
        (string Arg, string Quoted)[] table =
        [
            // nothing to protect: unchanged
            ("simple", "simple"),
            ("-u", "-u"),
            ("/c", "/c"),
            ("--flag", "--flag"),
            ("C:\\dir\\file.txt", "C:\\dir\\file.txt"),
            ("C:\\dir\\", "C:\\dir\\"),                                   // a trailing backslash is not special to cmd
            ("a.b", "a.b"),
            ("$HOME~1", "$HOME~1"),
            ("'single'", "'single'"),
            ("h\u00e9llo", "h\u00e9llo"),
            // empty and whitespace
            ("", "\"\""),
            ("a b", "\"a b\""),
            ("a\tb", "\"a\tb\""),
            (" ", "\" \""),
            ("C:\\my dir\\", "\"C:\\my dir\\\""),                        // backslashes stay as they are: cmd has no backslash escape
            ("\u65e5\u672c \u8a9e", "\"\u65e5\u672c \u8a9e\""),
            // every metacharacter alone, and in context
            ("a&b", "\"a&b\""),
            ("&", "\"&\""),
            ("a|b", "\"a|b\""),
            ("a<b", "\"a<b\""),
            ("a>b", "\"a>b\""),
            (">out.txt", "\">out.txt\""),
            ("2>&1", "\"2>&1\""),
            ("a^b", "\"a^b\""),
            ("^", "\"^\""),
            ("(x)", "\"(x)\""),
            ("(", "\"(\""),
            (")", "\")\""),
            ("50%", "\"50%\""),
            ("%VAR%", "\"%VAR%\""),
            ("%TEMP%\\x", "\"%TEMP%\\x\""),
            ("hi!", "\"hi!\""),
            ("a,b", "\"a,b\""),
            ("a;b", "\"a;b\""),
            ("k=v", "\"k=v\""),
            ("--opt=value", "\"--opt=value\""),
            // quotes are doubled
            ("say \"hi\"", "\"say \"\"hi\"\"\""),
            ("\"", "\"\"\"\""),
            ("\"\"", "\"\"\"\"\"\""),
            ("a\"b", "\"a\"\"b\""),
            // hostile: a quote that would end the quoted part, then a second command
            ("a\" & calc & \"b", "\"a\"\" & calc & \"\"b\""),
            ("x & echo PWNED > pwned.txt", "\"x & echo PWNED > pwned.txt\""),
            ("\" & del *.* & \"", "\"\"\" & del *.* & \"\"\""),
            ("C:\\x\\\"&calc", "\"C:\\x\\\"\"&calc\""),                 // trailing backslash then a quote
            ("|calc", "\"|calc\""),
            ("^&calc", "\"^&calc\""),
            ("%COMSPEC% /c calc", "\"%COMSPEC% /c calc\""),
        ];
        foreach ((string arg, string quoted) in table)
        {
            string label = arg.Replace("\t", "\\t", StringComparison.Ordinal);
            T.Eq("quote '" + label + "'", quoted, CommandLine.QuoteForCmd(arg));
        }
    }

    public static void TheUnquotedFormIsReallyUnsafeAndTheCmdFormIsNot()
    {
        // CommandLine.Quote is for programs (CommandLineToArgvW): it leaves & alone. That is exactly why a batch file needs QuoteForCmd.
        T.Eq("Quote leaves & unquoted", "a&b", CommandLine.Quote("a&b"));
        T.Eq("QuoteForCmd quotes it", "\"a&b\"", CommandLine.QuoteForCmd("a&b"));
        T.Eq("Quote leaves | unquoted", "x|y", CommandLine.Quote("x|y"));
        T.Eq("QuoteForCmd quotes it", "\"x|y\"", CommandLine.QuoteForCmd("x|y"));
    }

    public static void EveryCmdMetacharacterIsQuotedWhereverItSits()
    {
        foreach (char c in "&|<>^()%!,;=")
        {
            foreach (string arg in new[] { c.ToString(), "a" + c, c + "a", "a" + c + "b" })
            {
                string q = CommandLine.QuoteForCmd(arg);
                T.True("'" + arg + "' is wrapped in quotes", q == "\"" + arg + "\"", q);
            }
        }
        foreach (char c in "abcXYZ019-_./\\:@+~#$*?[]{}'`")
        {
            string arg = "a" + c + "b";
            T.Eq("'" + arg + "' needs nothing", arg, CommandLine.QuoteForCmd(arg));
        }
    }

    public static void LineBreaksAndNulCannotBeCarried()
    {
        T.Throws<ArgumentException>("LF", () => CommandLine.QuoteForCmd("a\nb"));
        T.Throws<ArgumentException>("CR", () => CommandLine.QuoteForCmd("a\rb"));
        T.Throws<ArgumentException>("CRLF", () => CommandLine.QuoteForCmd("a\r\nb"));
        T.Throws<ArgumentException>("NUL", () => CommandLine.QuoteForCmd("a\0b"));
        T.Throws<ArgumentException>("a lone LF", () => CommandLine.QuoteForCmd("\n"));
        T.Throws<ArgumentNullException>("null", () => CommandLine.QuoteForCmd(null!));
        T.Throws<ArgumentNullException>("JoinForCmd(null)", () => CommandLine.JoinForCmd(null!));
    }

    public static void JoinForCmdQuotesEachArgument()
    {
        T.Eq("empty list", "", CommandLine.JoinForCmd([]));
        T.Eq("mixed", "plain \"a b\" \"x&y\" \"\" -v", CommandLine.JoinForCmd(["plain", "a b", "x&y", "", "-v"]));
    }

    /// <summary>A quoted argument survives cmd.exe's own parsing: <c>echo &lt;quoted&gt;</c> prints it back and starts nothing else.</summary>
    public static void CmdEchoRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadence-quotecmd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string[] args =
            [
                "simple", "a b", "a&b", "a|b", "a<b", "a>b", "a^b", "(x)", "50%", "hi!", "a,b", "a;b", "k=v", "say \"hi\"", "\"", "a\" & echo PWNED & \"b",
                "x & echo PWNED > pwned.txt", "x | echo PWNED", "x > pwned2.txt", "x < pwned.txt", "^& echo PWNED", "C:\\my dir\\", "C:\\x\\\"&echo PWNED", "(echo PWNED)",
                "trailing space ", "  leading", "a  b", "tab\there", "&&", "&echo PWNED&", "\" | echo PWNED | \"",
            ];
            foreach (string arg in args)
            {
                string quoted = CommandLine.QuoteForCmd(arg);
                string output = Echo(dir, quoted, out int exit);
                string label = arg.Replace("\t", "\\t", StringComparison.Ordinal);
                T.True("echo of '" + label + "' prints it back, once", output.TrimEnd('\r', '\n') == quoted && exit == 0, "[" + output + "] exit " + exit);
            }
            T.True("no file was created by a redirection or a second command", !Directory.EnumerateFileSystemEntries(dir).Any(), string.Join(", ", Directory.EnumerateFileSystemEntries(dir)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* temp folder: harmless if it stays */ }
        }
    }

    static string Echo(string workingDirectory, string quotedArgument, out int exitCode)
    {
        // cmd /d /s /c "echo <arg>": /s strips the outer pair of quotes, so cmd parses exactly: echo <arg>.
        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var psi = new ProcessStartInfo(cmdExe)
        {
            Arguments = "/d /s /c \"echo " + quotedArgument + "\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        exitCode = p.ExitCode;
        return output;
    }
}
