using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

internal enum ResourceFieldStatus
{
    NotObject,
    Absent,
    Present,
    Invalid
}

internal readonly record struct ResourceFieldRead(ResourceFieldStatus Status, object? Value = null);

public abstract partial class TypertCodec
{
    internal virtual object? ResourceIdentity(object? decoded) => decoded;

    internal virtual ResourceFieldRead ReadResourceField(object? decoded, string field)
    {
        if (decoded is null || ReferenceEquals(decoded, Undefined.Value))
            return new(ResourceFieldStatus.NotObject);
        try
        {
            if (decoded is JsonElement json)
                return json.ValueKind != JsonValueKind.Object
                    ? new(ResourceFieldStatus.NotObject)
                    : json.TryGetProperty(field, out var value)
                        ? new(ResourceFieldStatus.Present, value.Clone())
                        : new(ResourceFieldStatus.Absent);
            var metadata = ResultMetadata;
            if (metadata?.Kind == JsonTypeInfoKind.Object)
            {
                if (decoded.GetType() != metadata.Type)
                    return new(ResourceFieldStatus.Invalid);
                var property = metadata.Properties.FirstOrDefault(property => property.Name == field);
                // Serialization-only getters did not consume input and cannot supply a resource.
                if (property is null)
                {
                    var extension = metadata.Properties.FirstOrDefault(property => property.IsExtensionData);
                    if (extension is null)
                        return new(ResourceFieldStatus.Absent);
                    if (extension.Get is null)
                        return new(ResourceFieldStatus.Invalid);
                    return extension.Get(decoded) is not { } extra
                        ? new(ResourceFieldStatus.Absent)
                        : ReadDictionaryField(extra, field);
                }

                if (property.IsExtensionData || property.Set is null && property.AssociatedParameter is null)
                    return new(ResourceFieldStatus.Absent);
                return property.Get is null
                    ? new(ResourceFieldStatus.Invalid)
                    : new(ResourceFieldStatus.Present, property.Get(decoded));
            }

            if (decoded is IDictionary dictionary)
                return ReadDictionaryField(dictionary, field);
            if (metadata?.Kind == JsonTypeInfoKind.Dictionary)
                return new(ResourceFieldStatus.Invalid);
            if (metadata?.Kind == JsonTypeInfoKind.Enumerable || decoded is Array or string ||
                decoded is bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or
                    float or double or decimal or Enum or Guid or DateTime or DateTimeOffset or DateOnly or
                    TimeOnly or TimeSpan or Uri or Version)
                return new(ResourceFieldStatus.NotObject);
            // Opaque converter/custom-codec objects need an explicit resource reader.
            return new(ResourceFieldStatus.Invalid);
        }
        catch (Exception)
        {
            return new(ResourceFieldStatus.Invalid);
        }
    }

    private static ResourceFieldRead ReadDictionaryField(object decoded, string field) => decoded switch
    {
        IDictionary dictionary => dictionary.Contains(field)
            ? new(ResourceFieldStatus.Present, dictionary[field])
            : new(ResourceFieldStatus.Absent),
        _ => new(ResourceFieldStatus.Invalid)
    };
}
