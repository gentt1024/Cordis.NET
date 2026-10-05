using System.Collections.ObjectModel;

namespace Cordis;

public sealed partial class ConfigDescriptor
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyAnnotations =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>Immutable plain-data annotations interpreted by application adapters.</summary>
    /// <remarks>Core does not enforce annotation constraints. Authors must keep them consistent with their validator.
    /// Annotation values cannot retain plugin objects, delegates or mutable caller collections.</remarks>
    public IReadOnlyDictionary<string, object?> Annotations { get; }

    /// <summary>Replace application annotations with a detached acyclic plain-data snapshot.</summary>
    /// <remarks>Preserves shape, defaults and stable-reference declarations. This method adds no validation behavior.</remarks>
    public ConfigDescriptor WithAnnotations(IReadOnlyDictionary<string, object?> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        var snapshot = (IReadOnlyDictionary<string, object?>)ConfigSnapshots.Create(annotations)!;
        return new(Kind, Properties, Inner, IsOptional, IsVolatile, HasDefault, DefaultValue,
            _lazy, Children, Key, _lazyIdentity, _selectBranch, snapshot);
    }
}
