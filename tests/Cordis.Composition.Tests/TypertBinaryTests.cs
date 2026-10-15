using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertBinaryTests
{
    [Fact]
    public void Encoded_string_annotation_does_not_promise_an_attachment()
    {
        using var schema = JsonDocument.Parse("""{"type":"string","contentEncoding":"base64"}""");
        var codec = TypertCodec.Create(BinaryJson.Default.String, schema: schema.RootElement);
        var result = codec.EncodeResult("AQID");
        Assert.Equal("AQID", result.Value!.Value.GetString());
        Assert.Empty(result.Attachments);
        Assert.Contains("export type Boundary0 = string;", Declaration(codec));
        using var shared = JsonDocument.Parse(
            """
            {"type":"object","properties":{"Bytes":{"$ref":"#/$defs/text"},"Text":{"$ref":"#/$defs/text"}},
             "required":["Bytes","Text"],"$defs":{"text":{"type":"string","contentEncoding":"base64"}}}
            """);
        var mixed = TypertCodec.Create(BinaryJson.Default.BinaryLabel, schema: shared.RootElement);
        var encoded = mixed.EncodeResult(new BinaryLabel([1], "AQ=="));
        Assert.Equal("Bytes", Assert.Single(encoded.Attachments).Path[0].GetString());
        Assert.Equal("AQ==", encoded.Value!.Value.GetProperty("Text").GetString());
        var types = Declaration(mixed);
        Assert.Contains("export type Boundary0Ref1 = Uint8Array;", types);
        Assert.Contains("export type Boundary0Ref2 = string;", types);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Binary_result_and_generated_type_follow_metadata_including_empty_and_nested_values(bool empty)
    {
        byte[] bytes = empty ? [] : [1, 2, 3];
        var root = TypertCodec.Create(BinaryJson.Default.ByteArray);
        var result = root.EncodeResult(bytes);
        Assert.Equal(JsonValueKind.Null, result.Value!.Value.ValueKind);
        var attachment = Assert.Single(result.Attachments);
        Assert.Empty(attachment.Path);
        Assert.Equal(bytes, attachment.Bytes.ToArray());
        Assert.Contains("export type Boundary0 = Uint8Array;", Declaration(root));
        var nullable = TypertCodec.CreateNullable(BinaryJson.Default.ByteArray);
        Assert.Contains("Uint8Array | null", Declaration(nullable));
        Assert.Equal(JsonValueKind.Null, nullable.EncodeResult(null).Value!.Value.ValueKind);
        Assert.Empty(nullable.EncodeResult(null).Attachments);
        var nested = TypertCodec.Create(BinaryJson.Default.BinaryRecord);
        var encoded = nested.EncodeResult(new BinaryRecord(bytes, [bytes], bytes));
        Assert.Equal(2, encoded.Attachments.Count);
        Assert.Equal("data", encoded.Attachments[0].Path[0].GetString());
        Assert.Equal("items", encoded.Attachments[1].Path[0].GetString());
        Assert.Equal(0, encoded.Attachments[1].Path[1].GetInt32());
        Assert.Equal(Convert.ToHexString(bytes), encoded.Value!.Value.GetProperty("custom").GetString());
        var declaration = Declaration(nested);
        Assert.Contains("\"data\": Uint8Array", declaration);
        Assert.Contains("\"items\": ReadonlyArray<Uint8Array>", declaration);
        Assert.DoesNotContain("\"custom\": Uint8Array", declaration);
    }

    [Fact]
    public void Type_converter_keeps_its_authored_result_without_binary_extraction()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new HexBytesConverter());
        var context = new BinaryJson(options);
        using var schema = JsonDocument.Parse("""{"type":"string","contentEncoding":"base64"}""");
        var codec = TypertCodec.Create(context.ByteArray, schema: schema.RootElement);
        var result = codec.EncodeResult(new byte[] { 0xFA });
        Assert.Equal("FA", result.Value!.Value.GetString());
        Assert.Empty(result.Attachments);
        Assert.Contains("export type Boundary0 = string;", Declaration(codec));
    }

    [Theory]
    [InlineData("""{"type":"string","enum":["AQID",""]}""", "Uint8Array", false)]
    [InlineData("""{"const":"AQID"}""", "Uint8Array", false)]
    [InlineData("""{"enum":["AQID",null]}""", "Uint8Array | null", false)]
    [InlineData("""{"const":null}""", "null", true)]
    public void Binary_literal_constraints_project_attachment_types_and_preserve_null(
        string schemaText,
        string type,
        bool nullValue)
    {
        using var schema = JsonDocument.Parse(schemaText);
        var codec = TypertCodec.Create(BinaryJson.Default.ByteArray, schema: schema.RootElement);
        var encoded = codec.EncodeResult(nullValue ? null : new byte[] { 1, 2, 3 });
        Assert.Equal(nullValue ? 0 : 1, encoded.Attachments.Count);
        Assert.Contains("export type Boundary0 = " + type + ";", Declaration(codec));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Collection_factories_keep_result_attachments_and_client_types_consistent(bool dictionary, bool nullable)
    {
        byte[] bytes = [1, 2, 3];
        using var schema = JsonDocument.Parse("""{"type":"string","contentEncoding":"base64"}""");
        var item = TypertCodec.Create(BinaryJson.Default.ByteArray, schema: schema.RootElement);
        var wrapped = dictionary
            ? TypertCodec.CreateDictionary(BinaryJson.Default.DictionaryStringByteArray, item, nullable: nullable)
            : TypertCodec.CreateSequence(BinaryJson.Default.ByteArrayArray, item, nullable: nullable);
        var direct = dictionary
            ? TypertCodec.Create(BinaryJson.Default.DictionaryStringByteArray)
            : TypertCodec.Create(BinaryJson.Default.ByteArrayArray);
        object value = dictionary
            ? new Dictionary<string, byte[]>
            {
                ["data"] = bytes
            }
            : new byte[][] { bytes };
        foreach (var codec in new[] { direct, wrapped })
        {
            var result = codec.EncodeResult(value);
            var attachment = Assert.Single(result.Attachments);
            Assert.Equal(bytes, attachment.Bytes.ToArray());
            Assert.Single(attachment.Path);
            if (dictionary)
                Assert.Equal("data", attachment.Path[0].GetString());
            else
                Assert.Equal(0, attachment.Path[0].GetInt32());
            Assert.Contains(dictionary ? "[key: string]: Uint8Array" : "ReadonlyArray<Uint8Array>", Declaration(codec));
        }

        if (nullable)
        {
            Assert.Contains(" | null", Declaration(wrapped));
            var result = wrapped.EncodeResult(null);
            Assert.Equal(JsonValueKind.Null, result.Value!.Value.ValueKind);
            Assert.Empty(result.Attachments);
        }
    }

    private static string Declaration(TypertCodec codec) => TypertArtifacts.GenerateClient(
            new TypertContribution("binary", [new("binary#read", "binary", "binary", "read", [], codec)]))
        .Declaration;
}

internal sealed record BinaryLabel(byte[] Bytes, string Text);

internal sealed record BinaryRecord(
    [property: JsonPropertyName("data")] byte[] Data,
    [property: JsonPropertyName("items")] byte[][] Items,
    [property: JsonPropertyName("custom"), JsonConverter(typeof(HexBytesConverter))]
    byte[] Custom);

internal sealed class HexBytesConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Convert.FromHexString(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Convert.ToHexString(value));
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(byte[][]))]
[JsonSerializable(typeof(Dictionary<string, byte[]>))]
[JsonSerializable(typeof(BinaryRecord))]
[JsonSerializable(typeof(BinaryLabel))]
internal partial class BinaryJson : JsonSerializerContext;
