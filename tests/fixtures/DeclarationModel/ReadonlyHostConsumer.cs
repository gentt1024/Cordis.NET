using System.Text;
using System.Text.Json;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Read-only Host consumer enabled reflection fallback.");
var host = new Uri(args.Single());
using var http = new HttpClient
{
    BaseAddress = host,
    Timeout = TimeSpan.FromSeconds(20)
};
http.DefaultRequestHeaders.Authorization = new("consumer-policy");
using var body = new StringContent(
    """{"type":"client-request","rpcId":"readonly-check","method":"readonly/value","payload":{"args":{}}}""",
    Encoding.UTF8,
    "application/json");
using var response = await http.PostAsync("/remote/readonly/value", body);
response.EnsureSuccessStatusCode();
using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
if (!result.RootElement.GetProperty("result").GetProperty("ok").GetBoolean() ||
    result.RootElement.GetProperty("result").GetProperty("value").GetProperty("applies").GetString() != "live")
    throw new Exception("Generated Host binding did not call the authored getter and STJ metadata.");
using (var stopped = await http.PostAsync("/control/stop", null))
    stopped.EnsureSuccessStatusCode();
Console.WriteLine("read-only Host succeeds while standalone client projection refuses");
