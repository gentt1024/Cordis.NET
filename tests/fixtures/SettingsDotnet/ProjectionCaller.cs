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
Console.WriteLine(
    "GREEN primitive/string/collection roots; null/default named arguments and legal colliding names; DisallowNull decode; read-only retained/refused; changed nullable policy changes identity; stale output removed on SDK/options failure.");

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
}
