using System.Text.Json;

namespace Cordis.Typert.Generator.Model;

internal static class DeclarationConstantEmitter
{
    internal static string Emit(NativeConstant value) => value.Kind switch
    {
        "carrierDefault" => "default",
        "emptyCollection" => "[]",
        "enum" => "(global::" + value.Type + ")" + value.Json,
        "type" => "typeof(" + JsonSerializer.Deserialize<string>(value.Json) + ")",
        "value" when value.Json == "null" && value.Type == "System.Guid" => "default(global::System.Guid)",
        "value" when value.Json == "null" => "null",
        "value" => value.Type.TrimEnd('?') switch
        {
            "string" or "System.String" => Literal(JsonSerializer.Deserialize<string>(value.Json)!),
            "char" or "System.Char" => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                JsonSerializer.Deserialize<string>(value.Json)![0],
                true),
            "float" or "System.Single" => FloatingPoint(value.Json, "Single", "F"),
            "double" or "System.Double" => FloatingPoint(value.Json, "Double", "D"),
            "decimal" or "System.Decimal" => value.Json + "M",
            "ulong" or "System.UInt64" => value.Json + "UL",
            "long" or "System.Int64" => value.Json + "L",
            "uint" or "System.UInt32" => value.Json + "U",
            _ => value.Json
        },
        _ => throw new InvalidDataException("Unsupported projected constant kind " + value.Kind)
    };

    private static string FloatingPoint(string json, string type, string suffix) => json.StartsWith('"')
        ? "global::System." + type + "." + (JsonSerializer.Deserialize<string>(json) switch
        {
            "NaN" => "NaN",
            "Infinity" => "PositiveInfinity",
            "-Infinity" => "NegativeInfinity",
            _ => throw new InvalidDataException("Unsupported floating-point constant " + json)
        })
        : json + suffix;

    private static string Literal(string value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, true);
}
