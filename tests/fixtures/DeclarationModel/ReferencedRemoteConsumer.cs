using System.Text.Json;
using Cordis.Composition;
using IndependentDeclarations.RemoteReference;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Referenced Remote caller enabled reflection fallback.");
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri(new Uri(args[0]), "/remote"));
await using var client = new ReferencedClient(connection);
var expected = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var value = new ReferencedData
{
    ByName = new()
    {
        ["empty"] = null,
        ["reference"] = new ImportedData
        {
            Name = "dictionary",
            Code = null
        }
    },
    External = new ImportedData
    {
        Name = "referenced",
        Code = null,
        Note = null
    },
    Items =
    [
        new ImportedData
        {
            Name = "item",
            Code = "code"
        }
    ]
};
var result = await client.EchoAsync(value);
if (!result.Ok) throw result.Error;
if (result.Value.External.Count != expected || result.Value.External.Name != "referenced" ||
    result.Value.External.Code is not null || result.Value.External.Note is not null ||
    result.Value.Items?.Single().Note != "external-default" ||
    result.Value.ByName?["reference"]?.Name != "dictionary" || !result.Value.ByName.ContainsKey("empty") ||
    result.Value.ByName["empty"] is not null)
    throw new Exception("Referenced declaration facts did not survive the generated HTTP call.");
Console.WriteLine("referenced Remote package caller passed: " + expected);
