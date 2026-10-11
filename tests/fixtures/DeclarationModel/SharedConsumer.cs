using System.Text;
using System.Text.Json;
using IndependentDeclarations.Wire;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Shared declaration consumer enabled reflection fallback.");
var host = new Uri(args.Single());
using var http = new HttpClient { BaseAddress = host, Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.Authorization = new("consumer-policy");
using var client = new SharedClient(http, new Uri(host, "/remote"));
using var lifetime = new CancellationTokenSource();
var request = new SharedData
{
    Name = "remote",
    Note = null,
    Count = 29,
    Child = new SharedData { Name = "nested", Count = 17 }
};
var response = await client.EchoAsync(request, cancellationToken: lifetime.Token);
Check(response.Name == "remote" && response.Note is null && response.Count == 32 &&
    response.Child is { Name: "nested", Count: 17 },
    "saved Remote model maps serializer names, recursion, omitted nullable optional and constant default over HTTP");
var changed = await client.EchoAsync(request, adjustment: -2, replacementCount: 101, cancellationToken: lifetime.Token);
Check(changed.Count == 99, "explicit optional values reach the real author method");

using (var body = new StringContent(
    """{"args":{"value":{"display_name":"invalid"},"replacementCount":null}}""", Encoding.UTF8, "application/json"))
using (var rejected = await http.PostAsync("/remote/sharedData/echo", body))
{
    rejected.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
    Check(!document.RootElement.GetProperty("ok").GetBoolean() &&
        document.RootElement.GetProperty("error").GetProperty("code").GetString() == "gateway/input-invalid",
        "nullable optional CLR parameter distinguishes omission from an explicit wire null");
}
using (var stopped = await http.PostAsync("/control/stop", null))
    stopped.EnsureSuccessStatusCode();
Console.WriteLine("saved Remote declaration HTTP consumer passed");

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}
