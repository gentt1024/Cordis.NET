using System.Collections;
using System.Collections.ObjectModel;

namespace Cordis;

/// <summary>An immutable declaration of a configuration value's observable shape.</summary>
/// <remarks>It describes the captured validator; it is not a second validator. Lazy builders run during configuration resolution, never during capture or raw comparison.</remarks>
public sealed partial class ConfigDescriptor
{
    private ConfigDescriptor(
        string kind,
        IReadOnlyDictionary<string, ConfigDescriptor>? properties = null,
        ConfigDescriptor? inner = null,
        bool optional = false,
        bool volatileValue = false,
        bool hasDefault = false,
        object? defaultValue = null,
        Func<ConfigDescriptor>? lazy = null,
        IReadOnlyList<ConfigDescriptor>? children = null,
        ConfigDescriptor? key = null,
        object? lazyIdentity = null,
        Func<object?, int>? selectBranch = null,
        IReadOnlyDictionary<string, object?>? annotations = null)
    {
        Kind = kind;
        Properties = properties ?? EmptyProperties;
        Inner = inner;
        IsOptional = optional;
        IsVolatile = volatileValue;
        HasDefault = hasDefault;
        DefaultValue = defaultValue;
        Children = children ?? System.Array.Empty<ConfigDescriptor>();
        Key = key;
        _lazy = lazy;
        _lazyIdentity = lazy is null ? null : lazyIdentity ?? new object();
        _selectBranch = selectBranch;
        Annotations = annotations ?? EmptyAnnotations;
    }

    private static readonly IReadOnlyDictionary<string, ConfigDescriptor> EmptyProperties =
        new ReadOnlyDictionary<string, ConfigDescriptor>(new Dictionary<string, ConfigDescriptor>());

    private readonly Func<ConfigDescriptor>? _lazy;
    private readonly object? _lazyIdentity;
    private readonly Func<object?, int>? _selectBranch;

    /// <summary>The declared kind (any, number, string, boolean, object, array, or lazy).</summary>
    public string Kind
    {
        get;
    }

    /// <summary>Declared object members, copied when authored and captured.</summary>
    public IReadOnlyDictionary<string, ConfigDescriptor> Properties
    {
        get;
        private set;
    }

    /// <summary>The array element or resolved lazy target.</summary>
    public ConfigDescriptor? Inner
    {
        get;
        private set;
    }

    /// <summary>Whether an omitted value is admitted by its validator.</summary>
    public bool IsOptional
    {
        get;
    }

    /// <summary>Whether changes at this boundary may use stable references.</summary>
    public bool IsVolatile
    {
        get;
    }

    /// <summary>Whether this declaration supplies a default.</summary>
    public bool HasDefault
    {
        get;
    }

    /// <summary>The detached immutable declared default.</summary>
    public object? DefaultValue
    {
        get;
    }

    /// <summary>Tuple, union, or intersection child declarations in authoring order.</summary>
    public IReadOnlyList<ConfigDescriptor> Children
    {
        get;
        private set;
    }

    /// <summary>The optional dictionary key declaration.</summary>
    public ConfigDescriptor? Key
    {
        get;
        private set;
    }

    /// <summary>Describe an unconstrained value; the validator still owns acceptance.</summary>
    public static ConfigDescriptor Any() => new("any");

    /// <summary>Describe a numeric value.</summary>
    public static ConfigDescriptor Number() => new("number");

    /// <summary>Describe a string value.</summary>
    public static ConfigDescriptor String() => new("string");

    /// <summary>Describe a boolean value.</summary>
    public static ConfigDescriptor Boolean() => new("boolean");

    /// <summary>Describe an object without retaining the caller's member array.</summary>
    public static ConfigDescriptor Object(params (string Name, ConfigDescriptor Descriptor)[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var properties = new Dictionary<string, ConfigDescriptor>(StringComparer.Ordinal);
        foreach (var (name, descriptor) in members)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!properties.TryAdd(name, descriptor))
                throw new ArgumentException($"Duplicate configuration member '{name}'.", nameof(members));
        }

        return new("object", new ReadOnlyDictionary<string, ConfigDescriptor>(properties));
    }

    /// <summary>Describe an array's element shape.</summary>
    public static ConfigDescriptor Array(ConfigDescriptor element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new("array", inner: element);
    }

    /// <summary>Describe a dictionary, whose dynamic value/key placements cannot contain volatile fields.</summary>
    public static ConfigDescriptor Dict(ConfigDescriptor value, ConfigDescriptor? key = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new("dict", inner: value, key: key);
    }

    private static ConfigDescriptor List(string kind, ConfigDescriptor[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        foreach (var child in children)
            ArgumentNullException.ThrowIfNull(child);
        return new(kind, children: System.Array.AsReadOnly(children.ToArray()));
    }

    /// <summary>Describe an ordered tuple without retaining the caller's array.</summary>
    public static ConfigDescriptor Tuple(params ConfigDescriptor[] children) => List("tuple", children);

    /// <summary>Retain alternative shapes; branch selection remains owned by the bundled validator.</summary>
    public static ConfigDescriptor Union(params ConfigDescriptor[] children) => List("union", children);

    /// <summary>Select the validator's active union branch when resolving lazy description nodes.</summary>
    /// <remarks>The author supplies the same branch decision as the validator. The callback reads description input; it does not validate or convert and never runs during capture or raw comparison.</remarks>
    public static ConfigDescriptor Union(Func<object?, int> selectBranch, params ConfigDescriptor[] children)
    {
        ArgumentNullException.ThrowIfNull(selectBranch);
        var union = List("union", children);
        return new("union", children: union.Children, selectBranch: selectBranch);
    }

    /// <summary>Retain intersecting shapes; conversion remains owned by the bundled validator.</summary>
    public static ConfigDescriptor Intersect(params ConfigDescriptor[] children) => List("intersect", children);

    /// <summary>Retain a conversion's input declaration without storing or executing a second transform.</summary>
    public static ConfigDescriptor Transform(ConfigDescriptor input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new("transform", inner: input);
    }

    /// <summary>Retain a getter's result declaration without storing or invoking a second getter.</summary>
    public static ConfigDescriptor Getter(ConfigDescriptor result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new("getter", inner: result);
    }

    /// <summary>Describe a recursive or delayed shape. Actual configuration resolution materializes each captured builder once.</summary>
    public static ConfigDescriptor Lazy(Func<ConfigDescriptor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new("lazy", lazy: builder);
    }

    private ConfigDescriptor Copy(
        bool? optional = null,
        bool? volatileValue = null,
        bool? hasDefault = null,
        object? defaultValue = null) =>
        new(
            Kind,
            Properties,
            Inner,
            optional ?? IsOptional,
            volatileValue ?? IsVolatile,
            hasDefault ?? HasDefault,
            hasDefault == true ? defaultValue : DefaultValue,
            _lazy,
            Children,
            Key,
            _lazyIdentity,
            _selectBranch,
            Annotations);

    /// <summary>Mark omission as admitted, preserving the underlying declaration.</summary>
    public ConfigDescriptor Optional() => Copy(optional: true);

    /// <summary>Describe a required value; the bundled validator owns missing-value rejection.</summary>
    public ConfigDescriptor Required() => Copy(optional: false);

    /// <summary>Declare an immutable plain-value default.</summary>
    public ConfigDescriptor Default(object? value) =>
        ConfigSnapshots.TryCreate(value, out var snapshot)
            ? Copy(hasDefault: true, defaultValue: snapshot)
            : throw new ArgumentException("A default must be an acyclic plain value.", nameof(value));

    /// <summary>Mark this value as a stable-reference boundary.</summary>
    public ConfigDescriptor Volatile() =>
        IsVolatile
            ? throw new InvalidOperationException("This declaration is already volatile.")
            : Copy(volatileValue: true);

    /// <summary>Reuse this immutable declaration as a shared graph node.</summary>
    public ConfigDescriptor Alias() => this;

    internal ConfigDescriptor Capture()
    {
        var seen = new Dictionary<ConfigDescriptor, ConfigDescriptor>(ReferenceEqualityComparer.Instance);
        var lazyTargets = new Dictionary<object, ConfigDescriptor>(ReferenceEqualityComparer.Instance);

        ConfigDescriptor Visit(ConfigDescriptor source)
        {
            if (seen.TryGetValue(source, out var previous))
                return previous;
            var result = new ConfigDescriptor(
                source.Kind,
                optional: source.IsOptional,
                volatileValue: source.IsVolatile,
                hasDefault: source.HasDefault,
                defaultValue: source.DefaultValue,
                lazy: source._lazy is null
                    ? null
                    : () =>
                    {
                        if (lazyTargets.TryGetValue(source._lazyIdentity!, out var known))
                            return known;
                        var target = Visit(
                            source._lazy() ??
                            throw new InvalidOperationException("A configuration lazy builder returned null."));
                        lazyTargets.Add(source._lazyIdentity!, target);
                        return target;
                    },
                selectBranch: source._selectBranch,
                annotations: source.Annotations);
            seen.Add(source, result);
            result.Properties = new ReadOnlyDictionary<string, ConfigDescriptor>(
                source.Properties.ToDictionary(pair => pair.Key, pair => Visit(pair.Value), StringComparer.Ordinal));
            result.Children = System.Array.AsReadOnly(source.Children.Select(Visit).ToArray());
            result.Key = source.Key is null ? null : Visit(source.Key);
            var target = source.Inner;
            result.Inner = target is null ? null : Visit(target);
            return result;
        }

        return Visit(this);
    }

    internal void ResolveLazy(object? configuration)
    {
        var seen = new Dictionary<ConfigDescriptor, HashSet<object?>>(ReferenceEqualityComparer.Instance);

        void Visit(ConfigDescriptor descriptor, object? value)
        {
            if (value is null || ReferenceEquals(value, Undefined.Value))
                value = descriptor.HasDefault ? descriptor.DefaultValue : Undefined.Value;
            if (value is null || ReferenceEquals(value, Undefined.Value))
                return;
            if (!seen.TryGetValue(descriptor, out var values))
                seen.Add(descriptor, values = new HashSet<object?>(ReferenceEqualityComparer.Instance));
            if (!values.Add(value))
                return;
            if (descriptor.Kind == "lazy")
            {
                descriptor.Inner ??= descriptor._lazy?.Invoke();
                if (descriptor.Inner is not null)
                    Visit(descriptor.Inner, value);
            }
            else if (descriptor.Kind is "object" or "dict" && ConfigSnapshots.TryMembers(value, out var members))
            {
                if (descriptor.Kind == "object")
                    foreach (var (name, child) in descriptor.Properties)
                        Visit(child, members.GetValueOrDefault(name, Undefined.Value));
                else
                    foreach (var (name, child) in members)
                    {
                        if (descriptor.Key is not null)
                            Visit(descriptor.Key, name);
                        if (descriptor.Inner is not null)
                            Visit(descriptor.Inner, child);
                    }
            }
            else if (descriptor.Kind is "array" or "tuple" && value is IEnumerable items && value is not string)
            {
                var index = 0;
                foreach (var item in items)
                {
                    var child = descriptor.Kind == "array"
                        ? descriptor.Inner
                        : descriptor.Children.ElementAtOrDefault(index++);
                    if (child is not null)
                        Visit(child, item);
                }
            }
            else if (descriptor.Kind is "transform" or "getter" && descriptor.Inner is not null)
                Visit(descriptor.Inner, value);
            else if (descriptor.Kind == "union" && descriptor.ContainsLazy())
            {
                if (descriptor._selectBranch is null)
                    throw new ConfigurationValidationException(
                        ["A union containing lazy declarations requires an explicit branch selector."]);
                var branch = descriptor._selectBranch(value);
                if (branch < 0 || branch >= descriptor.Children.Count)
                    throw new ConfigurationValidationException(
                        ["The configuration union branch selector returned an invalid index."]);
                Visit(descriptor.Children[branch], value);
            }
            else if (descriptor.Kind is "union" or "intersect")
                foreach (var child in descriptor.Children)
                    Visit(child, value);
            else if (descriptor.Kind is "object" or "dict" or "array" or "tuple" && descriptor.ContainsLazy())
                throw new ConfigurationValidationException(
                [
                    "Lazy descriptions require a plain input shape; use WithDescriptionData for typed configuration."
                ]);
        }

        Visit(this, configuration);
    }

    private bool ContainsLazy()
    {
        var seen = new HashSet<ConfigDescriptor>(ReferenceEqualityComparer.Instance);

        bool Visit(ConfigDescriptor descriptor) =>
            seen.Add(descriptor) && (descriptor.Kind == "lazy" ||
                descriptor.Inner is not null && Visit(descriptor.Inner) ||
                descriptor.Key is not null && Visit(descriptor.Key) || descriptor.Properties.Values.Any(Visit) ||
                descriptor.Children.Any(Visit));

        return Visit(this);
    }

    /// <summary>Compare raw values without validators, projectors, or lazy callbacks.</summary>
    /// <remarks>Unknown changed members and opaque changed values require ordinary lifecycle updates.</remarks>
    public bool IsVolatileOnly(object? oldRaw, object? newRaw) =>
        Compare(oldRaw, newRaw, new HashSet<ConfigDescriptor>(ReferenceEqualityComparer.Instance), true);

    /// <summary>Strict fixed-source value equality, including opaque identity and missing-key Undefined semantics.</summary>
    /// <remarks>CLR numeric widths represent the source Number domain; DateTime/DateTimeOffset, Uri, Regex, and byte memory adapt its dedicated value cases.</remarks>
    public static bool StrictEquals(object? left, object? right) => ConfigSnapshots.Equal(left, right);

    internal bool EffectiveEquals(object? left, object? right) =>
        Compare(left, right, new HashSet<ConfigDescriptor>(ReferenceEqualityComparer.Instance), false);

    private bool Compare(object? left, object? right, HashSet<ConfigDescriptor> visiting, bool raw)
    {
        if (IsVolatile)
            return true;
        if (Kind != "object" || !visiting.Add(this))
            return ConfigSnapshots.Equal(left, right);
        try
        {
            if (raw)
            {
                if (left is null || ReferenceEquals(left, Undefined.Value))
                    left = HasDefault ? DefaultValue : Undefined.Value;
                if (right is null || ReferenceEquals(right, Undefined.Value))
                    right = HasDefault ? DefaultValue : Undefined.Value;
                // The marker is raw transport, not a schema member. Its parent remains
                // opaque even if a described child is named __jsExpr and is volatile.
                if (IsRawExpression(left) || IsRawExpression(right))
                    return ConfigSnapshots.Equal(left, right);
            }

            if (ConfigSnapshots.TryMembers(left, out var a) && ConfigSnapshots.TryMembers(right, out var b))
            {
                foreach (var name in a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal))
                {
                    var x = a.GetValueOrDefault(name, Undefined.Value);
                    var y = b.GetValueOrDefault(name, Undefined.Value);
                    if (Properties.TryGetValue(name, out var child)
                            ? !child.Compare(x, y, visiting, raw)
                            : !ConfigSnapshots.Equal(x, y))
                        return false;
                }

                return true;
            }

            return ConfigSnapshots.Equal(left, right);
        }
        finally
        {
            visiting.Remove(this);
        }
    }

    private static bool IsRawExpression(object? value) =>
        ConfigSnapshots.TryMembers(value, out var members) && members.ContainsKey("__jsExpr");

    internal bool HasBlockedVolatilePlacement()
    {
        var visited = new HashSet<(ConfigDescriptor, bool)>();

        bool Visit(ConfigDescriptor descriptor, bool blocked)
        {
            if (!visited.Add((descriptor, blocked)))
                return false;
            if (descriptor.IsVolatile && blocked)
                return true;
            if (descriptor.Inner is not null && Visit(descriptor.Inner, true))
                return true;
            if (descriptor.Key is not null && Visit(descriptor.Key, true))
                return true;
            if (descriptor.Children.Any(child => Visit(child, true)))
                return true;
            return descriptor.Properties.Values.Any(child => Visit(
                child,
                blocked || descriptor.IsVolatile || descriptor.Kind != "object"));
        }

        return Visit(this, false);
    }

    internal void ValidateBindings(IReadOnlyList<ConfigBinding> bindings)
    {
        var paths = new List<string[]>();
        var ancestors = new HashSet<ConfigDescriptor>(ReferenceEqualityComparer.Instance);

        void Visit(ConfigDescriptor descriptor, string[] path)
        {
            if (descriptor.IsVolatile)
            {
                paths.Add(path);
                return;
            }

            if (!ancestors.Add(descriptor))
                return;
            try
            {
                if (descriptor.Kind == "object")
                    foreach (var (name, child) in descriptor.Properties)
                        Visit(child, [.. path, name]);
            }
            finally
            {
                ancestors.Remove(descriptor);
            }
        }

        Visit(this, []);
        foreach (var path in paths)
            if (!bindings.Any(binding => binding.Keys.SequenceEqual(path, StringComparer.Ordinal)))
                throw new ConfigurationValidationException(
                    [$"Volatile field '{ConfigBinding.DisplayPath(path)}' requires an explicit typed projection."]);
        foreach (var binding in bindings)
            if (!paths.Any(path => binding.Keys.SequenceEqual(path, StringComparer.Ordinal)))
                throw new ConfigurationValidationException(
                    [$"Projection '{binding.Path}' must name a declared fixed volatile field."]);
    }

    /// <summary>Return a detached plain raw value with declared defaults omitted.</summary>
    public object? Simplify(object? raw)
    {
        var visiting = new HashSet<(ConfigDescriptor, object?)>();

        object? Visit(ConfigDescriptor descriptor, object? value)
        {
            if (!visiting.Add((descriptor, value)))
                throw new ArgumentException("Cannot simplify a recursive raw graph.", nameof(raw));
            try
            {
                if (descriptor.HasDefault && ConfigSnapshots.Equal(
                        value,
                        descriptor.DefaultValue,
                        descriptor.Kind == "dict"))
                    return null;
                if (value is null || ReferenceEquals(value, Undefined.Value))
                    return value;
                if (descriptor.Kind == "lazy" && descriptor.Inner is not null)
                    return Visit(descriptor.Inner, value);
                if (descriptor.Kind is "object" or "dict" && ConfigSnapshots.TryMembers(value, out var members))
                {
                    var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (name, item) in members)
                    {
                        var child = descriptor.Kind == "dict"
                            ? descriptor.Inner
                            : descriptor.Properties.GetValueOrDefault(name);
                        if (child is null)
                            continue;
                        var simplified = Visit(child, item);
                        if (descriptor.Kind == "dict" ||
                            simplified is not null && !ReferenceEquals(simplified, Undefined.Value))
                            result[name] = simplified;
                    }

                    if (descriptor.HasDefault && ConfigSnapshots.Equal(
                            result,
                            descriptor.DefaultValue,
                            descriptor.Kind == "dict"))
                        return null;
                    return ConfigSnapshots.Create(result);
                }

                if (descriptor.Kind is "array" or "tuple" && value is IList list)
                    return System.Array.AsReadOnly(
                        list
                            .Cast<object?>()
                            .Select((item, index) =>
                            {
                                var child = descriptor.Kind == "array"
                                    ? descriptor.Inner
                                    : descriptor.Children.ElementAtOrDefault(index);
                                return child is null ? ConfigSnapshots.Create(item) : Visit(child, item);
                            })
                            .ToArray());
                if (descriptor.Kind == "intersect")
                {
                    var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var child in descriptor.Children)
                        if (ConfigSnapshots.TryMembers(Visit(child, value), out var portion))
                            foreach (var pair in portion)
                                merged[pair.Key] = pair.Value;
                    return ConfigSnapshots.Create(merged);
                }

                if (descriptor.Kind == "union")
                    throw new InvalidOperationException(
                        "Union persistence requires an explicit typed WithSimplify projection; descriptors do not select validator branches.");
                return ConfigSnapshots.Create(value);
            }
            finally
            {
                visiting.Remove((descriptor, value));
            }
        }

        return Visit(this, raw);
    }
}

/// <summary>A typed validator and its optional immutable description, with explicit AOT-safe field projections.</summary>
/// <typeparam name="T">The existing plugin configuration type.</typeparam>
public sealed class ConfigSchema<T>
{
    private readonly IReadOnlyList<ConfigBinding> _bindings;
    private readonly Func<T, T, bool>? _ordinaryEquality;
    private readonly Func<T, object?>? _simplify;
    private readonly Func<T, object?>? _descriptionData;

    /// <summary>Bind a description to the exact validator used during plugin capture.</summary>
    public ConfigSchema(Func<object?, ConfigResult<T>> validator, ConfigDescriptor descriptor) : this(
        validator,
        descriptor,
        [])
    {
    }

    private ConfigSchema(
        Func<object?, ConfigResult<T>> validator,
        ConfigDescriptor descriptor,
        IReadOnlyList<ConfigBinding> bindings,
        Func<T, T, bool>? ordinaryEquality = null,
        Func<T, object?>? simplify = null,
        Func<T, object?>? descriptionData = null)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(descriptor);
        Validator = validator;
        Descriptor = descriptor;
        _bindings = bindings;
        _ordinaryEquality = ordinaryEquality;
        _simplify = simplify;
        _descriptionData = descriptionData;
    }

    /// <summary>The synchronous validator; ordinary opaque CLR values are permitted.</summary>
    public Func<object?, ConfigResult<T>> Validator
    {
        get;
    }

    /// <summary>The authoring declaration, captured independently for each plugin registration.</summary>
    public ConfigDescriptor Descriptor
    {
        get;
    }

    /// <summary>Bind a stable reference to a typed validated field without reflection.</summary>
    /// <remarks>The value must detach to the declared TValue. Use object or readonly collection interfaces for plain graphs.</remarks>
    public ConfigSchema<T> WithVolatile<TValue>(string path, Func<T, TValue> project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);
        return WithVolatile<TValue>(new[] { path }, project);
    }

    /// <summary>Bind a nested fixed object-key path. Display paths use escaped JSON-pointer syntax for multiple keys.</summary>
    public ConfigSchema<T> WithVolatile<TValue>(IReadOnlyList<string> path, Func<T, TValue> project)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(project);
        foreach (var key in path)
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (path.Count == 0)
            throw new ArgumentException("Use WithVolatileValue for the whole configuration.", nameof(path));
        return AddBinding(path, value => project((T)value!));
    }

    /// <summary>Bind the whole validated value to a root volatile declaration.</summary>
    public ConfigSchema<T> WithVolatileValue() => AddBinding<T>(System.Array.Empty<string>(), value => (T)value!);

    private ConfigSchema<T> AddBinding<TValue>(IReadOnlyList<string> path, Func<object?, TValue> project)
    {
        if (_bindings.Any(binding =>
                binding.Keys.SequenceEqual(path, StringComparer.Ordinal) ||
                binding.Path == ConfigBinding.DisplayPath(path)))
            throw new ArgumentException(
                $"Duplicate or ambiguous volatile path '{ConfigBinding.DisplayPath(path)}'.",
                nameof(path));
        return new(
            Validator,
            Descriptor,
            System.Array.AsReadOnly(_bindings.Append(new ConfigBinding<TValue>(path, project)).ToArray()),
            _ordinaryEquality,
            _simplify,
            _descriptionData);
    }

    /// <summary>Compare ordinary portions of a typed configuration without reflection or reading volatile values.</summary>
    public ConfigSchema<T> WithOrdinaryEquality(Func<T, T, bool> equal)
    {
        ArgumentNullException.ThrowIfNull(equal);
        return new(Validator, Descriptor, _bindings, equal, _simplify, _descriptionData);
    }

    /// <summary>Provide complete typed simplification to raw persistence data, without reflection or descriptor branch selection.</summary>
    public ConfigSchema<T> WithSimplify(Func<T, object?> simplify)
    {
        ArgumentNullException.ThrowIfNull(simplify);
        return new(Validator, Descriptor, _bindings, _ordinaryEquality, simplify, _descriptionData);
    }

    /// <summary>Expose plain data for data-dependent lazy traversal when validator inputs are opaque CLR values.</summary>
    /// <remarks>Called once after successful validation, never by raw comparison or persistence. The author supplies the same configuration shape described by Descriptor; this callback does not validate or transform plugin values.</remarks>
    public ConfigSchema<T> WithDescriptionData(Func<T, object?> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new(Validator, Descriptor, _bindings, _ordinaryEquality, _simplify, project);
    }

    internal CapturedConfigSchema Capture()
    {
        var descriptor = Descriptor.Capture();
        if (descriptor.HasBlockedVolatilePlacement())
            throw new ConfigurationValidationException(
                ["Volatile fields require a fixed object path without an enclosing volatile field."]);
        descriptor.ValidateBindings(_bindings);
        return new(
            descriptor,
            _bindings,
            _ordinaryEquality is null ? null : (left, right) => _ordinaryEquality((T)left!, (T)right!),
            _simplify is null ? null : value => _simplify((T)value!),
            _descriptionData is null ? null : value => _descriptionData((T)value!));
    }

    internal PluginConfiguration CapturePluginConfiguration(Func<object?, ConfigResult<T>>? validator)
    {
        if (validator is not null && !validator.Equals(Validator))
            throw new ArgumentException("Configuration must describe the same validator as Config.");
        var captured = Capture();
        var validate = Validator;
        return new(captured, raw => validate(raw).GetValue());
    }

    /// <summary>Capture this exact validator/description bundle for an optional custom IPlugin adapter.</summary>
    public PluginConfiguration CaptureConfiguration() => CapturePluginConfiguration(null);
}

/// <summary>Optional configuration capture, independent of the mandatory IPlugin contract.</summary>
public interface IConfigurationPlugin
{
    /// <summary>Capture one validator/description bundle, or null when the plugin does not opt in.</summary>
    PluginConfiguration? CaptureConfiguration();
}

/// <summary>An immutable captured configuration bundle supplied by an optional plugin adapter.</summary>
public sealed class PluginConfiguration
{
    internal PluginConfiguration(CapturedConfigSchema schema, Func<object?, object?> validate) =>
        (Schema, Validate) = (schema, validate);

    internal CapturedConfigSchema Schema
    {
        get;
    }

    internal Func<object?, object?> Validate
    {
        get;
    }

    /// <summary>The captured data declaration, accessible without executing validation or activating a plugin.</summary>
    public ConfigDescriptor Descriptor => Schema.Descriptor;
}

internal static class ConfigSnapshots
{
    internal static object? Create(object? value) =>
        TryCreate(value, out var result)
            ? result
            : throw new ArgumentException("Expected an acyclic plain configuration snapshot.", nameof(value));

    internal static bool TryCreate(object? value, out object? result)
    {
        var ancestors = new HashSet<object>(ReferenceEqualityComparer.Instance);

        bool Visit(object? input, out object? output)
        {
            output = input;
            if (input is null or string or bool or char or byte or sbyte or short or ushort or int or uint or long
                    or ulong or float or double or decimal || ReferenceEquals(input, Undefined.Value))
                return true;
            if (input is Delegate || !ancestors.Add(input))
                return false;
            try
            {
                if (TryMembers(input, out var members))
                {
                    var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var pair in members)
                    {
                        if (!Visit(pair.Value, out var child))
                            return false;
                        copy.Add(pair.Key, child);
                    }

                    output = new ReadOnlyDictionary<string, object?>(copy);
                    return true;
                }

                if (input is IList list)
                {
                    var copy = new object?[list.Count];
                    for (var index = 0;index < list.Count;index++)
                        if (!Visit(list[index], out copy[index]))
                            return false;
                    output = System.Array.AsReadOnly(copy);
                    return true;
                }

                if (input is IReadOnlyList<object?> readonlyList)
                {
                    var copy = new object?[readonlyList.Count];
                    for (var index = 0;index < readonlyList.Count;index++)
                        if (!Visit(readonlyList[index], out copy[index]))
                            return false;
                    output = System.Array.AsReadOnly(copy);
                    return true;
                }

                return false;
            }
            finally
            {
                ancestors.Remove(input);
            }
        }

        return Visit(value, out result);
    }

    internal static bool TryMembers(object? value, out IReadOnlyDictionary<string, object?> members)
    {
        if (value is IReadOnlyDictionary<string, object?> typed)
        {
            members = typed;
            return true;
        }

        if (value is IDictionary dictionary)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry pair in dictionary)
            {
                if (pair.Key is not string name)
                {
                    members = copy;
                    return false;
                }

                copy.Add(name, pair.Value);
            }

            members = copy;
            return true;
        }

        members = new Dictionary<string, object?>();
        return false;
    }

    internal static bool Equal(object? left, object? right, bool strict = true)
    {
        var ancestors = new HashSet<object>(ReferenceEqualityComparer.Instance);

        static bool Number(object? value) =>
            value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

        static bool Date(object? value, out long milliseconds)
        {
            milliseconds = value switch
            {
                DateTimeOffset date => date.ToUnixTimeMilliseconds(),
                DateTime date => new DateTimeOffset(
                    date.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
                        : date.ToUniversalTime()).ToUnixTimeMilliseconds(),
                _ => 0
            };
            return value is DateTimeOffset or DateTime;
        }

        static bool Binary(object? value, out ReadOnlyMemory<byte> bytes)
        {
            bytes = value switch
            {
                byte[] array => array,
                Memory<byte> memory => memory,
                ReadOnlyMemory<byte> memory => memory,
                _ => default
            };
            return value is byte[] or Memory<byte> or ReadOnlyMemory<byte>;
        }

        static bool List(object? value, out IReadOnlyList<object?> items)
        {
            if (value is IReadOnlyList<object?> readonlyList)
            {
                items = readonlyList;
                return true;
            }

            if (value is IList list)
            {
                items = list.Cast<object?>().ToArray();
                return true;
            }

            items = System.Array.Empty<object?>();
            return false;
        }

        bool Compare(object? p, object? q)
        {
            if (!strict && (p is null || ReferenceEquals(p, Undefined.Value)) &&
                (q is null || ReferenceEquals(q, Undefined.Value)))
                return true;
            if (p is double nan && double.IsNaN(nan) || p is float nan1 && float.IsNaN(nan1) ||
                q is double nan2 && double.IsNaN(nan2) || q is float nan3 && float.IsNaN(nan3))
                return false;
            if (ReferenceEquals(p, q))
                return true;
            if (p is IConfigReference || q is IConfigReference)
                return p is IConfigReference && q is IConfigReference;
            if (Number(p) || Number(q))
                return Number(p) && Number(q) &&
                    Convert.ToDouble(p, System.Globalization.CultureInfo.InvariantCulture) == Convert.ToDouble(
                        q,
                        System.Globalization.CultureInfo.InvariantCulture);
            if (p is string or bool or char || q is string or bool or char)
                return p?.Equals(q) ?? false;
            if (p is null || q is null || ReferenceEquals(p, Undefined.Value) || ReferenceEquals(q, Undefined.Value))
                return false;
            if (!ancestors.Add(p))
                return false;
            try
            {
                if (Date(p, out var x) || Date(q, out _))
                    return Date(p, out x) && Date(q, out var y) && x == y;
                if (p is Uri || q is Uri)
                    return p is Uri u && q is Uri v && (u.IsAbsoluteUri ? u.AbsoluteUri : u.OriginalString) ==
                        (v.IsAbsoluteUri ? v.AbsoluteUri : v.OriginalString);
                if (p is System.Text.RegularExpressions.Regex || q is System.Text.RegularExpressions.Regex)
                    return p is System.Text.RegularExpressions.Regex r1 &&
                        q is System.Text.RegularExpressions.Regex r2 && r1.ToString() == r2.ToString() &&
                        r1.Options == r2.Options;
                if (Binary(p, out var bytes) || Binary(q, out _))
                    return Binary(p, out bytes) && Binary(q, out var other) && bytes.Span.SequenceEqual(other.Span);
                if (List(p, out var a) || List(q, out _))
                    return List(p, out a) && List(q, out var b) && a.Count == b.Count &&
                        !a.Where((value, index) => !Compare(value, b[index])).Any();
                if (TryMembers(p, out var d) && TryMembers(q, out var e))
                    return d
                        .Keys.Concat(e.Keys)
                        .Distinct(StringComparer.Ordinal)
                        .All(key => Compare(
                            d.GetValueOrDefault(key, Undefined.Value),
                            e.GetValueOrDefault(key, Undefined.Value)));
                return false;
            }
            finally
            {
                ancestors.Remove(p);
            }
        }

        return Compare(left, right);
    }
}
