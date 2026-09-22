using System.Buffers;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

/// <summary>Optional data-only configuration binding through explicit JSON metadata.</summary>
public static class ConfigBinding
{
    /// <summary>Creates a plugin configuration binder without reflecting over arbitrary input objects.</summary>
    /// <typeparam name="T">The configuration contract described by <paramref name="typeInfo"/>.</typeparam>
    /// <param name="typeInfo">Explicit metadata, normally supplied by a generated JsonSerializerContext.</param>
    /// <param name="validate">Optional business rules. Return no issues for success; include field paths in issues where applicable.</param>
    /// <returns>A delegate for <see cref="Plugin{T}.Config"/>. Binding and validation issues have distinct stage prefixes.</returns>
    /// <remarks>
    /// Missing and null roots are rejected, including undefined/null JsonElements. Wrap this delegate explicitly
    /// when a plugin needs different root defaults. Non-null scalar roots are supported when the metadata accepts them.
    /// Inputs may contain strings, booleans, standard integer types, decimal, finite float/double values,
    /// string/object dictionaries (mutable or read-only), IList sequences and JsonElement data, nested up to 64 levels.
    /// JsonElement numbers must fit a finite double. Nested nulls are supported; nested undefined values, cycles,
    /// delegates, arbitrary objects and unevaluated expressions are rejected. Shared acyclic children are allowed.
    /// Metadata controls constructors, initializers, required members, naming, enum and unknown-member behavior.
    /// Generated metadata may replace absent init-only property initializers with CLR defaults; constructor
    /// parameter defaults are a supported way to express missing-field defaults. The adapter does not repair metadata.
    /// This adapter neither mutates nor retains raw data, and never changes a fiber's RawConfig.
    /// The returned delegate retains its metadata and validator; release it with its plugin when unloading CLR modules.
    /// There is no static metadata cache. Exceptions from user business rules propagate unchanged.
    /// </remarks>
    public static Func<object?, ConfigResult<T>> FromJsonTypeInfo<T>(
        JsonTypeInfo<T> typeInfo, Func<T, IEnumerable<string>>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return raw =>
        {
            if (raw is Undefined || raw is JsonElement { ValueKind: JsonValueKind.Undefined })
                return ConfigResult<T>.Failure("binding at $: configuration is missing.");
            if (raw is null || raw is JsonElement { ValueKind: JsonValueKind.Null })
                return ConfigResult<T>.Failure("binding at $: configuration is explicitly null.");

            T? value;
            try
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 64 }))
                    Write(writer, raw, "$", 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
                value = JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo);
            }
            catch (DataBindingException error)
            {
                return ConfigResult<T>.Failure($"binding at {error.Path}: {error.Message}");
            }
            catch (JsonException error)
            {
                return ConfigResult<T>.Failure($"binding at {error.Path ?? "$"}: {error.Message}");
            }
            catch (NotSupportedException error)
            {
                return ConfigResult<T>.Failure($"binding at $: {error.Message}");
            }

            if (value is null)
                return ConfigResult<T>.Failure("binding at $: metadata produced a null configuration.");
            if (validate is not null)
            {
                var issues = validate(value).Select(issue => $"validation: {issue}").ToArray();
                if (issues.Length > 0) return ConfigResult<T>.Failure(issues);
            }
            return ConfigResult<T>.Success(value);
        };
    }

    private static void Write(Utf8JsonWriter writer, object? value, string path, int depth, HashSet<object> ancestors)
    {
        if (depth > 64) throw new DataBindingException(path, "data exceeds the maximum depth of 64.");
        switch (value)
        {
            case null: writer.WriteNullValue(); return;
            case string text: writer.WriteStringValue(text); return;
            case bool boolean: writer.WriteBooleanValue(boolean); return;
            case byte number: writer.WriteNumberValue(number); return;
            case sbyte number: writer.WriteNumberValue(number); return;
            case short number: writer.WriteNumberValue(number); return;
            case ushort number: writer.WriteNumberValue(number); return;
            case int number: writer.WriteNumberValue(number); return;
            case uint number: writer.WriteNumberValue(number); return;
            case long number: writer.WriteNumberValue(number); return;
            case ulong number: writer.WriteNumberValue(number); return;
            case decimal number: writer.WriteNumberValue(number); return;
            case float number when float.IsFinite(number): writer.WriteNumberValue(number); return;
            case double number when double.IsFinite(number): writer.WriteNumberValue(number); return;
            case float or double: throw new DataBindingException(path, "numbers must be finite.");
            case JsonElement element: WriteElement(writer, element, path, depth, ancestors); return;
            case Undefined: throw new DataBindingException(path, "undefined is not a data value.");
        }

        if (!ancestors.Add(value)) throw new DataBindingException(path, "cyclic data is not supported.");
        try
        {
            switch (value)
            {
                case IDictionary<string, object?> map:
                    WriteMap(writer, map, path, depth, ancestors);
                    break;
                case IReadOnlyDictionary<string, object?> map:
                    WriteMap(writer, map, path, depth, ancestors);
                    break;
                case IList list:
                    CheckContainerDepth(path, depth);
                    writer.WriteStartArray();
                    for (var index = 0; index < list.Count; index++)
                        Write(writer, list[index], $"{path}[{index}]", depth + 1, ancestors);
                    writer.WriteEndArray();
                    break;
                default:
                    throw new DataBindingException(path, $"unsupported data type '{value.GetType().FullName}'.");
            }
        }
        finally { ancestors.Remove(value); }
    }

    private static void WriteMap(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> map,
        string path, int depth, HashSet<object> ancestors)
    {
        CheckContainerDepth(path, depth);
        writer.WriteStartObject();
        foreach (var pair in map)
        {
            writer.WritePropertyName(pair.Key);
            Write(writer, pair.Value, PropertyPath(path, pair.Key), depth + 1, ancestors);
        }
        writer.WriteEndObject();
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, string path, int depth, HashSet<object> ancestors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                CheckContainerDepth(path, depth);
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, PropertyPath(path, property.Name), depth + 1, ancestors);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                CheckContainerDepth(path, depth);
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray()) Write(writer, item, $"{path}[{index++}]", depth + 1, ancestors);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new DataBindingException(path, "numbers must fit a finite double.");
                element.WriteTo(writer);
                break;
            case JsonValueKind.Undefined:
                throw new DataBindingException(path, "undefined is not a data value.");
            default: element.WriteTo(writer); break;
        }
    }

    private static void CheckContainerDepth(string path, int depth)
    {
        if (depth >= 64) throw new DataBindingException(path, "data exceeds the maximum depth of 64.");
    }

    private static string PropertyPath(string path, string key) =>
        $"{path}['{key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}']";

    private sealed class DataBindingException(string path, string message) : Exception(message)
    {
        public string Path { get; } = path;
    }
}
