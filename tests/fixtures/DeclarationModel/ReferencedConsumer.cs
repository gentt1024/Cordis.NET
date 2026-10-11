using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IndependentDeclarations.Referenced;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Referenced data consumer enabled reflection fallback.");
var expectedDefault = int.Parse(args.Single(), System.Globalization.CultureInfo.InvariantCulture);
var metadata = (JsonTypeInfo<ReferencedData>)ReferencedDataJson.Default.GetTypeInfo(typeof(ReferencedData))!;
var value = JsonSerializer.Deserialize("""{"External":{"external_name":"imported","Code":null}}""", metadata)!;
Check(
    value.External.Name == "imported" && value.External.Code is null,
    "imported JSON name and required nullable member");
Check(
    value.External.Count == expectedDefault && value.External.Note == "external-default",
    "source-only imported declaration defaults");
var explicitNull = JsonSerializer.Deserialize(
    """{"External":{"external_name":"imported","Code":null,"Note":null}}""",
    metadata)!;
Check(explicitNull.External.Note is null, "imported explicit null differs from omitted default");
var invalidInputs = new[]
{
    "{}",
    """{"External":null}""",
    """{"External":{"Code":null}}""",
    """{"External":{"external_name":"imported"}}""",
    """{"External":{"external_name":null,"Code":null}}""",
    """{"External":{"external_name":"imported","Code":null,"Next":{}}}"""
};
foreach (var invalid in invalidInputs)
{
    try
    {
        JsonSerializer.Deserialize(invalid, metadata);
        throw new Exception("Referenced data accepted missing required or invalid null: " + invalid);
    }
    catch (JsonException)
    {
    }
}

var complete = JsonSerializer.Deserialize(
    """
    {"External":{"external_name":"first","Code":null,"Next":{"external_name":"second","Code":"code"}},
     "Items":[{"external_name":"list","Code":null}]}
    """,
    metadata)!;
Check(
    complete.External.Next?.Name == "second" && complete.Items?.Single().Name == "list",
    "recursive imported objects and list elements");
var json = JsonSerializer.Serialize(complete, metadata);
using var document = JsonDocument.Parse(json);
Check(
    document.RootElement.GetProperty("External").GetProperty("external_name").GetString() == "first",
    "imported serialized JSON name");
Check(
    JsonSerializer.Deserialize(json, metadata)!.External.Next?.Name == "second",
    "cross-model static metadata round trip");
Check(
    ReferencedDataJson.Default.GetTypeInfo(typeof(ImportedData)) is not null,
    "reachable imported type has static metadata");
Check(
    typeof(ReferencedData).Assembly.GetType("IndependentDeclarations.Referenced.UnrelatedData") is null,
    "referenced model roots do not expand the main projection");
Console.WriteLine("referenced ordinary data carrier consumer passed: " + expectedDefault);

static void Check(bool condition, string contract)
{
    if (!condition)
        throw new Exception("Referenced ordinary data contract failed: " + contract);
}
