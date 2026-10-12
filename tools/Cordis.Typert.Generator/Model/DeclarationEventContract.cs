using static Cordis.Typert.Generator.Model.DeclarationAttributeProjection;

namespace Cordis.Typert.Generator.Model;

internal sealed record RemoteEventContract(
    DeclarationType Facts,
    string Name,
    DeclarationReference JsonContext,
    DeclarationParameter[] Parameters,
    DeclarationReference? Subject,
    DeclarationReference? Request,
    DeclarationReference? Result);

/// <summary>Interpret selected event semantics once from common declaration facts.</summary>
internal static class DeclarationEventContract
{
    internal static RemoteEventContract Read(DeclarationModelDocument model, string id)
    {
        var facts = model.Declarations.Single(type => type.Id == id);
        var marker = Attribute(facts.Attributes, "Cordis.Composition.RemoteEventAttribute")
            ?? throw new InvalidDataException("The selected declaration is not a Remote event.");
        var name = String(marker.Arguments.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(name) || facts.Kind != "delegate" || facts.TypeParameters.Length != 0 ||
            facts.ContainingType is not null || facts.Accessibility != "Public")
            throw new InvalidDataException(
                "Remote events require a public top-level non-generic delegate and an event name.");
        var json = marker.Arguments.ElementAtOrDefault(1)?.Reference
            ?? throw new InvalidDataException("Remote events require explicit source JSON metadata.");
        var invoke = facts.Members.Single(member => member.Name == "Invoke");
        if (invoke.Parameters.Any(p => p.RefKind != "None" || p.Optional || p.Params))
            throw new InvalidDataException("Remote event arguments cannot be ref, optional or params.");
        if (invoke.Type is { Kind: "primitive", Name: "System_Void" })
            return new(facts, name, json, invoke.Parameters, null, null, null);

        bool Is(DeclarationReference reference, string metadata) => reference.Kind == "named" &&
            model.Declarations.Any(type => type.Id == reference.Name && type.MetadataName == metadata);

        var args = invoke.Parameters;
        if (invoke.Type is not { Arguments.Length: 1 } result || !Is(result, "System.Threading.Tasks.Task`1") ||
            args.Length != 4 || !Is(args[2].Type, "System.Func`1") || args[2].Type.Arguments.Length != 1 ||
            System.Text.Json.JsonSerializer.Serialize(args[2].Type.Arguments[0]) !=
            System.Text.Json.JsonSerializer.Serialize(result) ||
            !Is(args[3].Type, "System.Threading.CancellationToken"))
            throw new InvalidDataException(
                "A scoped Remote event requires Task<T> (agent, request, Func<Task<T>> next, CancellationToken signal).");
        var subject = Attribute(args[0].Attributes, "Cordis.Composition.RemoteLookupAttribute");
        if (String(subject?.Arguments.FirstOrDefault()) != "agent" ||
            String(subject?.Arguments.ElementAtOrDefault(1)) != "agentId" ||
            subject?.Arguments.ElementAtOrDefault(2)?.Reference is not { Kind: "primitive", Name: "System_String" } ||
            !model.Contexts.Any(context =>
                context.Key == "agent" && context.WireType is { Kind: "primitive", Name: "System_String" }))
            throw new InvalidDataException(
                "The event subject must use the declared agent Context and its string lookup identity.");
        return new(facts, name, json, args, args[0].Type, args[1].Type, result.Arguments[0]);
    }
}
