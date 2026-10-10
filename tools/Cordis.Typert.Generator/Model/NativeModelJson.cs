using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Typert.Generator.Model;

internal static class NativeModelJson
{
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static string Serialize(NativeModelDocument model) => JsonSerializer.Serialize(model, Options) + "\n";

    internal static NativeModelDocument Deserialize(string json)
    {
        var model = JsonSerializer.Deserialize<NativeModelDocument>(json, Options) ??
            throw new InvalidDataException("Native contract model is null.");
        if (model.FormatVersion != CurrentVersion)
            throw new InvalidDataException($"Unsupported native model format {model.FormatVersion}.");
        if (model.Services is null || model.Types is null || model.Diagnostics is null || model.Compilation is null)
            throw new InvalidDataException("Native model collections and compilation settings are required.");
        if (model.Services.Select(service => service.Key).Distinct(StringComparer.Ordinal).Count() !=
            model.Services.Length ||
            model.Types.Select(type => type.Id).Distinct(StringComparer.Ordinal).Count() != model.Types.Length)
            throw new InvalidDataException("Native model contains duplicate service or type identities.");
        if (model.ContractIdentity != Identity(model))
            throw new InvalidDataException("Native model contract identity does not match its authored facts.");
        return model;
    }

    internal static NativeModelDocument WithIdentity(NativeModelDocument model) =>
        model with
        {
            ContractIdentity = Identity(model)
        };

    internal static JsonNode ContractFacts(NativeModelDocument model)
    {
        var node = JsonSerializer.SerializeToNode(model, Options)!.AsObject();
        node.Remove("contractIdentity");
        RemoveLocations(node);
        return node;
    }

    private static string Identity(NativeModelDocument model) =>
        "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(ContractFacts(model).ToJsonString(Options))));

    private static void RemoveLocations(JsonNode? node)
    {
        if (node is JsonObject map)
        {
            map.Remove("location");
            foreach (var child in map.ToArray())
                RemoveLocations(child.Value);
        }
        else if (node is JsonArray list)
            foreach (var child in list)
                RemoveLocations(child);
    }
}

internal static class NativeModelConformance
{
    internal static string[] Compare(NativeModelDocument artifact, NativeModelDocument authored, string service)
    {
        var expected = Select(artifact, service);
        var actual = Select(authored, service);
        var differences = new List<string>();
        CompareNode(NativeModelJson.ContractFacts(expected), NativeModelJson.ContractFacts(actual), "$", differences);
        return differences.ToArray();
    }

    private static NativeModelDocument Select(NativeModelDocument model, string service)
    {
        var selected = model.Services.Where(value => value.Key == service || value.SourceType == service).ToArray();
        if (selected.Length != 1)
            throw new InvalidDataException($"Native model must contain exactly one selected service '{service}'.");
        // Each extraction is service-scoped. This compares the complete reachable authored graph too.
        return model with
        {
            Services = selected
        };
    }

    private static void CompareNode(JsonNode? expected, JsonNode? actual, string path, List<string> output)
    {
        if (output.Count >= 32 || JsonNode.DeepEquals(expected, actual))
            return;
        if (expected is JsonObject left && actual is JsonObject right)
        {
            foreach (var key in left
                         .Select(pair => pair.Key)
                         .Union(right.Select(pair => pair.Key), StringComparer.Ordinal))
                CompareNode(left[key], right[key], path + "." + key, output);
        }
        else if (expected is JsonArray leftList && actual is JsonArray rightList && leftList.Count == rightList.Count)
        {
            for (var index = 0;index < leftList.Count;index++)
                CompareNode(leftList[index], rightList[index], path + "[" + index + "]", output);
        }
        else
            output.Add(
                path + ": artifact " + (expected?.ToJsonString() ?? "null") +
                ", authored " + (actual?.ToJsonString() ?? "null"));
    }
}
