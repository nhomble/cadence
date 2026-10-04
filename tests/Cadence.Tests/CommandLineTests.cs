using Cadence.Core.Execution;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class CommandLineTests
{
    public static void QuoteTable()
    {
        (string Arg, string Quoted)[] table =
        [
            ("simple", "simple"),
            ("", "\"\""),
            ("a b", "\"a b\""),
            ("a\tb", "\"a\tb\""),
            ("say \"hi\"", "\"say \\\"hi\\\"\""),
            ("\"", "\"\\\"\""),
            ("C:\\dir\\file.txt", "C:\\dir\\file.txt"),
            ("C:\\dir\\", "C:\\dir\\"),                                // no space: nothing to protect, trailing backslash is harmless
            ("C:\\my dir\\", "\"C:\\my dir\\\\\""),                    // trailing backslash doubled before the closing quote
            ("C:\\my dir\\\\", "\"C:\\my dir\\\\\\\\\""),
            ("a\\\"b", "\"a\\\\\\\"b\""),                            // backslash then quote: 2n+1 backslashes then the quote
            ("a\\\\\"b", "\"a\\\\\\\\\\\"b\""),
            ("a\\b c", "\"a\\b c\""),                                 // backslashes not before a quote stay single
            ("%TEMP%\\x", "%TEMP%\\x"),                               // %VAR% is left for the wrapper to expand
            ("a&b|c^d", "a&b|c^d"),                                   // no shell, so no cmd escaping
            ("-u", "-u"),
            ("--flag=value with space", "\"--flag=value with space\""),
            ("h\u00e9llo \u65e5\u672c", "\"h\u00e9llo \u65e5\u672c\""),
            ("line1\nline2", "\"line1\nline2\""),
        ];
        foreach (var (arg, quoted) in table)
        {
            string label = arg.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
            T.Eq("quote '" + label + "'", quoted, CommandLine.Quote(arg));
            var back = CommandLine.Split(quoted);
            T.True("split(quote) gives the argument back for '" + label + "'", back.Count == 1 && back[0] == arg, string.Join("|", back));
        }
    }

    public static void JoinTable()
    {
        T.Eq("empty list", "", CommandLine.Join([]));
        T.Eq("two plain", "-u script.py", CommandLine.Join(["-u", "script.py"]));
        T.Eq("path with spaces", "-u \"C:\\Users\\Dad\\my stocks\\ingest.py\" --day \"\"", CommandLine.Join(["-u", "C:\\Users\\Dad\\my stocks\\ingest.py", "--day", ""]));
        T.Eq("quotes inside", "echo \"he said \\\"no\\\"\"", CommandLine.Join(["echo", "he said \"no\""]));
    }

    public static void SplitTable()
    {
        // The documented Microsoft C runtime examples.
        (string Line, string[] Args)[] table =
        [
            ("\"abc\" d e", ["abc", "d", "e"]),
            ("a\\\\\\b d\"e f\"g h", ["a\\\\\\b", "de fg", "h"]),
            ("a\\\\\\\"b c d", ["a\\\"b", "c", "d"]),
            ("a\\\\\\\\\"b c\" d e", ["a\\\\b c", "d", "e"]),
            // Cadence cases.
            ("", []),
            ("   ", []),
            ("  a   b  ", ["a", "b"]),
            ("\"\"", [""]),
            ("a \"\" b", ["a", "", "b"]),
            ("a\tb", ["a", "b"]),
            ("\"a b\"c", ["a bc"]),
            ("\"unterminated quote", ["unterminated quote"]),
            ("a\"\"b", ["ab"]),
            ("\"a \"\"b\"\" c\"", ["a \"b\" c"]),                   // 2008 rule: doubled quote inside quotes is a literal quote
            ("-u \"C:\\Users\\Dad\\stocks\\ingest.py\"", ["-u", "C:\\Users\\Dad\\stocks\\ingest.py"]),
            ("%TEMP%\\x \"%A B%\"", ["%TEMP%\\x", "%A B%"]),
            ("trailing\\", ["trailing\\"]),
            ("\\\\server\\share", ["\\\\server\\share"]),
        ];
        foreach (var (line, args) in table)
        {
            var got = CommandLine.Split(line);
            T.True("split '" + line + "'", got.SequenceEqual(args), "[" + string.Join("|", got) + "]");
        }
    }

    public static void RoundTripCorpus()
    {
        var rng = new Random(20261001);
        const string alphabet = "ab \t\\\"%&-.\u00e9";
        int bad = 0;
        for (int i = 0; i < 2000; i++)
        {
            int n = rng.Next(0, 6);
            var args = new string[n];
            for (int a = 0; a < n; a++)
            {
                var chars = new char[rng.Next(0, 8)];
                for (int c = 0; c < chars.Length; c++) chars[c] = alphabet[rng.Next(alphabet.Length)];
                args[a] = new string(chars);
            }
            var back = CommandLine.Split(CommandLine.Join(args));
            if (!back.SequenceEqual(args)) { bad++; if (bad == 1) T.True("round trip", false, "[" + string.Join("|", args) + "] -> " + CommandLine.Join(args)); }
        }
        T.Eq("2000 random argument lists survive Join then Split", 0, bad);
    }

    public static void NullsAreRejected()
    {
        T.Throws<ArgumentNullException>("Quote(null)", () => CommandLine.Quote(null!));
        T.Throws<ArgumentNullException>("Join(null)", () => CommandLine.Join(null!));
        T.Throws<ArgumentNullException>("Split(null)", () => CommandLine.Split(null!));
    }
}
