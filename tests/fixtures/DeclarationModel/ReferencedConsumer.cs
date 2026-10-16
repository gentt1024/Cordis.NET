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
Check(value.External.Status == NumericStatus.Unavailable, "referenced enum initializer retains its generated type");
Check(value.External.Previous == NumericStatus.Succeeded, "referenced nullable enum retains its non-null initializer");
var nullPrevious = JsonSerializer.Deserialize(
    """{"External":{"external_name":"imported","Code":null,"Previous":null}}""",
    metadata)!;
Check(nullPrevious.External.Previous is null, "explicit null overrides the nullable enum initializer");
value.External.Status = (NumericStatus)37;
Check(
    JsonSerializer.Deserialize(JsonSerializer.Serialize(value, metadata), metadata)!.External.Status ==
    (NumericStatus)37,
    "ordinary data enum roundtrip retains unnamed numeric values");
Check(value.Identifier == Guid.Empty, "omitted framework scalar retains its CLR default");
var identifier = Guid.Parse("f031a626-7695-4b15-a915-15aa56e0cf94");
value.Identifier = identifier;
Check(
    JsonSerializer.Deserialize(JsonSerializer.Serialize(value, metadata), metadata)!.Identifier == identifier,
    "independent data carrier retains the Guid type and value");
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
     "Items":[{"external_name":"list","Code":null}],
     "ByName":{"first":{"external_name":"mapped","Code":null},"empty":null},
     "Groups":{"batch":[{"external_name":"grouped","Code":null}]},
     "Labels":{"present":"yes","empty":null},
     "Sequence":[null,{"external_name":"sequence","Code":null}],
     "Collection":[null,{"external_name":"collection","Code":null}],
     "ReadOnlyCollection":[null,{"external_name":"readonly","Code":null}],
     "MutableList":[null,{"external_name":"mutable","Code":null}]}
    """,
    metadata)!;
Check(
    complete.External.Next?.Name == "second" && complete.Items?.Single().Name == "list",
    "recursive imported objects and list elements");
Check(
    complete.ByName?["first"]?.Name == "mapped" && complete.ByName.ContainsKey("empty") &&
    complete.ByName["empty"] is null && complete.Groups?["batch"].Single().Name == "grouped" &&
    complete.Labels?["present"] == "yes" && complete.Labels["empty"] is null,
    "string dictionary keys, imported values, nested arrays and explicit null");
var json = JsonSerializer.Serialize(complete, metadata);
foreach (var (items, name) in new (IEnumerable<ImportedData?>?, string)[]
         {
             (complete.Sequence, "sequence"),
             (complete.Collection, "collection"),
             (complete.ReadOnlyCollection, "readonly"),
             (complete.MutableList, "mutable")
         })
    Check(
        items is not null && items.First() is null && items.Last()?.Name == name &&
        items.Last()?.Count == expectedDefault,
        "collection interface preserves referenced defaults and nullable elements");
using var document = JsonDocument.Parse(json);
Check(
    document.RootElement.GetProperty("External").GetProperty("external_name").GetString() == "first",
    "imported serialized JSON name");
Check(
    JsonSerializer.Deserialize(json, metadata)!.External.Next?.Name == "second",
    "cross-model static metadata round trip");
Check(
    document.RootElement.GetProperty("ByName").GetProperty("empty").ValueKind == JsonValueKind.Null &&
    document.RootElement.GetProperty("Groups").GetProperty("batch")[0].GetProperty("external_name").GetString() ==
    "grouped",
    "dictionary keys and values survive static serialization");
foreach (var name in new[] { "Sequence", "Collection", "ReadOnlyCollection", "MutableList" })
    Check(
        document.RootElement.GetProperty(name)[0].ValueKind == JsonValueKind.Null &&
        document.RootElement.GetProperty(name).GetArrayLength() == 2,
        "collection interface survives static serialization");
Check(
    ReferencedDataJson.Default.GetTypeInfo(typeof(ImportedData)) is not null,
    "reachable imported type has static metadata");
Check(
    Type.GetType("IndependentDeclarations.Referenced.UnrelatedData, ReferencedDataContracts") is null,
    "referenced model roots do not expand the main projection");
Console.WriteLine("referenced ordinary data carrier consumer passed: " + expectedDefault);

static void Check(bool condition, string contract)
{
    if (!condition)
        throw new Exception("Referenced ordinary data contract failed: " + contract);
}
