using System.Text.Json.Serialization.Metadata;

namespace Cordis.Typert;

internal static class ClientMetadata
{
    internal static bool IsBinary(JsonTypeInfo? metadata) =>
        metadata?.Type == typeof(byte[]) && ReferenceEquals(
            metadata.Converter,
            JsonMetadataServices.ByteArrayConverter);

    internal static JsonTypeInfo? Property(JsonTypeInfo? metadata, string name)
    {
        if (metadata?.Kind == JsonTypeInfoKind.Dictionary && metadata.ElementType is { } elementType)
            return metadata.Options.GetTypeInfo(elementType);
        var property = metadata?.Kind == JsonTypeInfoKind.Object
            ? metadata.Properties.FirstOrDefault(property => property.Name == name)
            : null;
        return property is { CustomConverter: null }
            ? metadata!.Options.GetTypeInfo(property.PropertyType)
            : null;
    }
}
