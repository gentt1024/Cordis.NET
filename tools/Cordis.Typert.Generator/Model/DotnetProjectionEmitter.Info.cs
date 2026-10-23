namespace Cordis.Typert.Generator.Model;

internal sealed partial class DotnetProjectionEmitter
{
    private void EmitInfo(NativeService service)
    {
        output
            .AppendLine("    /// <summary>Reusable metadata for this declaration's assembly package.</summary>")
            .AppendLine(
                "    public static global::Cordis.Composition.TypertRemoteInfo Info { get; } = CreateInfo(" +
                Literal(model!.Compilation.AssemblyName) + ");")
            .AppendLine("    /// <summary>Creates metadata for an explicitly selected package identity.</summary>")
            .AppendLine(
                "    public static global::Cordis.Composition.TypertRemoteInfo CreateInfo(string package) => new(package, [");
        foreach (var method in service.Methods)
        {
            var parameters = method
                .Parameters.Where(parameter => method.Context is null || parameter.Wire != method.ContextWire)
                .ToArray();
            output
                .Append("        new global::Cordis.Composition.TypertInvocationDescriptor(package + ")
                .Append(Literal("#" + service.Namespace + "/" + method.Name))
                .Append(", ")
                .Append(Literal(service.Key))
                .Append(", ")
                .Append(Literal(service.Namespace))
                .Append(", ")
                .Append(Literal(method.Name))
                .Append(", [");
            foreach (var parameter in parameters)
            {
                var disallowNull = parameter.Attributes.Any(attribute =>
                    attribute.Name == "System.Diagnostics.CodeAnalysis.DisallowNullAttribute");
                output
                    .Append("new global::Cordis.Composition.TypertInvocationParameter(")
                    .Append(Literal(parameter.Name))
                    .Append(", ")
                    .Append(Literal(parameter.Wire))
                    .Append(", ")
                    .Append(Codec(parameter.Type, disallowNull))
                    .Append(") { ");
                if (parameter.Lookup is { } lookup)
                    output.Append("Lookup = ").Append(Literal(lookup)).Append(", ");
                else if (parameter.Optional)
                    output.Append("AcceptsUndefined = true, ");
                if (RemoteDocumentation.Parameter(method.Documentation, parameter.Name) is { } description)
                    output.Append("Description = ").Append(Literal(description)).Append(", ");
                output.Append("},");
            }

            output
                .Append("], ")
                .Append(Codec(method.Result))
                .Append(") { Cancellation = ")
                .Append(method.Cancellation ? "true" : "false")
                .Append(", IsStream = ")
                .Append(method.Stream ? "true" : "false");
            if (method.Implementation != method.Name)
                output.Append(", Implementation = ").Append(Literal(method.Implementation));
            if (RemoteDocumentation.Summary(method.Documentation) is { } summary)
                output.Append(", Description = ").Append(Literal(summary));
            if (RemoteDocumentation.Returns(method.Documentation) is { } resultDescription)
                output.Append(", ResultDescription = ").Append(Literal(resultDescription));
            if (method.Context is { } context)
                output
                    .Append(", Invocation = new global::Cordis.Composition.TypertContextInvocation(")
                    .Append(Literal(context))
                    .Append(", ")
                    .Append(Literal(method.ContextWire))
                    .Append(", ")
                    .Append(Codec(method.ContextWireType!))
                    .Append(')');
            if (method.Scope is { } scope)
                output
                    .Append(", Scope = new global::Cordis.Composition.TypertScopeProjection(")
                    .Append(Literal(scope.Context))
                    .Append(", ")
                    .Append(Literal(scope.Wire))
                    .Append(')');
            if (method.Uplink is { } uplink)
            {
                var codec = Codec(uplink.Type);
                if (uplink.Optional)
                    codec = "global::Cordis.Composition.TypertCodec.CreateOptional<" + TypeName(uplink.Type) + ">(" +
                        codec + ")";
                output.Append(", Uplink = ").Append(codec);
            }

            output.AppendLine(" },");
        }

        output
            .AppendLine("    ]);")
            .AppendLine(
                "    object global::Cordis.Composition.ITypertNamedRemote.Invoke(string endpoint, global::System.Collections.Generic.IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(arguments);")
            .AppendLine("        return endpoint switch")
            .AppendLine("        {");
        foreach (var method in service.Methods.Where(method => method.Context is null))
        {
            var values = method.Parameters.Select((parameter, index) => parameter.Optional
                ? "(arguments.TryGetValue(" + Literal(parameter.Wire) + ", out var value" + index +
                ") && !global::System.Object.ReferenceEquals(value" + index + ", global::Cordis.Undefined.Value) ? (" +
                TypeName(parameter.Type) + ")value" + index + "! : " + Constant(parameter.Default!) +
                (OmitNullDefault(parameter) ? "!" : "") + ")"
                : "(" + TypeName(parameter.Type) + ")arguments[" + Literal(parameter.Wire) + "]!");
            output
                .Append("            ")
                .Append(Literal(service.Namespace + "/" + method.Name))
                .Append(" => ")
                .Append(Identifier(char.ToUpperInvariant(method.Name[0]) + method.Name[1..] + "Async"))
                .Append('(')
                .Append(string.Join(", ", values.Append("cancellationToken")))
                .AppendLine("),");
        }

        output
            .AppendLine(
                "            _ => throw new global::System.ArgumentException(\"Unknown direct Remote endpoint.\", nameof(endpoint))")
            .AppendLine("        };")
            .AppendLine("    }");
    }
}
