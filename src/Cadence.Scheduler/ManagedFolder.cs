namespace Cadence.Scheduler;

/// <summary>
/// [Review R] The only two Task Scheduler folders Cadence may ever manage, in ONE place: the CLI's <c>FolderPolicy</c>, the MCP host and the
/// gateway itself all use this rule, so a third copy cannot drift and no caller can point a writer at <c>\Microsoft</c> or another product's folder.
/// The spelling is canonical (exact case) because the folder name is also part of the writer-lock mutex name, which is case-sensitive.
/// </summary>
public static class ManagedFolder
{
    public const string Product = "Cadence";
    public const string Test = "CadenceTest";

    /// <summary>The canonical spelling of an allowed folder name (case-insensitive match), or false (null, blank, paths and anything else).</summary>
    public static bool TryNormalize(string? text, out string folder)
    {
        folder = Product;
        if (text is null) return false;
        if (string.Equals(text, Product, StringComparison.OrdinalIgnoreCase)) { folder = Product; return true; }
        if (string.Equals(text, Test, StringComparison.OrdinalIgnoreCase)) { folder = Test; return true; }
        return false;
    }

    /// <summary>True only for the exact canonical spellings <c>Cadence</c> and <c>CadenceTest</c>.</summary>
    public static bool IsCanonical(string? name) => name is Product or Test;
}
