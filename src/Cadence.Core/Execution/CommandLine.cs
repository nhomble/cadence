using System.Text;

namespace Cadence.Core.Execution;

/// <summary>
/// Windows command-line quoting for building a <c>RunSpec.Args</c> tail from separate arguments, and the inverse
/// (the CommandLineToArgvW rules), so the editor can show and edit arguments one per row. <c>%VAR%</c> and cmd metacharacters are
/// deliberately left alone: the wrapper expands <c>%VAR%</c> in Args, and the child receives the string verbatim (no shell).
/// </summary>
public static class CommandLine
{
    /// <summary>Quotes one argument so that <see cref="Split"/> (and CommandLineToArgvW) returns it unchanged.</summary>
    public static string Quote(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length > 0 && arg.AsSpan().IndexOfAny(" \t\n\v\"") < 0) return arg;

        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        for (int i = 0; ; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { i++; backslashes++; }
            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);   // trailing backslashes must not escape the closing quote
                break;
            }
            if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The characters (besides whitespace and the double quote) that cmd.exe gives a meaning to, or splits arguments at.</summary>
    const string CmdSpecials = "&|<>^()%!,;=";

    /// <summary>
    /// [CP-A] Quotes one argument for a batch script (<c>.bat</c>/<c>.cmd</c>), which cmd.exe re-parses. <see cref="Quote"/> is not enough there:
    /// an unquoted <c>&amp;</c>, <c>|</c>, <c>&lt;</c>, <c>&gt;</c>, <c>^</c>, <c>(</c>, <c>)</c> or <c>%</c> ends the argument and starts another command, and cmd
    /// also splits arguments at <c>,</c> <c>;</c> and <c>=</c>. So an argument that is empty or holds whitespace, a quote or any of
    /// <c>&amp;|&lt;&gt;^()%!,;=</c> is wrapped in double quotes, with every embedded double quote doubled (<c>""</c> keeps cmd's quote state balanced,
    /// so everything between stays literal). Backslashes are not special to cmd and are left alone. Other arguments are returned unchanged.
    /// Limits cmd.exe imposes: <c>%NAME%</c> is expanded even inside quotes (here the wrapper expands it first, on purpose), and a carriage
    /// return, line feed or NUL can't be carried at all, so those throw <see cref="ArgumentException"/>.
    /// </summary>
    public static string QuoteForCmd(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.AsSpan().IndexOfAny("\r\n\0") >= 0)
            throw new ArgumentException("A command-line argument for a batch file can't contain a line break.", nameof(arg));
        bool plain = arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"' || CmdSpecials.Contains(c, StringComparison.Ordinal));
        if (plain) return arg;
        return "\"" + arg.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Joins arguments with <see cref="QuoteForCmd"/>.</summary>
    public static string JoinForCmd(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return string.Join(' ', args.Select(QuoteForCmd));
    }

    public static string Join(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return string.Join(' ', args.Select(Quote));
    }

    /// <summary>Splits a command-line tail into arguments (Microsoft C runtime rules, 2008 and later: <c>""</c> inside quotes is a literal quote).</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false, started = false;
        int i = 0;
        while (i < commandLine.Length)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { n++; i++; }
                started = true;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    cur.Append('\\', n / 2);
                    if (n % 2 == 1) { cur.Append('"'); i++; }       // odd: the quote is literal
                    // even: the quote is processed by the next loop pass
                }
                else cur.Append('\\', n);
                continue;
            }
            if (c == '"')
            {
                started = true;
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { cur.Append('"'); i += 2; continue; }
                inQuotes = !inQuotes;
                i++;
                continue;
            }
            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (started) { result.Add(cur.ToString()); cur.Clear(); started = false; }
                i++;
                continue;
            }
            cur.Append(c);
            started = true;
            i++;
        }
        if (started) result.Add(cur.ToString());
        return result;
    }
}
