namespace Cordis.Composition;

/// <summary>One renderer badge authored alongside the configuration validator.</summary>
/// <param name="Text">Display text.</param>
/// <param name="Type">Renderer badge style.</param>
public sealed record ConfigurationBadge(string Text, string Type);

/// <summary>An ECMAScript pattern declaration; no CLR regular expression or plugin callback is retained.</summary>
/// <param name="Source">Pattern source.</param>
/// <param name="Flags">ECMAScript flags.</param>
public sealed record ConfigurationPattern(string Source, string Flags = "");

/// <summary>Explicit application metadata for a configuration declaration.</summary>
/// <remarks>Constraints describe the author's validator; they do not install a Core validator.
/// Authors must keep acceptance and normalization consistent. Extra must be acyclic plain data.</remarks>
public sealed record ConfigurationMetadata
{
    /// <summary>Renderer role; secret produces write-only settings fields.</summary>
    public string? Role
    {
        get;
        init;
    }

    /// <summary>Renderer-specific plain data.</summary>
    public object? Extra
    {
        get;
        init;
    }

    /// <summary>A plain description, used when localized descriptions are absent.</summary>
    public string? Description
    {
        get;
        init;
    }

    /// <summary>Descriptions keyed by locale.</summary>
    public IReadOnlyDictionary<string, string>? Descriptions
    {
        get;
        init;
    }

    /// <summary>Hide this field in application forms and settings.</summary>
    public bool? Hidden
    {
        get;
        init;
    }

    /// <summary>Disable input in a renderer.</summary>
    public bool? Disabled
    {
        get;
        init;
    }

    /// <summary>Initially collapse the renderer section.</summary>
    public bool? Collapse
    {
        get;
        init;
    }

    /// <summary>Renderer badges.</summary>
    public IReadOnlyList<ConfigurationBadge>? Badges
    {
        get;
        init;
    }

    /// <summary>Documentation link.</summary>
    public string? Link
    {
        get;
        init;
    }

    /// <summary>Author comment.</summary>
    public string? Comment
    {
        get;
        init;
    }

    /// <summary>Declared numeric lower bound or string/array length bound.</summary>
    public double? Min
    {
        get;
        init;
    }

    /// <summary>Declared numeric upper bound or string/array length bound.</summary>
    public double? Max
    {
        get;
        init;
    }

    /// <summary>Declared numeric step relative to Min, or zero when inactive.</summary>
    public double? Step
    {
        get;
        init;
    }

    /// <summary>Declared ECMAScript string pattern.</summary>
    public ConfigurationPattern? Pattern
    {
        get;
        init;
    }

    /// <summary>Declared tolerant native acceptance; exact recovery behavior remains runtime-owned.</summary>
    public bool? Loose
    {
        get;
        init;
    }
}

/// <summary>Application authoring helpers over Core's immutable plain-data annotation carrier.</summary>
public static class ConfigurationMetadataExtensions
{
    /// <summary>Copy declared metadata onto a descriptor without retaining caller collections or adding validators.</summary>
    public static ConfigDescriptor WithMetadata(this ConfigDescriptor descriptor, ConfigurationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(metadata);
        var annotations = new Dictionary<string, object?>(descriptor.Annotations, StringComparer.Ordinal);

        void Add(string key, object? value)
        {
            if (value is not null)
                annotations[key] = value;
        }

        Add("role", metadata.Role);
        Add("extra", metadata.Extra);
        Add(
            "description",
            metadata.Descriptions is null
                ? metadata.Description
                : metadata.Descriptions.ToDictionary(pair => pair.Key, pair => (object?)pair.Value));
        Add("hidden", metadata.Hidden);
        Add("disabled", metadata.Disabled);
        Add("collapse", metadata.Collapse);
        Add(
            "badges",
            metadata
                .Badges?.Select(badge => (object?)new Dictionary<string, object?>
                {
                    ["text"] = badge.Text,
                    ["type"] = badge.Type
                })
                .ToArray());
        Add("link", metadata.Link);
        Add("comment", metadata.Comment);
        foreach (var (name, value) in new[] { ("min", metadata.Min), ("max", metadata.Max), ("step", metadata.Step) })
        {
            if (value is { } number && !double.IsFinite(number))
                throw new ArgumentException("Metadata bounds must be finite.", nameof(metadata));
            Add(name, value);
        }

        if (metadata.Pattern is { } pattern)
            Add(
                "pattern",
                new Dictionary<string, object?>
                {
                    ["source"] = pattern.Source,
                    ["flags"] = pattern.Flags
                });
        Add("loose", metadata.Loose);
        return descriptor.WithAnnotations(annotations);
    }
}
