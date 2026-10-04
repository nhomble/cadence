using System.Text.Json.Serialization;

namespace Cadence.Core.Metadata;

/// <summary>Source-generated serializer metadata for the metadata DTOs (no reflection). Property names are set explicitly on the DTOs.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MetadataDto))]
internal sealed partial class MetadataJsonContext : JsonSerializerContext
{
}
