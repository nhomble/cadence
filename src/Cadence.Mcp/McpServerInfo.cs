namespace Cadence.Mcp;

/// <summary>What the server tells a client about itself in the initialize answer (serverInfo): a machine name, a display title and a version.</summary>
public sealed record McpServerInfo(string Name, string Title, string Version);

/// <summary>Settings of a server that are not part of the protocol.</summary>
public sealed record McpServerOptions
{
    /// <summary>Prefix of every stderr log line (see <see cref="StderrLog"/>).</summary>
    public string LogPrefix { get; init; } = StderrLog.DefaultPrefix;
}