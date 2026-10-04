using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cadence.Agent.Tools;

/// <summary>A tool argument that is wrong. The message names the field and says how to fix it; the host turns it into an <c>invalid_argument</c> error result.</summary>
public sealed class ArgumentProblem(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>
/// Strict, hand-written argument parsing that mirrors the tool schemas exactly (PLAN 12.3): unknown properties are refused with the list of
/// allowed names, wrong types and out-of-range values are refused with the field path. Patterns are anchored with \A and \z (the schema's
/// <c>$</c> would accept a trailing newline in .NET).
/// </summary>
public sealed partial class ArgReader
{
    public const string JobIdHelp = "must be a job id: 8 characters, lowercase letters and digits only (for example d4dst0ck). Get it from cadence_list_jobs";
    public const string RunIdHelp = "must look like 20261001-180002-117 (yyyymmdd-hhmmss-fff). Get it from recentRuns in cadence_get_job";

    readonly JsonElement _args;
    readonly string _prefix;

    ArgReader(JsonElement args, string prefix = "")
    {
        _args = args;
        _prefix = prefix;
    }

    /// <summary>The field's name as an error message shows it: <c>program.path</c> inside a nested object, <c>jobId</c> at the top.</summary>
    string N(string name) => _prefix + name;

    [GeneratedRegex(@"\A[a-z0-9]{8}\z")]
    public static partial Regex JobIdPattern();

    [GeneratedRegex(@"\A[0-9]{8}-[0-9]{6}-[0-9]{3}\z")]
    public static partial Regex RunIdPattern();

    [GeneratedRegex(@"\A[0-9a-f]{12}\z")]
    public static partial Regex RevisionPattern();

    /// <summary>Opens the arguments (absent, null or {} are all "no arguments") and refuses any property that is not in <paramref name="allowed"/>.</summary>
    /// <exception cref="ArgumentProblem">The arguments are not an object, or hold an unknown property.</exception>
    public static ArgReader Open(JsonElement arguments, params string[] allowed)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new ArgReader(default);
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentProblem("The arguments must be a JSON object.", "Send the arguments as {\"name\": value, ...}.");
        foreach (JsonProperty p in arguments.EnumerateObject())
        {
            if (Array.IndexOf(allowed, p.Name) >= 0) continue;
            string list = allowed.Length == 0 ? "This tool takes no arguments." : "This tool accepts: " + string.Join(", ", allowed) + ".";
            throw new ArgumentProblem("Unknown property '" + Shorten(p.Name) + "'. " + list, allowed.Length == 0 ? "Call it with {} or without arguments." : "Remove the unknown property and check the spelling.");
        }
        return new ArgReader(arguments);
    }

    bool TryGet(string name, out JsonElement value)
    {
        value = default;
        if (_args.ValueKind != JsonValueKind.Object) return false;
        if (!_args.TryGetProperty(name, out value)) return false;
        if (value.ValueKind == JsonValueKind.Null) throw new ArgumentProblem(N(name) + " must not be null; leave it out instead.");
        return true;
    }

    /// <summary>A string argument. <paramref name="help"/> completes "name ..." when the pattern fails.</summary>
    public string? String(string name, bool required = false, int min = 0, int max = int.MaxValue, Regex? pattern = null, string? help = null)
    {
        if (!TryGet(name, out JsonElement v))
        {
            if (required) throw new ArgumentProblem(N(name) + " is required" + (help is null ? "." : ": it " + help + "."));
            return null;
        }
        if (v.ValueKind != JsonValueKind.String) throw new ArgumentProblem(N(name) + " must be a string.");
        string s = v.GetString()!;
        if (s.Length < min || s.Length > max) throw new ArgumentProblem(N(name) + " must be " + LengthText(min, max) + ".");
        if (pattern is not null && !pattern.IsMatch(s))
            throw new ArgumentProblem(N(name) + " " + (help ?? "has the wrong format") + ". Got '" + Shorten(s) + "'.");
        return s;
    }

    /// <summary>A string that must be one of <paramref name="values"/> (ordinal).</summary>
    public string? Choice(string name, IReadOnlyList<string> values)
    {
        if (!TryGet(name, out JsonElement v)) return null;
        if (v.ValueKind != JsonValueKind.String || !values.Contains(v.GetString()!, StringComparer.Ordinal))
            throw new ArgumentProblem(N(name) + " must be one of: " + string.Join(", ", values) + ".");
        return v.GetString();
    }

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>; <paramref name="fallback"/> when it is absent.</summary>
    public int Int(string name, int min, int max, int fallback)
    {
        if (!TryGet(name, out JsonElement v)) return fallback;
        string bad = N(name) + " must be a whole number from " + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + ".";
        if (v.ValueKind != JsonValueKind.Number) throw new ArgumentProblem(bad);
        long n;
        if (!v.TryGetInt64(out n))
        {
            if (!v.TryGetDouble(out double d) || d != Math.Floor(d) || Math.Abs(d) > 1e15) throw new ArgumentProblem(bad);
            n = (long)d;
        }
        if (n < min || n > max) throw new ArgumentProblem(bad);
        return (int)n;
    }

    /// <summary>An array of strings; each item is checked and named with its index (<c>cron[2]</c>). Null when absent.</summary>
    public IReadOnlyList<string>? StringArray(string name, int minItems, int maxItems, int itemMin, int itemMax)
    {
        if (!TryGet(name, out JsonElement v)) return null;
        if (v.ValueKind != JsonValueKind.Array) throw new ArgumentProblem(N(name) + " must be an array of strings, for example [\"0 18 * * 1-5\"].");
        int count = v.GetArrayLength();
        if (count < minItems || count > maxItems)
            throw new ArgumentProblem(N(name) + " must have " + minItems.ToString(CultureInfo.InvariantCulture) + " to " + maxItems.ToString(CultureInfo.InvariantCulture) + " items, but has " + count.ToString(CultureInfo.InvariantCulture) + ".");
        var list = new List<string>(count);
        int i = 0;
        foreach (JsonElement item in v.EnumerateArray())
        {
            string path = N(name) + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
            if (item.ValueKind != JsonValueKind.String) throw new ArgumentProblem(path + " must be a string.");
            string s = item.GetString()!;
            if (s.Length < itemMin || s.Length > itemMax) throw new ArgumentProblem(path + " must be " + LengthText(itemMin, itemMax) + ".");
            list.Add(s);
            i++;
        }
        return list;
    }

    /// <summary>A true/false argument; null when it is absent.</summary>
    public bool? Bool(string name)
    {
        if (!TryGet(name, out JsonElement v)) return null;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentProblem(N(name) + " must be true or false.");
        return v.GetBoolean();
    }

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>; null when it is absent.</summary>
    public int? OptionalInt(string name, int min, int max)
    {
        if (!TryGet(name, out _)) return null;
        return Int(name, min, max, min);
    }

    /// <summary>
    /// A nested object (null when absent) with its own allowed property names; its fields are named with the parent's name in error messages
    /// (<c>program.path</c>). An unknown property inside it is refused like a top-level one.
    /// </summary>
    public ArgReader? Child(string name, params string[] allowed)
    {
        if (!TryGet(name, out JsonElement v)) return null;
        if (v.ValueKind != JsonValueKind.Object) throw new ArgumentProblem(N(name) + " must be an object.", "Send " + N(name) + " as {\"field\": value, ...}.");
        foreach (JsonProperty p in v.EnumerateObject())
        {
            if (Array.IndexOf(allowed, p.Name) >= 0) continue;
            throw new ArgumentProblem("Unknown property '" + Shorten(N(name) + "." + p.Name) + "'. " + N(name) + " accepts: " + string.Join(", ", allowed) + ".", "Remove the unknown property and check the spelling.");
        }
        return new ArgReader(v, N(name) + ".");
    }

    /// <summary>
    /// An object whose values are all strings (the environment of a program), in the order given; null when absent. Only the TYPES are checked here: the
    /// count, the names and the lengths are the agent rules' business (<c>AgentInputRules.CheckEnvironment</c>), which refuse with their own message and never echo a value.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? StringMap(string name)
    {
        if (!TryGet(name, out JsonElement v)) return null;
        if (v.ValueKind != JsonValueKind.Object) throw new ArgumentProblem(N(name) + " must be an object of text values, for example {\"NAME\": \"value\"}.");
        var list = new List<KeyValuePair<string, string>>();
        int i = 0;
        foreach (JsonProperty p in v.EnumerateObject())
        {
            i++;
            if (p.Value.ValueKind != JsonValueKind.String) throw new ArgumentProblem(N(name) + " entry number " + i.ToString(CultureInfo.InvariantCulture) + " must have a text value.");
            list.Add(new KeyValuePair<string, string>(p.Name, p.Value.GetString()!));
        }
        return list;
    }

    static string LengthText(int min, int max)
    {
        string a = min.ToString(CultureInfo.InvariantCulture), b = max.ToString(CultureInfo.InvariantCulture);
        if (max == int.MaxValue) return "at least " + a + " characters long";
        return min <= 0 ? "at most " + b + " characters long" : a + " to " + b + " characters long";
    }

    /// <summary>Echoes user text in an error message without letting it grow or break lines.</summary>
    static string Shorten(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s)
        {
            if (sb.Length >= 40) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }
}
