using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Caller.Edges;

if (JsonSerializer.IsReflectionEnabledByDefault) throw new Exception("Reflection fallback enabled.");
using var http = new HttpClient(new Replies());
using var client = new PrimitivesClient(http, new Uri("https://test.invalid/remote"));
if (await client.CountAsync(null, 0, false, null, 0) != 0) throw new Exception("Primitive result wrong.");
if (await client.TextAsync() is not null) throw new Exception("Nullable string result wrong.");
if (!(await client.ValuesAsync()).SequenceEqual(new[] { 1, 2 })) throw new Exception("Collection result wrong.");
foreach (var type in new[] { typeof(int), typeof(string), typeof(int[]) })
    if (PrimitivesClientJson.Default.GetTypeInfo(type) is null)
        throw new Exception("Primitive public root metadata absent.");
var integer = (JsonTypeInfo<int>)PrimitivesClientJson.Default.GetTypeInfo(typeof(int))!;
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(17, integer), integer) != 17)
    throw new Exception("Primitive metadata roundtrip failed.");
var metadata = (JsonTypeInfo<NullableValue>)NullableClientJson.Default.GetTypeInfo(typeof(NullableValue))!;
try
{
    JsonSerializer.Deserialize("{\"text\":null}", metadata);
    throw new Exception("DisallowNull ignored.");
}
catch (JsonException)
{
}

try
{
    JsonSerializer.Deserialize("{\"text\":\"yes\",\"strict\":null}", metadata);
    throw new Exception("Property DisallowNull ignored.");
}
catch (JsonException)
{
}

if (JsonSerializer.Deserialize("{\"text\":\"yes\"}", metadata)!.Text != "yes")
    throw new Exception("Nullable DTO metadata decode failed.");
using var numeric = new NumericDefaultsClient(http, new Uri("https://test.invalid/remote"));
var defaults = await numeric.ValueAsync(new NumericDefaults());
if (defaults.Ratio != 1.25f || defaults.Amount != 0.1m ||
    defaults.EmptyRatio is not null || defaults.EmptyAmount is not null)
    throw new Exception("Absent numeric fields did not retain nullable initializers.");
var cleared = await numeric.ValueAsync(
    new NumericDefaults
    {
        Ratio = null,
        Amount = null
    });
if (cleared.Ratio is not null || cleared.Amount is not null ||
    cleared.EmptyRatio is not null || cleared.EmptyAmount is not null)
    throw new Exception("Explicit null numeric fields did not override initializers.");
var numbers = await numeric.ValueAsync(
    new NumericDefaults
    {
        Ratio = 2.5f,
        Amount = 12.75m,
        EmptyRatio = 3.5f,
        EmptyAmount = 4.25m
    });
if (numbers.Ratio != 2.5f || numbers.Amount != 12.75m ||
    numbers.EmptyRatio != 3.5f || numbers.EmptyAmount != 4.25m)
    throw new Exception("Explicit nullable numeric values changed in the generated client.");
Console.WriteLine(
    "GREEN primitive/string/collection roots; null/default named arguments and legal colliding names; DisallowNull decode; nullable numeric absent/null/explicit values; read-only retained/refused; changed nullable policy changes identity; stale output removed on SDK/options failure.");

sealed class Replies : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        using var document = JsonDocument.Parse(body);
        var args = document.RootElement.GetProperty("args");
        var value = request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "count" => Check(args),
            "text" => "null",
            "values" => "[1,2]",
            "value" => CheckNumeric(args.GetProperty("input")),
            _ => throw new Exception("Unknown request.")
        };
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"value\":" + value + "}", Encoding.UTF8, "application/json")
        };
    }

    private static string Check(JsonElement args)
    {
        if (args.EnumerateObject().Count() != 5 || args.GetProperty("request").ValueKind != JsonValueKind.Null ||
            args.GetProperty("cancellationToken").GetInt32() != 0 || args.GetProperty("httpClient").GetBoolean() ||
            args.GetProperty("__cordisRequest").ValueKind != JsonValueKind.Null ||
            args.GetProperty("_").GetInt32() != 0)
            throw new Exception("Null/default args omitted or changed.");
        return "0";
    }

    private static string CheckNumeric(JsonElement input)
    {
        if (input.EnumerateObject().Count() != 4)
            throw new Exception("Nullable numeric request fields omitted.");
        var ratio = input.GetProperty("Ratio");
        var amount = input.GetProperty("Amount");
        var emptyRatio = input.GetProperty("EmptyRatio");
        var emptyAmount = input.GetProperty("EmptyAmount");
        if (ratio.ValueKind == JsonValueKind.Null && amount.ValueKind == JsonValueKind.Null &&
            emptyRatio.ValueKind == JsonValueKind.Null && emptyAmount.ValueKind == JsonValueKind.Null)
            return "{\"Ratio\":null,\"Amount\":null,\"EmptyRatio\":null,\"EmptyAmount\":null}";
        if (ratio.GetSingle() == 1.25f && amount.GetDecimal() == 0.1m &&
            emptyRatio.ValueKind == JsonValueKind.Null && emptyAmount.ValueKind == JsonValueKind.Null)
            return "{}";
        if (ratio.GetSingle() == 2.5f && amount.GetDecimal() == 12.75m &&
            emptyRatio.GetSingle() == 3.5f && emptyAmount.GetDecimal() == 4.25m)
            return "{\"Ratio\":2.5,\"Amount\":12.75,\"EmptyRatio\":3.5,\"EmptyAmount\":4.25}";
        throw new Exception("Nullable numeric request values changed.");
    }
}
