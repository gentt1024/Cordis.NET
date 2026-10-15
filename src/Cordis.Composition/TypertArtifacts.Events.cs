using Cordis.Typert;

namespace Cordis.Composition;

public static partial class TypertArtifacts
{
    /// <summary>Emit typed listeners from the Host-selected common event declarations.</summary>
    public static TypertClientArtifact GenerateEvents(IReadOnlyList<TypertRemoteEventDefinition> events)
    {
        static ClientCodec Codec(TypertCodec value, bool output) =>
            new(output ? value.ResultSchema : value.Schema, AcceptsUndefined: value.AcceptsUndefined);

        var artifact = EventClientEmitter.Generate(
            events
                .Select(item => new ClientEvent(
                    item.Event,
                    item.Args.Select(value => Codec(value, true)).ToArray(),
                    item.Request is { } request
                        ? new(Codec(request, true), Codec(item.Result!, false), Codec(item.Result!, true))
                        : null))
                .ToArray());
        return new(artifact.Module, artifact.Declaration);
    }
}
