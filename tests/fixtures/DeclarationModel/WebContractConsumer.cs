using Cordis.Composition;
using IndependentWeb.Caller;

using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri(new Uri(args[0]), "/remote"));
await using var client = new WebContractClient(connection);
var omitted = await client.RevisionAsync();
var supplied = await client.RevisionAsync(3);
if (!omitted.Ok || omitted.Value != "omitted" || !supplied.Ok || supplied.Value != "supplied")
    throw new Exception("Generated .NET caller lost revision omission or value.");
var result = await client.DirectionAsync(
    new DirectionalData
    {
        Count = 0,
        GlobalCount = 0,
        Value = "input",
        OutputNullable = "input",
        InputNullable = null
    });
if (!result.Ok)
    throw result.Error;
if (result.Value.Count != 0 || result.Value.GlobalCount != 0 || result.Value.Value is not null ||
    result.Value.OutputNullable is not null || result.Value.Always != 0 || result.Value.InputNullable != "initial")
    throw new Exception("Generated .NET caller did not preserve the directional result.");
Console.WriteLine("PASS directional .NET package caller");
