using System.Text;
using System.Text.Json;

namespace Cordis.Composition;

/// <summary>Generated TypeScript module and declaration, produced from the same Host descriptors.</summary>
public sealed record TypertClientArtifact(string Module, string Declaration);

/// <summary>Native emitters consuming compiler-independent Typert metadata.</summary>
public static class TypertArtifacts
{
    /// <summary>Build package schemas and reflected method signatures from compiler-generated descriptors.</summary>
    public static TypertContribution Contribution(
        string package,
        string exportName,
        IReadOnlyList<TypertInvocationDescriptor> descriptors)
    {
        var schemas = descriptors
            .SelectMany(descriptor => descriptor
                .Parameters.Select(parameter => parameter.Codec)
                .Append(descriptor.Result)
                .Concat(descriptor.Invocation is { } receiver ? [receiver.Codec] : []))
            .DistinctBy(codec => codec.TypeSymbol)
            .Select(codec => new TypertSchemaFactory(codec.TypeSymbol, () => codec.Schema))
            .ToArray();
        var services = descriptors
            .GroupBy(descriptor => descriptor.Service)
            .Select(group => new TypertServiceModel(
                group.Key,
                exportName,
                group
                    .Select(descriptor => new TypertMemberModel(
                        "method",
                        descriptor.Method,
                        "(" + string.Join(
                            ", ",
                            descriptor.Parameters.Select(parameter =>
                                parameter.Name + ": " + parameter.Codec.TypeSymbol)) +
                        "): " + (descriptor.IsStream ? "RemoteStream<" : "Promise<") + descriptor.Result.TypeSymbol +
                        ">"))
                    .ToArray(),
                []))
            .ToArray();
        return new(package, "host", schemas, new(services, [], []), descriptors);
    }

    /// <summary>Emit typed Client methods and an HTTP carrier factory. Unsupported schema constructs fail generation.</summary>
    public static TypertClientArtifact GenerateClient(TypertContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (contribution
            .Invocations.GroupBy(descriptor => descriptor.Endpoint, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
            throw new ArgumentException(
                "A Client contribution cannot contain duplicate Remote endpoints.",
                nameof(contribution));
        var definitions = new StringBuilder(
            "export declare class RemoteError extends Error { readonly isDSHRemoteError: true; readonly code: string; readonly details: unknown; constructor(code: string, message: string, details?: unknown); }\n" +
            "export type RemoteResult<T> = { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: RemoteError };\n");
        var methods = new StringBuilder("export interface RemoteClient {\n  dispose(): void;\n");
        var runtime = new StringBuilder(
            "export function createRemote(base, fetcher = fetch) { const state = {active:true, controllers:new Set()}; return {\n  dispose() { state.active = false; for (const controller of state.controllers) controller.abort(); },\n");
        var index = 0;
        foreach (var descriptor in contribution.Invocations)
        {
            var fields = new List<string>();
            foreach (var parameter in descriptor.Parameters)
            {
                var type = "Boundary" + index++;
                AppendType(definitions, type, parameter.Codec.Schema);
                fields.Add(Quote(parameter.Wire) + (parameter.AcceptsUndefined ? "?" : "") + ": " + type);
            }

            if (descriptor.Invocation is { } receiver)
            {
                var type = "Boundary" + index++;
                AppendType(definitions, type, receiver.Codec.Schema);
                fields.Add(Quote(receiver.Wire) + ": " + type);
            }

            var result = "Boundary" + index++;
            AppendType(definitions, result, descriptor.Result.Schema);
            methods
                .Append("  ")
                .Append(Quote(descriptor.Endpoint))
                .Append("(args: { ")
                .Append(string.Join("; ", fields))
                .Append(" }, signal?: AbortSignal): ")
                .Append(descriptor.IsStream ? "AsyncIterable<RemoteResult<" : "Promise<RemoteResult<")
                .Append(result)
                .Append(">>;\n");
            runtime
                .Append("  [")
                .Append(Quote(descriptor.Endpoint))
                .Append("]: (args, signal) => ")
                .Append(descriptor.IsStream ? "stream" : "unary")
                .Append("(fetcher, base, ")
                .Append(Quote(descriptor.Endpoint))
                .Append(", args, signal, state),\n");
        }

        methods.Append(
            "}\nexport declare function createRemote(base: string, fetcher?: typeof fetch): RemoteClient;\n");
        runtime.Append("}; }\n");
        runtime.Append("const namespaceSpecs = [\n");
        foreach (var group in contribution.Invocations.GroupBy(descriptor => descriptor.Namespace))
            runtime
                .Append("  [")
                .Append(Quote("remote." + group.Key))
                .Append(", [")
                .Append(
                    string.Join(
                        ", ",
                        group.Select(descriptor =>
                            "[" + Quote(descriptor.Method) + ", " + Quote(descriptor.Endpoint) + "]")))
                .Append("]],\n");
        runtime.Append("];\n").Append(Carrier);
        methods.Append(
            "export interface RemoteOwner { readonly root: {get(name: string): unknown; provide(name: string, value: object): () => void | Promise<void>}; effect(setup: () => Promise<() => Promise<void>>): PromiseLike<unknown> & (() => Promise<void>); }\n" +
            "export declare function mountRemote(ctx: RemoteOwner, base: string, fetcher?: typeof fetch): Promise<{remote: RemoteClient; dispose: () => Promise<void>}>;\n");
        return new(runtime.ToString(), definitions.Append(methods).ToString());
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, TypertJson.Default.String);

    private static void AppendType(StringBuilder output, string name, JsonElement schema)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["#"] = name
        };
        var pending = new Queue<(string Name, JsonElement Schema)>();
        pending.Enqueue((name, schema));
        while (pending.TryDequeue(out var type))
            output.Append("export type ").Append(type.Name).Append(" = ").Append(Render(type.Schema)).Append(";\n");

        string Render(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.True)
                return "unknown";
            if (node.ValueKind == JsonValueKind.False)
                return "never";
            if (node.ValueKind != JsonValueKind.Object)
                throw new NotSupportedException("Invalid JSON Schema type model.");
            if (node.TryGetProperty("$ref", out var reference))
            {
                var path = reference.GetString()!;
                if (refs.TryGetValue(path, out var known))
                    return known;
                if (!path.StartsWith("#/", StringComparison.Ordinal))
                    throw new NotSupportedException("Only local schema references are supported.");
                var target = schema;
                foreach (var segment in path[2..].Split('/'))
                {
                    var key = segment
                        .Replace("~1", "/", StringComparison.Ordinal)
                        .Replace("~0", "~", StringComparison.Ordinal);
                    target = target.ValueKind == JsonValueKind.Array
                        ? target[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)]
                        : target.GetProperty(key);
                }

                var alias = name + "Ref" + refs.Count;
                refs.Add(path, alias);
                pending.Enqueue((alias, target));
                return alias;
            }

            if (node.TryGetProperty("enum", out var values))
                return string.Join(" | ", values.EnumerateArray().Select(value => value.GetRawText()));
            if (node.TryGetProperty("const", out var constant))
                return constant.GetRawText();
            foreach (var union in new[] { "anyOf", "oneOf" })
                if (node.TryGetProperty(union, out var choices))
                    return string.Join(" | ", choices.EnumerateArray().Select(Render));
            if (node.TryGetProperty("allOf", out var requirements))
                return string.Join(" & ", requirements.EnumerateArray().Select(Render));
            if (!node.TryGetProperty("type", out var kind))
            {
                if (node.TryGetProperty("properties", out _))
                    return ObjectType(node);
                if (node
                    .EnumerateObject()
                    .All(property => property.Name is "description" or "title" or "$schema" or "$id"))
                    return "unknown";
                throw new NotSupportedException("The generated schema has no supported type projection.");
            }

            var types = kind.ValueKind == JsonValueKind.Array
                ? kind.EnumerateArray().Select(value => value.GetString()!)
                : [kind.GetString()!];
            return string.Join(
                " | ",
                types.Select(value => value switch
                {
                    "string" => "string",
                    "integer" or "number" => "number",
                    "boolean" => "boolean",
                    "null" => "null",
                    "array" => "ReadonlyArray<" +
                        (node.TryGetProperty("items", out var items) ? Render(items) : "unknown") + ">",
                    "object" => ObjectType(node),
                    _ => throw new NotSupportedException("Unsupported schema type " + value),
                }));
        }

        string ObjectType(JsonElement node)
        {
            var required = node.TryGetProperty("required", out var requiredFields)
                ? requiredFields.EnumerateArray().Select(field => field.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
            var fields = node.TryGetProperty("properties", out var properties)
                ? properties
                    .EnumerateObject()
                    .Select(field =>
                        "readonly " + Quote(field.Name) + (required.Contains(field.Name) ? "" : "?") + ": " +
                        Render(field.Value))
                    .ToList()
                : [];
            if (node.TryGetProperty("additionalProperties", out var additional) &&
                additional.ValueKind != JsonValueKind.False)
                fields.Add("readonly [key: string]: " + Render(additional));
            return "{ " + string.Join("; ", fields) + " }";
        }
    }

    private const string Carrier = """
                                   function url(base, endpoint, stream = false) {
                                     return base.replace(/\/$/, '') + '/' + endpoint.split('/').map(encodeURIComponent).join('/') + (stream ? '/stream' : '');
                                   }
                                   export class RemoteError extends Error {
                                     constructor(code, message, details = {}) { super(message); this.name = 'RemoteError'; this.isDSHRemoteError = true; this.code = code; this.details = details; }
                                   }
                                   const failure = (code, message, details) => ({ok:false, error:new RemoteError(code, message, details)});
                                   const rebuild = result => result.ok ? result : failure(result.error.code, result.error.message, result.error.details);
                                   const withdrawn = () => failure('gateway/internal', 'The Remote contribution was withdrawn.');
                                   export async function mountRemote(ctx, base, fetcher = fetch) {
                                     const remote = createRemote(base, fetcher);
                                     const tableKey = Symbol.for('cordis.net.typert.remoteNamespaces');
                                     const registry = ctx.root[tableKey] ??= {groups:new Map(), retiring:new Map(), tail:Promise.resolve()};
                                     const table = registry.groups;
                                     const enqueue = operation => { const result = registry.tail.then(operation); registry.tail = result.catch(() => {}); return result; };
                                     const dispose = ctx.effect(async () => {
                                       let outcome;
                                       do {
                                       outcome = await enqueue(() => {
                                       const retirements = namespaceSpecs.map(([key]) => registry.retiring.get(key)).filter(Boolean);
                                       if (retirements.length) return {wait:Promise.all(retirements)};
                                       const installed = [];
                                       const cleanup = async () => {
                                         remote.dispose();
                                         const settlements = [];
                                         for (const [key, group, names] of installed.reverse()) {
                                           for (const name of names) delete group.service[name];
                                           if (!Object.keys(group.service).length && table.get(key) === group) {
                                             table.delete(key);
                                             const settlement = Promise.resolve().then(() => group.dispose());
                                             registry.retiring.set(key, settlement);
                                             const release = () => { if (registry.retiring.get(key) === settlement) registry.retiring.delete(key); };
                                             settlement.then(release, release);
                                             settlements.push(settlement);
                                           }
                                         }
                                         await Promise.all(settlements);
                                       };
                                       try {
                                         for (const [key, methods] of namespaceSpecs) {
                                           const group = table.get(key);
                                           if (!group && ctx.root.get(key) !== undefined) throw new Error('Remote namespace is already provided: ' + key);
                                           for (const [name] of methods) if (group && Object.hasOwn(group.service, name)) throw new Error('Remote method is already mounted: ' + key + '.' + name);
                                         }
                                         for (const [key, methods] of namespaceSpecs) {
                                           let group = table.get(key);
                                           if (!group) {
                                             const service = Object.create(null);
                                             for (const [name, endpoint] of methods) service[name] = remote[endpoint];
                                             group = {service, dispose:ctx.root.provide(key, service)};
                                             table.set(key, group);
                                           }
                                           const names = [];
                                           installed.push([key, group, names]);
                                           for (const [name, endpoint] of methods) { group.service[name] = remote[endpoint]; names.push(name); }
                                         }
                                         return {cleanup};
                                       } catch (error) { return {error, settlement:cleanup()}; }
                                       });
                                       if (outcome.wait) await outcome.wait;
                                       } while (outcome.wait);
                                       if (outcome.error) { await outcome.settlement; throw outcome.error; }
                                       return outcome.cleanup;
                                     });
                                     await dispose;
                                     return {remote, dispose};
                                   }
                                   async function unary(fetcher, base, endpoint, args, signal, state) {
                                     if (!state.active) return withdrawn();
                                     const lifetime = new AbortController();
                                     state.controllers.add(lifetime);
                                     const abort = () => lifetime.abort();
                                     signal?.addEventListener('abort', abort, {once:true});
                                     if (signal?.aborted) abort();
                                     try {
                                       const response = await fetcher(url(base, endpoint), { method: 'POST', headers: {'content-type': 'application/json'}, body: JSON.stringify({args}), signal:lifetime.signal });
                                       if (!state.active) return withdrawn();
                                       lifetime.signal.throwIfAborted();
                                       if (!response.ok) return failure('gateway/protocol', 'HTTP ' + response.status);
                                       const result = await response.json();
                                       if (!state.active) return withdrawn();
                                       lifetime.signal.throwIfAborted();
                                       return rebuild(result);
                                     } catch (error) {
                                       if (!state.active) return withdrawn();
                                       return failure(signal?.aborted ? 'gateway/cancelled' : 'gateway/internal', String(error));
                                     } finally {
                                       state.controllers.delete(lifetime); signal?.removeEventListener('abort', abort);
                                     }
                                   }
                                   async function* stream(fetcher, base, endpoint, args, signal, state) {
                                     if (!state.active) { yield withdrawn(); return; }
                                     const lifetime = new AbortController();
                                     state.controllers.add(lifetime);
                                     const abort = () => lifetime.abort();
                                     signal?.addEventListener('abort', abort, {once:true});
                                     if (signal?.aborted) abort();
                                     let reader;
                                     try {
                                       const response = await fetcher(url(base, endpoint, true), { method:'POST', headers:{'content-type':'application/json'}, body:JSON.stringify({args}), signal:lifetime.signal });
                                       if (!state.active) { yield withdrawn(); return; }
                                       lifetime.signal.throwIfAborted();
                                       if (!response.ok || !response.body) throw new Error('HTTP ' + response.status);
                                       reader = response.body.getReader();
                                       const decoder = new TextDecoder();
                                       let pending = '';
                                       while (true) {
                                         const chunk = await reader.read();
                                         if (!state.active) { yield withdrawn(); return; }
                                         lifetime.signal.throwIfAborted();
                                         pending += decoder.decode(chunk.value, {stream:!chunk.done});
                                         let end;
                                         while ((end = pending.indexOf('\n')) >= 0) {
                                           const line = pending.slice(0, end); pending = pending.slice(end + 1);
                                           if (line) { const item = rebuild(JSON.parse(line)); if (!state.active) {yield withdrawn(); return;} lifetime.signal.throwIfAborted(); yield item; if (!item.ok) return; }
                                         }
                                         if (chunk.done) { if (pending) throw new Error('Incomplete Remote stream frame'); return; }
                                       }
                                     } catch (error) {
                                       if (!state.active) {yield withdrawn(); return;}
                                       yield failure(lifetime.signal.aborted ? 'gateway/cancelled' : 'gateway/internal', String(error));
                                     } finally {
                                       lifetime.abort(); state.controllers.delete(lifetime); signal?.removeEventListener('abort', abort); await reader?.cancel().catch(() => {});
                                     }
                                   }
                                   """;
}
