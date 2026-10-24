using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Typert.Generator.Model;

internal static class DeclarationDefaultProjection
{
    internal static string? Json(NativeConstant? value, Func<DeclarationReference, string, string?> memberName)
    {
        if (value is null)
            return null;
        if (value.Kind == "construction" && value.Construction is { } construction)
        {
            var result = new JsonObject();
            foreach (var argument in construction.Arguments)
            {
                var name = memberName(construction.Type, argument.Name);
                var json = Json(argument.Value, memberName);
                if (name is null || json is null)
                    return null;
                result.Add(name, JsonNode.Parse(json));
            }

            return result.ToJsonString();
        }

        return value.Kind switch
        {
            "emptyCollection" when value.Type.TrimEnd('?') is "byte[]" or "System.Byte[]" => "\"\"",
            "emptyCollection" when value.Type.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal) ||
                value.Type.StartsWith("System.Collections.Generic.IDictionary<", StringComparison.Ordinal) ||
                value.Type.StartsWith("System.Collections.Generic.IReadOnlyDictionary<", StringComparison.Ordinal) => "{}",
            "emptyCollection" => "[]",
            "value" when value.Type == "System.Guid" && value.Json == "null" => "\"00000000-0000-0000-0000-000000000000\"",
            "value" or "enum" => value.Json,
            _ => null
        };
    }
}
