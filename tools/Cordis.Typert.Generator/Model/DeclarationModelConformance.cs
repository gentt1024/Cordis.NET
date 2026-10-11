using System.Text.Json.Nodes;
using static Cordis.Typert.Generator.Model.DeclarationAttributeProjection;

namespace Cordis.Typert.Generator.Model;

internal static class DeclarationModelConformance
{
    internal static string[] Compare(DeclarationModelDocument artifact, DeclarationModelDocument authored, string service)
    {
        var differences = new List<string>();
        CompareNode(DeclarationModelJson.ContractFacts(Select(artifact, service)),
            DeclarationModelJson.ContractFacts(Select(authored, service)), "$", differences);
        return differences.ToArray();
    }

    internal static DeclarationModelDocument Select(DeclarationModelDocument model, string service)
    {
        var declarations = model.Declarations.ToDictionary(type => type.Id, StringComparer.Ordinal);
        var selected = model.Roots.Select(id => declarations[id]).Where(type =>
            Attribute(type.Attributes, "Cordis.Composition.RemoteServiceAttribute") is { } marker &&
            (type.MetadataName.Replace('+', '.') == service || String(marker.Arguments.FirstOrDefault()) == service)).ToArray();
        if (selected.Length != 1)
            throw new InvalidDataException($"Declaration model must contain exactly one selected Remote service '{service}'.");
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        Visit(selected[0].Id);
        return model with
        {
            Roots = [selected[0].Id],
            Declarations = model.Declarations.Where(type => reachable.Contains(type.Id)).OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(),
            Diagnostics = model.Diagnostics.Where(value => value.Symbol is null || symbols.Contains(value.Symbol) ||
                reachable.Any(id => value.Symbol.StartsWith(id + ".", StringComparison.Ordinal))).ToArray()
        };

        void Visit(string id)
        {
            if (!reachable.Add(id))
                return;
            if (!declarations.TryGetValue(id, out var type))
                throw new InvalidDataException("Missing declaration in selected service closure: " + id);
            symbols.Add(id);
            if (type.ContainingType is not null)
                Visit(type.ContainingType);
            Reference(type.BaseType);
            Reference(type.EnumUnderlyingType);
            foreach (var value in type.Interfaces)
                Reference(value);
            Parameters(type.TypeParameters);
            Attributes(type.Attributes);
            foreach (var member in type.Members)
            {
                symbols.Add(member.Id);
                Reference(member.Type);
                Parameters(member.TypeParameters);
                Attributes(member.Attributes);
                Attributes(member.ReturnAttributes);
                Attributes(member.GetterAttributes);
                Attributes(member.SetterAttributes);
                Attributes(member.SetterValueAttributes);
                foreach (var parameter in member.Parameters)
                {
                    Reference(parameter.Type);
                    Attributes(parameter.Attributes);
                }
            }
        }

        void Reference(DeclarationReference? reference)
        {
            if (reference is null)
                return;
            if (reference.Kind is "named" or "tuple")
                Visit(reference.Name);
            foreach (var argument in reference.Arguments)
                Reference(argument);
            Reference(reference.ContainingType);
        }

        void Parameters(DeclarationTypeParameter[] parameters)
        {
            foreach (var parameter in parameters)
                foreach (var constraint in parameter.Constraints)
                    Reference(constraint);
        }

        void Attributes(DeclarationAttribute[] attributes)
        {
            foreach (var attribute in attributes)
            {
                Reference(attribute.Type);
                foreach (var value in attribute.Arguments.Concat(attribute.NamedArguments.Select(value => value.Value)))
                    Constant(value);
            }
        }

        void Constant(DeclarationConstant value)
        {
            Reference(value.Reference);
            foreach (var element in value.Elements)
                Constant(element);
        }
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
