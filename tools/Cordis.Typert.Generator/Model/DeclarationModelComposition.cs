using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Typert.Generator.Model;

/// <summary>Resolves explicit declaration artifacts without loading their implementation assemblies.</summary>
internal sealed class DeclarationModelComposition
{
    private readonly Dictionary<string, DeclarationType[]> declarations;
    private readonly Dictionary<string, DeclarationType> resolved = new(StringComparer.Ordinal);

    internal DeclarationModelComposition(DeclarationModelDocument primary, DeclarationModelDocument[] references)
    {
        var models = references.Prepend(primary).ToArray();
        declarations = models
            .SelectMany(model => model.Declarations)
            .GroupBy(type => type.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Diagnostics = models.SelectMany(model => model.Diagnostics).ToArray();
        ReferenceIdentities = references
            .Select(model => model.ContractIdentity)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal NativeDiagnostic[] Diagnostics
    {
        get;
    }

    internal string[] ReferenceIdentities
    {
        get;
    }

    internal DeclarationType Resolve(string id)
    {
        if (resolved.TryGetValue(id, out var previous))
            return previous;
        if (!declarations.TryGetValue(id, out var candidates))
            throw Refuse(id, "No declaration has this exact assembly-qualified identity.");
        foreach (var candidate in candidates)
            if (candidate.Id != candidate.Assembly + ":" + candidate.MetadataName)
                throw Refuse(id, "Declaration identity does not match its assembly and metadata name.");
        var sources = candidates.Where(type => type.Provenance == "source").ToArray();
        var selected = sources.FirstOrDefault() ?? candidates[0];
        if (sources.Select(SourceFacts).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw Refuse(id, "Conflicting source definitions were supplied for the same declaration identity.");
        if (candidates.Any(type => type.Name != selected.Name ||
                type.Kind != selected.Kind && (type.Provenance == "source" ||
                    MetadataKind(type.Kind) != MetadataKind(selected.Kind)) ||
                type.TypeParameters.Length != selected.TypeParameters.Length ||
                type.Accessibility != selected.Accessibility))
            throw Refuse(id, "Referenced source definition contradicts the recorded declaration shape.");
        resolved.Add(id, selected);
        return selected;
    }

    private static string MetadataKind(string kind) => kind switch
    {
        "record" => "class",
        "recordStruct" => "struct",
        _ => kind
    };

    private static string SourceFacts(DeclarationType type)
    {
        var node = JsonSerializer.SerializeToNode(type)!;
        RemoveLocations(node);
        return node.ToJsonString();
    }

    private static void RemoveLocations(JsonNode node)
    {
        if (node is JsonObject map)
        {
            map.Remove(nameof(DeclarationType.Location));
            foreach (var child in map)
                if (child.Value is not null)
                    RemoveLocations(child.Value);
        }
        else if (node is JsonArray list)
            foreach (var child in list)
                if (child is not null)
                    RemoveLocations(child);
    }

    private static InvalidDataException Refuse(string id, string message) =>
        new("DataProjectionUnsupported " + id + ": " + message);
}
