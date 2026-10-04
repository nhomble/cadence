namespace Cadence.Core.Planning;

/// <summary>
/// One English difference ("Time", "18:00", "19:00"). <c>Before</c> is the old value (for drift: what Cadence wrote),
/// <c>After</c> the new one (for drift: what Task Scheduler holds). Both null means the field only names the change ("Program").
/// </summary>
public sealed record FieldDiff(string Field, string? Before, string? After)
{
    /// <summary>"Time 18:00 → 19:00", "Disabled", or just the field name.</summary>
    public string Describe()
    {
        if (Field == "Enabled" && After is not null)
            return string.Equals(After, "disabled", StringComparison.Ordinal) ? "Disabled" : "Enabled";
        if (Before is null && After is null) return Field;
        return Field + " " + (Before ?? "(none)") + " → " + (After ?? "(none)");
    }
}
