using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

public abstract partial class TypertCodec
{
    private static JsonTypeInfo<T> ApplyAccessorNullability<T>(JsonTypeInfo<T> metadata, TypertNullability query)
    {
        var visited = new HashSet<(Type, int)>();
        var annotations = new Dictionary<Type, Dictionary<string, (bool? Getter, bool? Setter)>>();
        var changed = false;
        Visit(metadata, []);
        if (!changed)
            return metadata;

        var resolver = metadata.Options.TypeInfoResolver ??
            throw new InvalidOperationException("Declared accessor annotations require a JSON metadata resolver.");
        var options = new JsonSerializerOptions(metadata.Options)
        {
            TypeInfoResolver = resolver.WithAddedModifier(type =>
            {
                if (!annotations.TryGetValue(type.Type, out var members))
                    return;
                foreach (var property in type.Properties)
                    if (members.TryGetValue(property.Name, out var annotation))
                    {
                        if (annotation.Getter is { } getter)
                            property.IsGetNullable = getter;
                        if (annotation.Setter is { } setter)
                            property.IsSetNullable = setter;
                    }
            })
        };
        options.MakeReadOnly();
        return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));

        void Visit(JsonTypeInfo type, string[] path)
        {
            if (query(path) is not { } declaration || !visited.Add((type.Type, declaration.Id)))
                return;
            if (type.Kind == JsonTypeInfoKind.Object)
            {
                foreach (var property in type.Properties)
                {
                    string[] child = [.. path, "properties", property.Name];
                    if (query(child) is not { } member)
                        continue;
                    if (!property.PropertyType.IsValueType &&
                        (member.GetterNullable is not null || member.SetterNullable is not null))
                    {
                        if (!annotations.TryGetValue(type.Type, out var members))
                            annotations.Add(type.Type, members = new(StringComparer.Ordinal));
                        var value = (member.GetterNullable, member.SetterNullable);
                        if (members.TryGetValue(property.Name, out var existing) && existing != value)
                            throw new InvalidOperationException(
                                "Conflicting accessor annotations for " + property.Name);
                        members[property.Name] = value;
                        changed |= member.GetterNullable is { } getter && getter != property.IsGetNullable ||
                            member.SetterNullable is { } setter && setter != property.IsSetNullable;
                    }

                    if (property.CustomConverter is null)
                        Visit(type.Options.GetTypeInfo(property.PropertyType), child);
                }
            }
            else if (type.ElementType is { } element)
                Visit(
                    type.Options.GetTypeInfo(element),
                    [.. path, type.Kind == JsonTypeInfoKind.Dictionary ? "additionalProperties" : "items"]);
        }
    }
}
