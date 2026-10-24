using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cordis.Composition;

public abstract partial class TypertCodec
{
    private const string SignedIntegerText = @"^[+-]?[0-9]+$(?![\s\S])";
    private const string UnsignedIntegerText = @"^[0-9]+$(?![\s\S])";
    private const string RealText = @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$(?![\s\S])";

    private static readonly IReadOnlyDictionary<string, Regex> numericPatterns = new[]
    {
        SignedIntegerText,
        UnsignedIntegerText,
        RealText,
        @"^-?(?:0|[1-9]\d*)$",
        @"^-?(?:0|[1-9]\d*)(?:\.\d+)?$",
        @"^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$"
    }.ToDictionary(
        pattern => pattern,
        pattern => new Regex(
            pattern
                .Replace("$(?![\\s\\S])", "\\z", StringComparison.Ordinal)
                .Replace("$", "\\z", StringComparison.Ordinal)
                .Replace("\\d", "[0-9]", StringComparison.Ordinal),
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
        StringComparer.Ordinal);

    private static JsonNode ProjectNumbers(JsonSchemaExporterContext context, JsonNode node, bool output)
    {
        var handling = context.PropertyInfo?.NumberHandling ?? context.TypeInfo.NumberHandling ??
            context.TypeInfo.Options.NumberHandling;
        var type = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
        if (type.IsEnum && node is JsonObject enumSchema && enumSchema["enum"] is JsonArray names &&
            names.Any(value => value is JsonValue name && name.TryGetValue<string>(out _)) &&
            (context.PropertyInfo?.CustomConverter ?? context.TypeInfo.Converter).GetType().Assembly ==
            typeof(JsonSerializer).Assembly)
        {
            // STJ's named-value list omits legal casing, flags and integer representations.
            // Its built-in converter remains the authority for admitting strings and enum values.
            enumSchema.Remove("enum");
            enumSchema["type"] = names.Any(value => value is null)
                ? new JsonArray("string", "integer", "null")
                : new JsonArray("string", "integer");
            return enumSchema;
        }
        var unsigned = type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong);
        var integer = unsigned || type == typeof(sbyte) || type == typeof(short) || type == typeof(int) ||
            type == typeof(long);
        var real = type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        if ((handling & ~JsonNumberHandling.AllowReadingFromString) != 0 &&
            (integer || real || context.PropertyInfo?.NumberHandling is not null ||
                context.TypeInfo.NumberHandling is not null))
            throw new NotSupportedException("Remote numeric metadata supports Strict and AllowReadingFromString only.");
        if ((!integer && !real) || node is not JsonObject schema || schema["type"] is null ||
            (context.PropertyInfo?.CustomConverter ?? context.TypeInfo.Converter).GetType().Assembly !=
            typeof(JsonSerializer).Assembly)
            return node;

        // Exporter unions describe reading and writing together. Keep the actual direction here.
        var kinds = schema["type"] is JsonArray array
            ? array.Select(value => value!.GetValue<string>()).ToArray()
            : [schema["type"]!.GetValue<string>()];
        if (output)
        {
            schema.Remove("pattern");
            var written = kinds.Where(kind => kind != "string").ToArray();
            schema["type"] = written.Length == 1
                ? JsonValue.Create(written[0])
                : new JsonArray(written.Select(kind => (JsonNode?)JsonValue.Create(kind)).ToArray());
        }
        else if (kinds.Contains("string", StringComparer.Ordinal))
        {
            // Native parsers admit leading zeros, a sign on signed values, and decimal exponents.
            schema["pattern"] = unsigned ? UnsignedIntegerText : integer ? SignedIntegerText : RealText;
        }

        if (type == typeof(float) || type == typeof(double))
        {
            schema["format"] = type == typeof(float) ? "float" : "double";
        }

        return schema;
    }

    private static bool FiniteNumericText(string value, JsonElement schema)
    {
        if (!schema.TryGetProperty("format", out var format))
            return true;
        return format.GetString() switch
        {
            "float" => float.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var single) && float.IsFinite(single),
            "double" => double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var real) && double.IsFinite(real),
            _ => true
        };
    }
}
