namespace Cordis.Clr;

internal static class ClrBundleMetadata
{
    internal static List<object?> DefaultPatches(string packageId)
    {
        var id = packageId.ToLowerInvariant();
        var entry = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = "nuget:" + id,
            ["config"] = new Dictionary<string, object?>()
        };
        return
        [
            new Dictionary<string, object?>
            {
                ["insert"] = new List<object?>
                {
                    entry
                }
            }
        ];
    }

    internal static Dictionary<string, object?> Manifest(
        string packageId,
        string version,
        string? description,
        IDictionary<string, object?> declaration)
    {
        var result = new Dictionary<string, object?>
        {
            ["name"] = packageId,
            ["version"] = version,
            ["description"] = description,
            ["dsh"] = new Dictionary<string, object?>
            {
                ["bundle"] = new Dictionary<string, object?>
                {
                    ["patch"] = "cordis.patch.yml"
                }
            }
        };
        foreach (var field in new[] { "peerDependencies", "engines" })
            if (declaration.TryGetValue(field, out var value))
                result.Add(field, value);
        return result;
    }
}
