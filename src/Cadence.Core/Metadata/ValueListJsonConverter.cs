using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cadence.Core.Model;

namespace Cadence.Core.Metadata;

/// <summary>
/// JSON array converter for <see cref="ValueList{T}"/>. Used by attribute on DTO properties (a closed generic per use), so it needs no
/// reflection and works with the source-generated context. Elements are handled through the context's own type info.
/// </summary>
public sealed class ValueListJsonConverter<T> : JsonConverter<ValueList<T>>
{
    public override ValueList<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected a JSON array");
        var info = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var items = new List<T>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return new ValueList<T>(items);
            T? item = JsonSerializer.Deserialize(ref reader, info);
            if (item is null) throw new JsonException("null is not allowed in this array");
            items.Add(item);
        }
        throw new JsonException("unterminated JSON array");
    }

    public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options)
    {
        var info = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        writer.WriteStartArray();
        foreach (T item in value) JsonSerializer.Serialize(writer, item, info);
        writer.WriteEndArray();
    }
}
