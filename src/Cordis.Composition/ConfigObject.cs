namespace Cordis.Composition;

/// <summary>Composes explicit data-object fields into the existing typed configuration contract.</summary>
/// <typeparam name="T">The value returned by the author's validator.</typeparam>
/// <remarks>
/// This helper does not validate, infer members, or apply defaults. Declare every persisted and
/// ordinary field, using plain projections that the validator can read back. Defaults must agree
/// with that validator. Nested live paths and custom conversions use ConfigSchema directly.
/// Declarations are immutable and retain callbacks only with their owning plugin, without caches.
/// </remarks>
public sealed class ConfigObject<T>
{
    private readonly Func<object?, ConfigResult<T>> _validate;
    private readonly IReadOnlyList<FieldDefinition> _fields;

    private ConfigObject(Func<object?, ConfigResult<T>> validate, IReadOnlyList<FieldDefinition> fields)
    {
        _validate = validate;
        _fields = fields;
    }

    /// <summary>Use the existing validator as the sole authority for accepted effective values.</summary>
    public static ConfigObject<T> Create(Func<object?, ConfigResult<T>> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        return new(validate, []);
    }

    /// <summary>Declare one raw key, its description, and a complete plain-value projection.</summary>
    /// <typeparam name="TValue">The projected value and, for a live field, its reference type.</typeparam>
    /// <remarks>Live fields must be marked on this descriptor itself, rather than on a nested child.</remarks>
    public ConfigObject<T> Field<TValue>(string name, ConfigDescriptor descriptor, Func<T, TValue> project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(project);
        var visited = new HashSet<ConfigDescriptor>();
        bool HasNestedLive(ConfigDescriptor node)
        {
            if (!visited.Add(node)) return false;
            if (node.IsVolatile) return true;
            return node.Properties.Values.Any(HasNestedLive)
                || node.Children.Any(HasNestedLive)
                || node.Inner is not null && HasNestedLive(node.Inner)
                || node.Key is not null && HasNestedLive(node.Key);
        }
        if (descriptor.Properties.Values.Any(HasNestedLive) || descriptor.Children.Any(HasNestedLive)
            || descriptor.Inner is not null && HasNestedLive(descriptor.Inner)
            || descriptor.Key is not null && HasNestedLive(descriptor.Key))
            throw new ArgumentException("Nested live fields require explicit ConfigSchema bindings.", nameof(descriptor));
        if (_fields.Any(field => field.Name == name))
            throw new ArgumentException($"Duplicate configuration member '{name}'.", nameof(name));

        var field = new FieldDefinition(name, descriptor, value => project(value),
            schema => descriptor.IsVolatile ? schema.WithVolatile(name, project) : schema);
        return new(_validate, Array.AsReadOnly(_fields.Append(field).ToArray()));
    }

    /// <summary>Build a schema with live bindings, ordinary comparison and complete persistence.</summary>
    /// <remarks>All declared fields, including defaults, are saved. Use ConfigSchema.WithSimplify for custom omission.</remarks>
    public ConfigSchema<T> Build()
    {
        var descriptor = ConfigDescriptor.Object(_fields.Select(field => (field.Name, field.Descriptor)).ToArray());
        var schema = new ConfigSchema<T>(_validate, descriptor);
        foreach (var field in _fields)
            schema = field.Bind(schema);

        return schema.WithOrdinaryEquality(OrdinaryEquals)
            .WithSimplify(Project)
            .WithDescriptionData(Project);
    }

    private bool OrdinaryEquals(T left, T right)
    {
        foreach (var field in _fields)
            if (!field.Descriptor.IsVolatile && !ConfigDescriptor.StrictEquals(field.Project(left), field.Project(right)))
                return false;
        return true;
    }

    private object Project(T value)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in _fields)
            result.Add(field.Name, field.Project(value));
        return result;
    }

    private sealed record FieldDefinition(string Name, ConfigDescriptor Descriptor,
        Func<T, object?> Project, Func<ConfigSchema<T>, ConfigSchema<T>> Bind);
}
