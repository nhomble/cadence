namespace Cadence.Mcp;

/// <summary>MCP protocol versions this server speaks (PLAN 12.0). 2025-03-26 needed JSON-RPC batches, so it is not offered.</summary>
public static class McpProtocol
{
    public const string Latest = "2025-11-25";

    public static readonly IReadOnlyList<string> Supported = ["2025-11-25", "2025-06-18"];

    /// <summary>The requested version when supported, otherwise the latest one (the client then decides whether it can use it).</summary>
    public static string Negotiate(string? requested)
    {
        foreach (string s in Supported)
            if (string.Equals(s, requested, StringComparison.Ordinal)) return s;
        return Latest;
    }
}
