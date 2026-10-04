using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cadence.Core.Metadata;

// PLAN 12.4.1: the optional attribution and lock fields are tolerated when a hand edit or a future writer stores the wrong JSON type:
// the value reads as "not there" (unknown) instead of making the whole job CorruptMetadata; the lock instead fails closed (see below).

internal sealed class LenientStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value);
    }
}

/// <summary>
/// [Review R] The per-job agent lock fails CLOSED: a present value of the wrong type ("yes", 1, an object) reads as locked, because a damaged
/// safety switch must not quietly let agents in. Absent or null is "not locked" (v0.1.0 jobs have no field). The owner sees the lock and can clear it.
/// </summary>
internal sealed class AgentLockJsonConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
        if (reader.TokenType == JsonTokenType.Null) return null;
        reader.Skip();
        return true;
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is { } b) writer.WriteBooleanValue(b); else writer.WriteNullValue();
    }
}
