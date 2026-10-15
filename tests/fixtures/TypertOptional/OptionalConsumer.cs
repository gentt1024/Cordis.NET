using System.Net;
using System.Text;
using System.Text.Json;
using Cordis;
using Cordis.Composition;
using OptionalAuthor;
using OptionalCaller;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Reflection serialization is enabled.");
await using var root = new Context();
var service = new OptionalService();
var registry = new TypertRegistry(root);
await root.RunAsync(owner =>
{
    owner.Provide("optional", service);
    registry.Register(owner, OptionalServiceTypert.Contribution("optional-fixture"));
    owner.Provide("nonfinite", new NonfiniteService());
    registry.Register(owner, NonfiniteServiceTypert.Contribution("optional-fixture.nonfinite"));
    return Task.CompletedTask;
});
var gateway = new TypertGateway(root, registry);
using var transport = new GatewayTransport(gateway);
using var http = new HttpClient(transport);
await using var connection = new TypertConnection(http, new Uri("https://test.invalid/remote"));
await using var client = new OptionalClient(connection);

if ((await client.EditAsync(Json("{\"value\":\"first\"}"))).Value != "first:absent")
    throw new Exception("Omitted optional revision was not mapped to the C# default.");
if (transport.LastArguments.TryGetProperty("expectedRevision", out _))
    throw new Exception("The optional non-null wire parameter was serialized as null.");
if ((await client.EditAsync(Json("{\"value\":\"second\"}"), 2.5)).Value != "second:2.5" ||
    transport.LastArguments.GetProperty("expectedRevision").GetDouble() != 2.5)
    throw new Exception("A supplied numeric revision changed in transit.");

if ((await client.DefaultsAsync()).Value != "7:null:False:1.25:0.1")
    throw new Exception("Generated .NET methods lost ordinary constant defaults.");
if (transport.LastArguments.EnumerateObject().Count() != 5 ||
    transport.LastArguments.GetProperty("limit").GetInt32() != 7 ||
    transport.LastArguments.GetProperty("text").ValueKind != JsonValueKind.Null ||
    transport.LastArguments.GetProperty("enabled").GetBoolean() ||
    transport.LastArguments.GetProperty("ratio").GetSingle() != 1.25f ||
    transport.LastArguments.GetProperty("amount").GetDecimal() != 0.1m)
    throw new Exception("The carrier omitted required explicit null/default argument values.");
if ((await gateway.InvokeAsync("optional/defaults", Json("{}"))).Value!.Value.GetString() != "7:null:False:1.25:0.1")
    throw new Exception("An omitted optional constant did not reach the authored default.");
if ((await gateway.InvokeAsync("optional/defaults", Json("{\"limit\":3,\"text\":null,\"enabled\":true}")))
    .Value!.Value.GetString() != "3:null:True:1.25:0.1")
    throw new Exception("Explicit nullable/default values changed in the binding.");
var nonfinite = await gateway.InvokeAsync("nonfinite/isNaN", Json("{}"));
if (!nonfinite.Ok || !nonfinite.Value!.Value.GetBoolean())
    throw new Exception("The Host binding lost its authored nonfinite default when the argument was omitted.");

if ((await client.NullableAsync()).Value is not null ||
    transport.LastArguments.GetProperty("revision").ValueKind != JsonValueKind.Null)
    throw new Exception("A nullable parameter's explicit null was silently omitted.");
if ((await client.NullableAsync(4)).Value != 4 ||
    transport.LastArguments.GetProperty("revision").GetDouble() != 4)
    throw new Exception("The nullable parameter's supplied value changed.");

var acceptedCalls = service.Calls;
foreach (var (arguments, code) in new[]
         {
             ("{\"candidate\":{\"value\":\"bad\"},\"expectedRevision\":null}", "gateway/input-invalid"),
             ("{\"candidate\":{\"value\":\"bad\"},\"expectedRevision\":\"2.5\"}", "gateway/input-invalid"),
             ("{\"candidate\":{\"value\":\"bad\"},\"expectedRevision\":false}", "gateway/input-invalid"),
             ("{\"expectedRevision\":2.5}", "gateway/arguments-invalid")
         })
{
    var rejected = await gateway.InvokeAsync("optional/edit", Json(arguments));
    if (rejected.Ok || rejected.Error?.Code != code)
        throw new Exception("Invalid optional/required wire input changed its refusal: " + arguments);
}

foreach (var (arguments, code) in new[]
         {
             ("{}", "gateway/arguments-invalid"),
             ("{\"revision\":null}", "gateway/input-invalid"),
             ("{\"revision\":\"2\"}", "gateway/input-invalid")
         })
{
    var rejected = await gateway.InvokeAsync("optional/required", Json(arguments));
    if (rejected.Ok || rejected.Error?.Code != code)
        throw new Exception("Required numeric input was weakened by optional support.");
}

if (service.Calls != acceptedCalls)
    throw new Exception("A refused input reached the authored method.");
if ((await client.RequiredAsync(9)).Value != 9)
    throw new Exception("A required numeric input failed.");

Console.WriteLine(
    "GREEN optional numeric omission/value/null refusal; authored finite/nonfinite constant defaults; nullable explicit null; required wire validation; generated .NET carrier and binding.");

static JsonElement Json(string text)
{
    using var document = JsonDocument.Parse(text);
    return document.RootElement.Clone();
}

sealed class GatewayTransport(TypertGateway gateway) : HttpMessageHandler
{
    public JsonElement LastArguments
    {
        get;
        private set;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        LastArguments = body.RootElement.GetProperty("payload").GetProperty("args").Clone();
        var endpoint = string.Join('/', request.RequestUri!.Segments.TakeLast(2).Select(value => value.Trim('/')));
        var result = await gateway.InvokeAsync(endpoint, LastArguments);
        var json = result.Ok
            ? "{\"ok\":true,\"value\":" + result.Value!.Value.GetRawText() + "}"
            : "{\"ok\":false,\"error\":{\"code\":\"" + result.Error!.Code +
            "\",\"message\":\"fixture refusal\",\"details\":{}}}";
        json = "{\"type\":\"server-response\",\"rpcId\":" + body.RootElement.GetProperty("rpcId").GetRawText() +
            ",\"result\":" + json + "}";
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
