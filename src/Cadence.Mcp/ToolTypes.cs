using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cadence.Mcp;

/// <summary>Who the MCP client says it is, exactly as it sent it (raw: nothing is cleaned here). Whoever shows the name to a person cleans it first.</summary>
public sealed record ClientIdentity(string Name, string? Title, string? Version)
{
    /// <summary>Title when not blank, else Name. Raw, so it can be empty or contain control characters.</summary>
    public string PreferredName => !string.IsNullOrWhiteSpace(Title) ? Title : Name;
}

/// <summary>Tool annotations as the MCP schema names them (title, readOnlyHint, destructiveHint, idempotentHint, openWorldHint).</summary>
public sealed record ToolAnnotations(string Title, bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld = false);

public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson, ToolAnnotations Annotations);

/// <summary>An MCP session as the server knows it: its id (also in every audit line), who the client says it is, and the negotiated protocol version.</summary>
public sealed record SessionInfo(string SessionId, ClientIdentity Client, string ProtocolVersion);

public sealed record CallContext(ClientIdentity Client, string SessionId, string ProtocolVersion);

/// <summary>A tool's outcome. Errors set <see cref="IsError"/> and carry a plain-sentence <see cref="Text"/>; the server wraps both into the MCP result.</summary>
public sealed record ToolResult(bool IsError, string Text, JsonObject? Structured);

public interface IToolHost
{
    IReadOnlyList<ToolDefinition> Tools { get; }

    /// <summary>Runs one tool. Validation and business problems are returned as error results; an exception is turned into an "internal" error by the server.</summary>
    ToolResult Call(string name, JsonElement arguments, CallContext context);

    bool Has(string name);
}

/// <summary>
/// [CP-R7] A tool host that can say which tools write jobs. On end of input the server drops queued write requests that have not started (nobody is
/// listening for their answer) and WAITS for a write that is already running, so a kill never lands between the two halves of a rename.
/// </summary>
public interface IWriteAwareToolHost
{
    bool IsWriteTool(string name);
}
