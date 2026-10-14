using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertNumericTests
{
    [Fact]
    public void Numeric_input_matches_native_reading_with_finite_values_and_directional_output()
    {
        Check<sbyte>();
        Check<byte>();
        Check<short>();
        Check<ushort>();
        Check<int>();
        Check<uint>();
        Check<long>();
        Check<ulong>();
        Check<float>();
        Check<double>();
        Check<decimal>();

        static void Check<T>() where T : struct
        {
            foreach (var handling in new[] { JsonNumberHandling.Strict, JsonNumberHandling.AllowReadingFromString })
            {
                var options = Options(handling);
                var metadata = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
                var codec = TypertCodec.Create(metadata);
                Assert.Equal(
                    typeof(T) == typeof(float) || typeof(T) == typeof(double) || typeof(T) == typeof(decimal)
                        ? "number"
                        : "integer",
                    codec.ResultSchema.GetProperty("type").GetString());
                foreach (var text in new[]
                         {
                             "0",
                             "-0",
                             "+1",
                             "01",
                             "-01",
                             "1e2",
                             "1.0",
                             ".5",
                             "1.",
                             " 1",
                             "1 ",
                             "1\n",
                             "1\0",
                             "١",
                             "NaN",
                             "Infinity",
                             "1e100",
                             "1e1000",
                             "255",
                             "256",
                             "-129",
                             "2147483648",
                             "18446744073709551616",
                             "79228162514264337593543950336",
                             "not a number"
                         })
                {
                    using var stringDocument = JsonDocument.Parse(JsonSerializer.Serialize(text));
                    Compare(stringDocument.RootElement);
                    JsonDocument numberDocument;
                    try
                    {
                        numberDocument = JsonDocument.Parse(text);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    using (numberDocument)
                        Compare(numberDocument.RootElement);
                }

                void Compare(JsonElement json)
                {
                    T expected;
                    try
                    {
                        expected = JsonSerializer.Deserialize(json, metadata);
                    }
                    catch (JsonException)
                    {
                        Assert.Throws<JsonException>(() => codec.Decode(json));
                        return;
                    }

                    if (expected is float single && !float.IsFinite(single) ||
                        expected is double real && !double.IsFinite(real))
                        Assert.Throws<JsonException>(() => codec.Decode(json));
                    else
                        Assert.Equal(expected, Assert.IsType<T>(codec.Decode(json)));
                }
            }
        }
    }

    [Fact]
    public void Web_DTO_collections_and_property_policy_keep_defaults_and_output_numbers()
    {
        var options = Options(JsonNumberHandling.AllowReadingFromString);
        var codec = TypertCodec.Create((JsonTypeInfo<Definition>)options.GetTypeInfo(typeof(Definition)));
        using var input = JsonDocument.Parse(
            """{"name":"sample","frame":{"scale":"+1e2"},"values":["01",null],"counts":{"one":"2"},"strict":3}""");
        var result = Assert.IsType<Definition>(codec.Decode(input.RootElement));
        Assert.True(result.FrameLocked);
        Assert.Equal(100m, result.Frame.Scale);
        Assert.Equal(new int?[] { 1, null }, result.Values);
        Assert.Equal(2u, result.Counts["one"]);
        var output = codec.Encode(result);
        Assert.Equal(JsonValueKind.Number, output.GetProperty("frame").GetProperty("scale").ValueKind);
        Assert.Equal(JsonValueKind.Number, output.GetProperty("values")[0].ValueKind);
        Assert.IsType<Definition>(codec.DecodeOutput(output));
        Assert.Throws<JsonException>(() => codec.DecodeOutput(input.RootElement));
        foreach (var json in new[]
                 {
                     "{}",
                     "{\"name\":null}",
                     "{\"name\":\"a\",\"strict\":\"3\"}",
                     "{\"name\":\"a\",\"frame\":{\"scale\":\"bad\"}}",
                     "{\"name\":\"a\",\"counts\":{\"one\":\"-1\"}}"
                 })
        {
            using var invalid = JsonDocument.Parse(json);
            Assert.Throws<JsonException>(() => codec.Decode(invalid.RootElement));
        }

        using var explicitValues = JsonDocument.Parse("""{"name":"sample","frameLocked":false,"frame":{"scale":0}}""");
        var explicitResult = Assert.IsType<Definition>(codec.Decode(explicitValues.RootElement));
        Assert.False(explicitResult.FrameLocked);
        Assert.Equal(0m, explicitResult.Frame.Scale);
    }

    [Theory]
    [InlineData(JsonNumberHandling.WriteAsString)]
    [InlineData(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    [InlineData(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public void Unsupported_numeric_policies_refuse_projection(JsonNumberHandling handling)
    {
        var codec = TypertCodec.Create((JsonTypeInfo<double>)Options(handling).GetTypeInfo(typeof(double)));
        Assert.Throws<NotSupportedException>(() => codec.Schema);
        Assert.Throws<NotSupportedException>(() => codec.ResultSchema);
    }

    private static JsonSerializerOptions Options(JsonNumberHandling handling)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = handling,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        options.MakeReadOnly();
        return options;
    }

    public sealed record Frame
    {
        public decimal Scale
        {
            get;
            init;
        } = 1;
    }

    public sealed record Definition
    {
        public required string Name
        {
            get;
            init;
        }

        public bool FrameLocked
        {
            get;
            init;
        } = true;

        public Frame Frame
        {
            get;
            init;
        } = new();

        public int?[] Values
        {
            get;
            init;
        } = [];

        public Dictionary<string, uint> Counts
        {
            get;
            init;
        } = [];

        [JsonNumberHandling(JsonNumberHandling.Strict)]
        public int Strict
        {
            get;
            init;
        }
    }
}
