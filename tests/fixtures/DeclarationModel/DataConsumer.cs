using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IndependentDeclarations.Data;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Data consumer enabled reflection fallback.");
var metadata = (JsonTypeInfo<DataOptions>)OrdinaryDataJson.Default.GetTypeInfo(typeof(DataOptions))!;
var authoredCarrier = new DataOptions
{
    Name = "manual",
    Code = null,
    Child = new DataChild
    {
        Label = "child"
    }
};
Check(
    authoredCarrier.Ratio == 1.25f && authoredCarrier.Computed == 0,
    "ordinary C# object initializers retain the carrier's declaration defaults");
var minimal = "{\"display_name\":\"sample\",\"Code\":null}";
var defaults = JsonSerializer.Deserialize(minimal, metadata)!;
Check(defaults.Name == "sample" && defaults.Code is null, "authored JSON name and JSON-required nullable member");
Check(
    defaults.Note == "initial" && defaults.Ratio == 1.25f && defaults.Amount == 0.1m && defaults.Empty is null,
    "omitted fields retain declaration constants, including nullable float and decimal");
Check(
    defaults.Computed == 0 && defaults.Ratio != 99,
    "data carrier does not execute the author's accessor or constructor");

var explicitNull = JsonSerializer.Deserialize(
    "{\"display_name\":\"sample\",\"Code\":null,\"Note\":null,\"Ratio\":null,\"Amount\":null}",
    metadata)!;
Check(
    explicitNull.Note is null && explicitNull.Ratio is null && explicitNull.Amount is null,
    "explicit null replaces nullable declaration defaults");

foreach (var invalid in new[]
         {
             "{\"Code\":null}",
             "{\"display_name\":\"sample\"}",
             "{\"display_name\":null,\"Code\":null}",
             "{\"display_name\":\"sample\",\"Code\":null,\"Strict\":null}",
             "{\"display_name\":\"sample\",\"Code\":null,\"unexpected\":true}",
             "{\"display_name\":\"sample\",\"Code\":null,\"Child\":{}}"
         })
{
    try
    {
        JsonSerializer.Deserialize(invalid, metadata);
        throw new Exception(
            "Data metadata accepted missing required, invalid null, unknown or incomplete nested data: " + invalid);
    }
    catch (JsonException)
    {
    }
}

var complete = JsonSerializer.Deserialize(
    """
    {"display_name":"full","Code":"code","Count":"12","Computed":8,
     "Child":{"Label":"first","Next":{"Label":"second"}},
     "Items":[{"Label":"list"}],"Counts":[3,5],"Numbers":[2,4],"Extra":{"ok":true}}
    """,
    metadata)!;
Check(complete.Count == 12 && complete.Computed == 8, "number handling and carrier setter");
Check(
    complete.Child?.Next?.Label == "second" && complete.Items?.Single().Label == "list",
    "recursive objects and read-only list interface data");
Check(
    complete.Counts!.SequenceEqual(new[] { 3, 5 }) && complete.Numbers!.SequenceEqual(new[] { 2, 4 }) &&
    complete.Extra!.Value.GetProperty("ok").GetBoolean(),
    "list, array and JSON data");
var json = JsonSerializer.Serialize(complete, metadata);
using var document = JsonDocument.Parse(json);
Check(
    document.RootElement.GetProperty("display_name").GetString() == "full" &&
    !document.RootElement.TryGetProperty("Name", out _) && !document.RootElement.TryGetProperty("Local", out _),
    "serialized naming and ignore policy");
var roundtrip = JsonSerializer.Deserialize(json, metadata)!;
Check(
    roundtrip.Child?.Next?.Label == "second" && roundtrip.Ratio == 1.25f && roundtrip.Amount == 0.1m &&
    roundtrip.Computed == 8,
    "static metadata round trip");
Check(
    OrdinaryDataJson.Default.GetTypeInfo(typeof(DataChild)) is not null,
    "reachable child has public static metadata");
Console.WriteLine("ordinary data carrier consumer passed");

static void Check(bool condition, string contract)
{
    if (!condition)
    {
        throw new Exception("Ordinary data contract failed: " + contract);
    }
}
