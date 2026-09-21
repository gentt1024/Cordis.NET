using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
namespace Cordis.Composition;

/// <summary>Reflection-free YAML/JSON data codec with the Loader !!js dialect.</summary>
public static class ConfigurationFile
{
    /// <summary>
    /// Parses the requested value.
    /// </summary>
    public static object? Parse(string text, bool json = false)
    {
        if (json) { using var document = JsonDocument.Parse(text); return FromJson(document.RootElement); }
        var parser = new Parser(new StringReader(text));
        parser.Consume<StreamStart>();
        if (parser.Accept<StreamEnd>(out _)) return null;
        parser.Consume<DocumentStart>();
        var anchors = new Dictionary<string, object?>(StringComparer.Ordinal);
        var value = Read(parser, anchors);
        parser.Consume<DocumentEnd>(); parser.Consume<StreamEnd>(); return value;
    }
    /// <summary>
    /// Parses entries.
    /// </summary>
    public static List<EntryOptions> ParseEntries(string text, bool json = false) => Data.Entries(Parse(text, json));
    /// <summary>
    /// Reads entries async.
    /// </summary>
    public static async Task<List<EntryOptions>> ReadEntriesAsync(string path, CancellationToken cancellationToken = default) => ParseEntries(await File.ReadAllTextAsync(path, cancellationToken), Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase));
    private static object? Read(IParser parser, Dictionary<string, object?> anchors)
    {
        if (parser.TryConsume<AnchorAlias>(out var alias)) return anchors.TryGetValue(alias.Value.Value, out var value) ? value : throw new FormatException($"Unknown YAML alias {alias.Value}.");
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            object? value;
            var tag = scalar.Tag.IsEmpty ? "" : scalar.Tag.Value;
            if (tag == "tag:yaml.org,2002:js") value = scalar.Value.Length == 0 ? throw new FormatException("!!js requires a scalar expression.") : new JsExpression(scalar.Value);
            else if (tag.Length > 0) value = ResolveTaggedScalar(tag, scalar.Value);
            else if (scalar.Style != ScalarStyle.Plain || !TryResolveScalar(scalar.Value, out value)) value = scalar.Value;
            if (!scalar.Anchor.IsEmpty) anchors[scalar.Anchor.Value] = value;
            return value;
        }
        if (parser.TryConsume<SequenceStart>(out var sequence))
        {
            var list = new List<object?>(); if (!sequence.Anchor.IsEmpty) anchors[sequence.Anchor.Value] = list;
            while (!parser.Accept<SequenceEnd>(out _)) list.Add(Read(parser, anchors));
            parser.Consume<SequenceEnd>(); return list;
        }
        var mapping = parser.Consume<MappingStart>();
        var result = new EntryOptions(); if (!mapping.Anchor.IsEmpty) anchors[mapping.Anchor.Value] = result;
        while (!parser.Accept<MappingEnd>(out _))
        {
            var key = Read(parser, anchors) as string ?? throw new FormatException("Mapping keys must be strings.");
            if (!result.TryAdd(key, Read(parser, anchors))) throw new FormatException($"Duplicate mapping key '{key}'.");
        }
        parser.Consume<MappingEnd>(); return result;
    }

    private static readonly Regex IntegerPattern = new(@"^[+-]?(?:0b[01]+|0o[0-7]+|0x[0-9a-fA-F]+|[0-9]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex FloatPattern = new(@"^(?:[-+]?[0-9]+(?:\.[0-9]*)?(?:[eE][-+]?[0-9]+)?|\.[0-9]+(?:[eE][-+]?[0-9]+)?|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))$", RegexOptions.CultureInvariant);
    private static object? ResolveTaggedScalar(string tag, string text) => tag switch
    {
        "tag:yaml.org,2002:str" => text,
        "tag:yaml.org,2002:null" when IsNull(text) => null,
        "tag:yaml.org,2002:bool" when TryBoolean(text, out var boolean) => boolean,
        "tag:yaml.org,2002:int" when TryInteger(text, out var integer) => integer,
        "tag:yaml.org,2002:float" when TryFloat(text, out var number) => number,
        "tag:yaml.org,2002:null" or "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float"
            => throw new FormatException($"Cannot resolve scalar '{text}' with explicit YAML tag {tag}."),
        _ => throw new FormatException($"Unsupported YAML tag {tag}.")
    };
    private static bool TryResolveScalar(string text, out object? value)
    {
        if (IsNull(text)) { value = null; return true; }
        if (TryBoolean(text, out var boolean)) { value = boolean; return true; }
        if (TryInteger(text, out var integer)) { value = integer; return true; }
        if (TryFloat(text, out var number)) { value = number; return true; }
        value = null; return false;
    }
    private static bool IsNull(string text) => text is "" or "~" or "null" or "Null" or "NULL";
    private static bool TryBoolean(string text, out bool value)
    {
        if (text is "true" or "True" or "TRUE") { value = true; return true; }
        if (text is "false" or "False" or "FALSE") { value = false; return true; }
        value = false; return false;
    }
    private static bool TryInteger(string text, out object value)
    {
        value = 0L;
        if (!IntegerPattern.IsMatch(text)) return false;
        var negative = text[0] == '-';
        var offset = text[0] is '+' or '-' ? 1 : 0;
        var number = text[offset..];
        var radix = 10;
        if (number.Length > 1 && number[0] == '0')
        {
            radix = char.ToLowerInvariant(number[1]) switch { 'b' => 2, 'o' => 8, 'x' => 16, _ => 10 };
            if (radix != 10) number = number[2..];
        }
        BigInteger result = BigInteger.Zero;
        foreach (var character in number)
        {
            var digit = character <= '9' ? character - '0' : char.ToLowerInvariant(character) - 'a' + 10;
            result = result * radix + digit;
        }
        if (negative) result = -result;
        if (result >= long.MinValue && result <= long.MaxValue) { value = (long)result; return true; }
        var wide = (double)result;
        if (!double.IsFinite(wide)) return false;
        value = wide; return true;
    }
    private static bool TryFloat(string text, out double value)
    {
        value = 0;
        if (!FloatPattern.IsMatch(text)) return false;
        var normalized = text.ToLowerInvariant();
        if (normalized is ".nan") { value = double.NaN; return true; }
        if (normalized is ".inf" or "+.inf") { value = double.PositiveInfinity; return true; }
        if (normalized is "-.inf") { value = double.NegativeInfinity; return true; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new EntryOptions(element.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, FromJson(p.Value)))),
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var number) ? (object)number : element.GetDouble(),
        _ => null
    };
    /// <summary>
    /// Performs the write operation.
    /// </summary>
    public static string Write(object? value, bool json = false)
    {
        if (json)
        {
            using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) WriteJson(writer, value);
            return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        }
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var emitter = new Emitter(output); emitter.Emit(new StreamStart()); emitter.Emit(new DocumentStart()); WriteYaml(emitter, value); emitter.Emit(new DocumentEnd(true)); emitter.Emit(new StreamEnd()); return output.ToString();
    }
    private static void WriteYaml(IEmitter emitter, object? value)
    {
        switch (value)
        {
            case JsExpression expression: emitter.Emit(new Scalar(AnchorName.Empty, new TagName("tag:yaml.org,2002:js"), expression.Source, ScalarStyle.Plain, false, false)); break;
            case IDictionary<string, object?> expression when expression.TryGetValue("__jsExpr", out var source): emitter.Emit(new Scalar(AnchorName.Empty, new TagName("tag:yaml.org,2002:js"), (string)source!, ScalarStyle.Plain, false, false)); break;
            case IDictionary<string, object?> map:
                emitter.Emit(new MappingStart()); foreach (var pair in map) { WriteYamlString(emitter, pair.Key); WriteYaml(emitter, pair.Value); }
                emitter.Emit(new MappingEnd()); break;
            case IEnumerable<object?> list: emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block)); foreach (var item in list) WriteYaml(emitter, item); emitter.Emit(new SequenceEnd()); break;
            case string text:
                WriteYamlString(emitter, text); break;
            case double number when double.IsNaN(number): emitter.Emit(new Scalar(".nan")); break;
            case double number when double.IsPositiveInfinity(number): emitter.Emit(new Scalar(".inf")); break;
            case double number when double.IsNegativeInfinity(number): emitter.Emit(new Scalar("-.inf")); break;
            default: emitter.Emit(new Scalar(value is null ? "null" : value is bool b ? b ? "true" : "false" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null")); break;
        }
    }
    private static void WriteYamlString(IEmitter emitter, string text)
    {
        var quote = TryResolveScalar(text, out _);
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, text, quote ? ScalarStyle.DoubleQuoted : ScalarStyle.Any, true, true));
    }
    private static void WriteJson(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case JsExpression expression: writer.WriteStartObject(); writer.WriteString("__jsExpr", expression.Source); writer.WriteEndObject(); break;
            case IDictionary<string, object?> map: writer.WriteStartObject(); foreach (var pair in map) { writer.WritePropertyName(pair.Key); WriteJson(writer, pair.Value); } writer.WriteEndObject(); break;
            case IEnumerable<object?> list: writer.WriteStartArray(); foreach (var item in list) WriteJson(writer, item); writer.WriteEndArray(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case int integer: writer.WriteNumberValue(integer); break;
            case long integer: writer.WriteNumberValue(integer); break;
            case double number when !double.IsFinite(number): writer.WriteNullValue(); break;
            case double number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            default: throw new FormatException($"Unsupported configuration value {value.GetType().FullName}.");
        }
    }
}
