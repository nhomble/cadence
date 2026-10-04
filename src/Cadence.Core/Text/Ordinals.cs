using System.Globalization;
using Cadence.Core.Model;

namespace Cadence.Core.Text;

/// <summary>Small invariant-English helpers: ordinals, week words, list joining.</summary>
public static class Ordinals
{
    /// <summary>1 -> "1st", 2 -> "2nd", 11 -> "11th", 22 -> "22nd", 31 -> "31st".</summary>
    public static string Of(int n)
    {
        int mod100 = n % 100;
        string suffix = mod100 is >= 11 and <= 13 ? "th" : (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>"first", "second", "third", "fourth", "last".</summary>
    public static string WeekWord(WeekOfMonth w) => w switch
    {
        WeekOfMonth.First => "first",
        WeekOfMonth.Second => "second",
        WeekOfMonth.Third => "third",
        WeekOfMonth.Fourth => "fourth",
        _ => "last",
    };

    /// <summary>"a", "a and b", "a, b and c" (no Oxford comma). Empty input gives an empty string.</summary>
    public static string JoinAnd(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => "",
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
        };
    }
}
