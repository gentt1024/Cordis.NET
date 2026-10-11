using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ordinary = IndependentDeclarations.Data.SharedData;
using Remote = IndependentDeclarations.Wire.SharedData;
using IndependentDeclarations.Data;
using IndependentDeclarations.Wire;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Shared declaration consumer enabled reflection fallback.");
var ordinary = (JsonTypeInfo<Ordinary>)OrdinaryDataJson.Default.GetTypeInfo(typeof(Ordinary))!;
var remote = (JsonTypeInfo<Remote>)SharedClientJson.Default.GetTypeInfo(typeof(Remote))!;
var normal = JsonSerializer.Deserialize("""{"display_name":"ordinary","Child":{"display_name":"child"}}""", ordinary)!;
Check(normal.Count == 7 && normal.Note == "initial" && normal.Child?.Count == 7, "ordinary declaration defaults and recursive reference");
var wire = JsonSerializer.Deserialize("""{"display_name":"remote","child":{"display_name":"child"}}""", remote)!;
Check(wire.Count == 7 && wire.Note == "initial" && wire.Child?.Count == 7, "Remote projection shares defaults and recursive reference");
Check(JsonSerializer.Deserialize("""{"display_name":"ordinary","Note":null}""", ordinary)!.Note is null,
    "ordinary explicit null");
Check(JsonSerializer.Deserialize("""{"display_name":"remote","note":null}""", remote)!.Note is null,
    "Remote explicit null");
Reject("{}", ordinary);
Reject("{}", remote);
Reject("""{"display_name":null}""", ordinary);
Reject("""{"display_name":null}""", remote);
using var normalJson = JsonDocument.Parse(JsonSerializer.Serialize(normal, ordinary));
Check(normalJson.RootElement.GetProperty("Count").GetInt32() == 7 && !normalJson.RootElement.TryGetProperty("count", out _),
    "ordinary root retains declaration member names");
using var http = new HttpClient(new EchoTransport());
using var client = new SharedClient(http, new Uri("https://shared.invalid/remote"));
var response = await client.EchoAsync(wire);
Check(response.Name == "response" && response.Note is null && response.Count == 7 && response.Child?.Name == "nested",
    "generated Remote method uses the shared declaration projection");
Console.WriteLine("shared ordinary and Remote declaration consumer passed");

static void Check(bool value, string message)
{
    if (!value)
        throw new Exception(message);
}

static void Reject<T>(string json, JsonTypeInfo<T> metadata)
{
    try
    {
        JsonSerializer.Deserialize(json, metadata);
    }
    catch (JsonException) { return; }
    throw new Exception("Shared declaration accepted missing or null required data.");
}

sealed class EchoTransport : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath != "/remote/sharedData/echo")
            throw new Exception("Wrong generated Remote endpoint.");
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var value = document.RootElement.GetProperty("args").GetProperty("value");
        if (value.GetProperty("display_name").GetString() != "remote" || value.GetProperty("count").GetInt32() != 7 ||
            value.GetProperty("child").GetProperty("display_name").GetString() != "child" || value.TryGetProperty("Count", out _))
            throw new Exception("Serializer-specific names did not project the shared declaration.");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true,"value":{"display_name":"response","note":null,"child":{"display_name":"nested"}}}""",
                Encoding.UTF8, "application/json")
        };
    }
}
