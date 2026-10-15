using System.Text.Json;
using Cordis.Composition;
using IndependentDeclarations.RemoteReference;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Referenced Remote caller enabled reflection fallback.");
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri(new Uri(args[0]), "/remote"));
await using var client = new ReferencedClient(connection);
foreach (var identifier in new[] { Guid.Empty, Guid.Parse("f031a626-7695-4b15-a915-15aa56e0cf94") })
{
    var scalar = await client.GuidAsync(identifier);
    if (!scalar.Ok || scalar.Value != identifier)
        throw new Exception("Generated Guid caller changed the root value.");
    var nullableScalar = await client.NullableGuidAsync(identifier);
    if (!nullableScalar.Ok || nullableScalar.Value != identifier)
        throw new Exception("Generated nullable Guid caller changed a present value.");
    var item = await client.ItemAsync(
        new GuidItem(identifier)
        {
            Optional = identifier,
            Array = [Guid.Empty, identifier],
            Values = [null, identifier],
            ByName = new()
            {
                ["id"] = identifier
            }
        });
    if (!item.Ok || item.Value.Id != identifier || item.Value.Optional != identifier ||
        item.Value.Array?[1] != identifier || item.Value.Values?[0] is not null ||
        item.Value.Values?[1] != identifier || item.Value.ByName?["id"] != identifier)
        throw new Exception("Generated Guid DTO/collection caller changed the value.");
}

var defaultGuid = await client.GuidAsync();
var nullableGuid = await client.NullableGuidAsync();
if (!defaultGuid.Ok || defaultGuid.Value != Guid.Empty || !nullableGuid.Ok || nullableGuid.Value is not null)
    throw new Exception("Generated Guid default and nullable default were conflated.");
var expected = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var value = new ReferencedData
{
    Sequence =
    [
        null, new ImportedData
        {
            Name = "sequence",
            Code = null
        }
    ],
    Collection =
    [
        null, new ImportedData
        {
            Name = "collection",
            Code = null
        }
    ],
    ReadOnlyCollection =
    [
        null, new ImportedData
        {
            Name = "readonly",
            Code = null
        }
    ],
    MutableList =
    [
        null, new ImportedData
        {
            Name = "mutable",
            Code = null
        }
    ],
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
foreach (var items in new[]
         {
             result.Value.Sequence,
             result.Value.Collection,
             result.Value.ReadOnlyCollection,
             result.Value.MutableList
         })
    if (items is null || items.Count() != 2 || items.First() is not null || items.Last()?.Count != expected)
        throw new Exception("Referenced nullable collection did not survive the generated HTTP call.");
if (result.Value.External.Count != expected || result.Value.External.Name != "referenced" ||
    result.Value.External.Code is not null || result.Value.External.Note is not null ||
    result.Value.Items?.Single().Note != "external-default" ||
    result.Value.ByName?["reference"]?.Name != "dictionary" || !result.Value.ByName.ContainsKey("empty") ||
    result.Value.ByName["empty"] is not null)
    throw new Exception("Referenced declaration facts did not survive the generated HTTP call.");
value.Groups = new Dictionary<string, ImportedData[]>
{
    ["invalid"] = [null!]
};
var invalid = await client.EchoAsync(value);
if (invalid.Ok || invalid.Error.Code != "gateway/input-invalid")
    throw new Exception(
        "A nullable dictionary value weakened a separate non-null array element of the same declaration.");
using var response = await http.PostAsync(
    new Uri(new Uri(args[0]), "/remote/referencedData/echo"),
    new StringContent(
        """{"type":"client-request","rpcId":"required-null","method":"referencedData/echo","payload":{"args":{"value":{"External":null}}}}""",
        System.Text.Encoding.UTF8,
        "application/json"));
response.EnsureSuccessStatusCode();
using var required = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
var requiredResult = required.RootElement.GetProperty("result");
if (requiredResult.GetProperty("ok").GetBoolean() ||
    requiredResult.GetProperty("error").GetProperty("code").GetString() != "gateway/input-invalid")
    throw new Exception("A nullable dictionary value weakened a required non-null object of the same declaration.");
Console.WriteLine("referenced Remote package caller passed: " + expected);
