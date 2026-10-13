using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

internal static class TypertJsonMetadata
{
    private static readonly ConditionalWeakTable<JsonTypeInfo, JsonTypeInfo> inputWriters = new();
    private static readonly ConditionalWeakTable<JsonTypeInfo, JsonTypeInfo> argumentWriters = new();
    private static readonly ConditionalWeakTable<JsonTypeInfo, JsonTypeInfo> outputReaders = new();

    internal static JsonTypeInfo<T> WriteInput<T>(JsonTypeInfo<T> metadata, bool envelope = false) =>
        (JsonTypeInfo<T>)(envelope ? argumentWriters : inputWriters).GetValue(
            metadata,
            value => Project(value, input: true, envelope));

    internal static JsonTypeInfo<T> ReadOutput<T>(JsonTypeInfo<T> metadata) =>
        (JsonTypeInfo<T>)outputReaders.GetValue(metadata, value => Project(value, input: false, envelope: false));

    internal static bool RequiredOnWrite(JsonPropertyInfo property, JsonIgnoreCondition ignore) =>
        property.Get is not null && property.ShouldSerialize is null &&
        ignore != JsonIgnoreCondition.WhenWritingDefault &&
        (ignore != JsonIgnoreCondition.WhenWritingNull ||
            property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null);

    private static JsonTypeInfo Project(JsonTypeInfo metadata, bool input, bool envelope)
    {
        var resolver = metadata.Options.TypeInfoResolver ??
            throw new InvalidOperationException("Directional Remote data requires a JSON metadata resolver.");
        var options = new JsonSerializerOptions(metadata.Options)
        {
            TypeInfoResolver = resolver.WithAddedModifier(type =>
            {
                if (type.Kind != JsonTypeInfoKind.Object || input && envelope && type.Type == metadata.Type)
                    return;
                foreach (var property in type.Properties)
                {
                    if (input)
                    {
                        if (property.Get is null)
                            continue;
                        property.ShouldSerialize = null;
                        property.IsGetNullable = property.IsSetNullable;
                    }
                    else
                    {
                        property.IsRequired = RequiredOnWrite(property, metadata.Options.DefaultIgnoreCondition);
                        property.IsSetNullable = property.IsGetNullable;
                    }
                }
            })
        };
        if (input)
            options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        else
            options.RespectRequiredConstructorParameters = false;
        options.MakeReadOnly();
        return options.GetTypeInfo(metadata.Type);
    }
}
