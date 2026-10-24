using TypertConnection = Cordis.Composition.TypertConnection;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Caller.Edges;

if (JsonSerializer.IsReflectionEnabledByDefault) throw new Exception("Reflection fallback enabled.");
using var http = new HttpClient(new Replies());
await using var connection = new TypertConnection(http, new Uri("https://test.invalid/remote"));
await using var client = new PrimitivesClient(connection);
if ((await client.CountAsync(null, 0, false, null, 0)).Value != 0) throw new Exception("Primitive result wrong.");
if ((await client.TextAsync()).Value is not null) throw new Exception("Nullable string result wrong.");
if (!((await client.ValuesAsync()).Value).SequenceEqual(new[] { 1, 2 }))
    throw new Exception("Collection result wrong.");
var emptyImmutable = (await client.ImmutableAsync(new ImmutablePayload())).Value;
if (!emptyImmutable.Items.IsEmpty || emptyImmutable.Items.IsDefault ||
    !emptyImmutable.Labels.IsEmpty || emptyImmutable.Labels.IsDefault ||
    !emptyImmutable.StrictLabels.IsEmpty || emptyImmutable.OptionalItems is not null)
    throw new Exception("Empty immutable collections changed in the generated call.");
var immutable = (await client.ImmutableAsync(new ImmutablePayload
{
    Items = [new ImmutableItem("input", [1, 2]), null],
    Labels = ["input", null],
    StrictLabels = ["input"],
    OptionalItems = [null]
})).Value;
if (immutable.Items.Length != 2 || immutable.Items[0] is not { Name: null } nested ||
    !nested.Values.SequenceEqual(new[] { 3, 4 }) || immutable.Items[1] is not null ||
    !immutable.Labels.SequenceEqual(new string?[] { "result", null }) ||
    !immutable.StrictLabels.SequenceEqual(new[] { "result" }) ||
    immutable.OptionalItems is not { Length: 1 } optionalItems || optionalItems[0] is not null)
    throw new Exception("Immutable collection elements or nested DTO results changed.");
Console.WriteLine("GREEN immutable arrays: empty collections, nullable elements and nested DTO calls");

var defaultCodec = PrimitivesClient.Info.Descriptors.Single(value => value.Method == "passiveDefault").Parameters[0].Codec;
using var missingDefault = JsonDocument.Parse("{\"Id\":7}");
var defaultSnapshot = (PassiveSnapshot)defaultCodec.Decode(missingDefault.RootElement)!;
if (defaultSnapshot.Bytes is not { Length: 0 } || defaultSnapshot.Names is not { Count: 0 } ||
    defaultSnapshot.Policy is not { Enabled: false, Overrides.IsEmpty: true, Overrides.IsDefault: false, Detail: null })
    throw new Exception("Independent input codec lost the static member default.");
using var providedDefault = JsonDocument.Parse("{\"Id\":8,\"Policy\":{\"Enabled\":true,\"Overrides\":[\"keep\"]}}");
var providedSnapshot = (PassiveSnapshot)defaultCodec.Decode(providedDefault.RootElement)!;
if (!providedSnapshot.Policy.Enabled || !providedSnapshot.Policy.Overrides.SequenceEqual(new[] { "keep" }))
    throw new Exception("An explicit input value was replaced by a default.");
using var nullDefault = JsonDocument.Parse("{\"Id\":9,\"Policy\":null}");
try
{
    defaultCodec.Decode(nullDefault.RootElement);
    throw new Exception("Explicit null was defaulted.");
}
catch (JsonException) { }
var optionalOutput = (PassiveSnapshot)defaultCodec.DecodeOutput(missingDefault.RootElement)!;
if (optionalOutput.Policy is not null)
    throw new Exception("An input default leaked into output decoding.");
if ((await client.PassiveDefaultAsync(defaultSnapshot)).Value.Policy is not { Enabled: true, Overrides.Length: 1 })
    throw new Exception("Static default carrier did not make the generated client call.");
Console.WriteLine("GREEN independent input defaults: omission, explicit value, null refusal and output isolation");

foreach (var invalidVariant in new[] { 1, 2 })
{
    using var invalidCollectionHttp = new HttpClient(new Replies(invalidCollectionOutput: invalidVariant));
    await using var invalidCollectionConnection = new TypertConnection(invalidCollectionHttp, new Uri("https://test.invalid/remote"));
    await using var invalidCollectionClient = new PrimitivesClient(invalidCollectionConnection);
    var invalidCollection = await invalidCollectionClient.ImmutableAsync(new ImmutablePayload());
    if (invalidCollection.Ok || invalidCollection.Error.InnerException is not JsonException)
        throw new Exception("A null non-nullable immutable collection element was accepted, variant " + invalidVariant);
}
var timestamp = DateTimeOffset.Parse("2026-10-24T08:09:10.7654321-02:00", System.Globalization.CultureInfo.InvariantCulture);
var timestampResult = (await client.TimestampAsync(timestamp)).Value;
if (timestampResult?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) != "2026-10-24T12:34:56.1234567+05:30" ||
    (await client.TimestampAsync(null)).Value is not null)
    throw new Exception("DateTimeOffset precision, offset or null changed.");
foreach (var mode in new[] { WireMode.Read, WireMode.Read | WireMode.Write, WireMode.Special, (WireMode)17 })
    if ((await client.ModeAsync(mode)).Value != mode)
        throw new Exception("String enum names, flags, member names or integer fallback changed.");
foreach (var mode in new[] { PlainMode.Known, (PlainMode)7 })
{
    var result = await client.PlainModeAsync(mode);
    if (!result.Ok || result.Value != mode)
        throw new Exception("Plain string enum casing, alias or integer fallback changed: " +
            (result.Ok ? "unexpected value" : result.Error.Message));
}
using var invalidEnumHttp = new HttpClient(new Replies(invalidEnumOutput: true));
await using var invalidEnumConnection = new TypertConnection(invalidEnumHttp, new Uri("https://test.invalid/remote"));
await using var invalidEnumClient = new PrimitivesClient(invalidEnumConnection);
var invalidEnum = await invalidEnumClient.ModeAsync(WireMode.Read);
if (invalidEnum.Ok || invalidEnum.Error.InnerException is not JsonException)
    throw new Exception("An unknown string enum name was accepted.");
Console.WriteLine("GREEN DateTimeOffset and string enum calls; invalid enum and null elements refused");
await using var readonlyClient = new ReadonlyClient(connection);
if ((await readonlyClient.ValueAsync()).Value.Applies != "received")
    throw new Exception("Readonly output was recomputed instead of received from the wire.");
await using var structClient = new RecordstructClient(connection);
var valueRecord = (await structClient.ValueAsync()).Value;
var changedRecord = valueRecord with { Count = 19 };
if (!typeof(ValueRecord).IsValueType || valueRecord.Count != 7 || changedRecord.Count != 19)
    throw new Exception("Record struct value semantics changed.");
var genericInt = (await client.GenericIntAsync(new WireEnvelope<int>(3))).Value;
if (genericInt.Value != 7 || genericInt.Accepted)
    throw new Exception("Closed value parameter or received readonly generic field changed.");
if ((await client.GenericTextAsync(new WireEnvelope<string>(null))).Value.Value is not null ||
    (await client.GenericNullableAsync(new WireEnvelope<int?>(null))).Value.Value is not null)
    throw new Exception("Generic reference/value argument nullability changed.");
var genericArray = (await client.GenericArrayAsync(new WireEnvelope<System.Collections.Immutable.ImmutableArray<string?>>([null, "request"]))).Value;
if (genericArray.Value.Length != 2 || genericArray.Value[0] is not null || genericArray.Value[1] != "wire")
    throw new Exception("Nested generic collection element nullability changed.");
using var invalidGenericHttp = new HttpClient(new Replies(invalidGenericOutput: true));
await using var invalidGenericConnection = new TypertConnection(invalidGenericHttp, new Uri("https://test.invalid/remote"));
await using var invalidGenericClient = new PrimitivesClient(invalidGenericConnection);
var invalidGeneric = await invalidGenericClient.GenericIntAsync(new WireEnvelope<int>(3));
if (invalidGeneric.Ok || invalidGeneric.Error.InnerException is not JsonException)
    throw new Exception("Unconstrained T? incorrectly made a closed int nullable.");
Console.WriteLine("GREEN record struct, closed generic substitution and readonly wire consumption");
if ((await client.SeedAsync()).Value.Text != "from-wire")
    throw new Exception("An output-only carrier replayed its authored default instead of receiving data.");
await using var optional = new OptionalClient(connection);
if ((await optional.CountAsync()).Value != 7 || (await optional.CountAsync(3)).Value != 3)
    throw new Exception("Optional constants changed in the generated client.");
foreach (var type in new[] { typeof(int), typeof(string), typeof(int[]) })
    if (PrimitivesClientJson.Default.GetTypeInfo(type) is null)
        throw new Exception("Primitive public root metadata absent.");
var integer = (JsonTypeInfo<int>)PrimitivesClientJson.Default.GetTypeInfo(typeof(int))!;
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(17, integer), integer) != 17)
    throw new Exception("Primitive metadata roundtrip failed.");
var metadata = (JsonTypeInfo<NullableValue>)NullableClientJson.Default.GetTypeInfo(typeof(NullableValue))!;
try
{
    JsonSerializer.Deserialize("{\"text\":null}", metadata);
    throw new Exception("DisallowNull ignored.");
}
catch (JsonException)
{
}

try
{
    JsonSerializer.Deserialize("{\"text\":\"yes\",\"strict\":null}", metadata);
    throw new Exception("Property DisallowNull ignored.");
}
catch (JsonException)
{
}

if (JsonSerializer.Deserialize("{\"text\":\"yes\"}", metadata)!.Text != "yes")
    throw new Exception("Nullable DTO metadata decode failed.");
var numericMetadata =
    (JsonTypeInfo<NumericDefaults>)NumericDefaultsClientJson.Default.GetTypeInfo(typeof(NumericDefaults))!;
var initialized = JsonSerializer.Deserialize("{}", numericMetadata)!;
if (initialized.Ratio != 1.25f || initialized.Amount != 0.1m ||
    initialized.EmptyRatio is not null || initialized.EmptyAmount is not null)
    throw new Exception("Absent numeric input fields did not retain nullable initializers.");
await using var numeric = new NumericDefaultsClient(connection);
var defaults = (await numeric.ValueAsync(new NumericDefaults())).Value;
if (defaults.Ratio != 1.25f || defaults.Amount != 0.1m ||
    defaults.EmptyRatio is not null || defaults.EmptyAmount is not null)
    throw new Exception("Numeric defaults changed in the generated call.");
var cleared = (await numeric.ValueAsync(
    new NumericDefaults
    {
        Ratio = null,
        Amount = null
    })).Value;
if (cleared.Ratio is not null || cleared.Amount is not null ||
    cleared.EmptyRatio is not null || cleared.EmptyAmount is not null)
    throw new Exception("Explicit null numeric fields did not override initializers.");
var numbers = (await numeric.ValueAsync(
    new NumericDefaults
    {
        Ratio = 2.5f,
        Amount = 12.75m,
        EmptyRatio = 3.5f,
        EmptyAmount = 4.25m
    })).Value;
if (numbers.Ratio != 2.5f || numbers.Amount != 12.75m ||
    numbers.EmptyRatio != 3.5f || numbers.EmptyAmount != 4.25m)
    throw new Exception("Explicit nullable numeric values changed in the generated client.");
using var invalidHttp = new HttpClient(new Replies(incompleteOutput: true));
await using var invalidConnection = new TypertConnection(invalidHttp, new Uri("https://test.invalid/remote"));
await using var invalidClient = new NumericDefaultsClient(invalidConnection);
var invalidOutput = await invalidClient.ValueAsync(new NumericDefaults());
if (invalidOutput.Ok || invalidOutput.Error.InnerException is not JsonException)
    throw new Exception("A result missing required output fields was accepted.");
await using var invalidReadonlyClient = new ReadonlyClient(invalidConnection);
var missingReadonly = await invalidReadonlyClient.ValueAsync();
if (missingReadonly.Ok || missingReadonly.Error.InnerException is not JsonException)
    throw new Exception("A result missing its required readonly output was accepted.");
Console.WriteLine(
    "GREEN primitive/string/collection roots; null/default named arguments and legal colliding names; DisallowNull decode; nullable numeric absent/null/explicit values; readonly wire values; changed nullable policy changes identity; stale output removed on SDK/options failure.");

sealed class Replies(bool incompleteOutput = false, int invalidCollectionOutput = 0, bool invalidEnumOutput = false, bool invalidGenericOutput = false) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        using var document = JsonDocument.Parse(body);
        var args = document.RootElement.GetProperty("payload").GetProperty("args");
        var value = request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "count" when request.RequestUri!.AbsolutePath.Contains("/optional/", StringComparison.Ordinal) =>
                args.GetProperty("limit").GetRawText(),
            "count" => Check(args),
            "text" => "null",
            "values" => "[1,2]",
            "immutable" => CheckImmutable(args.GetProperty("input"), invalidCollectionOutput),
            "timestamp" => CheckTimestamp(args.GetProperty("input")),
            "mode" => CheckMode(args.GetProperty("input"), invalidEnumOutput),
            "plainMode" => args.GetProperty("input").GetRawText() switch
            {
                "\"Known\"" => "\"known\"",
                "7" => "\"7\"",
                _ => throw new Exception("Plain string enum request changed.")
            },
            "genericInt" => CheckGenericInt(args.GetProperty("input"), invalidGenericOutput),
            "seed" => "{\"Text\":\"from-wire\"}",
            "passiveDefault" => CheckPassiveDefault(args.GetProperty("input")),
            "genericText" or "genericNullable" => CheckGenericNull(args.GetProperty("input")),
            "genericArray" => CheckGenericArray(args.GetProperty("input")),
            "value" when request.RequestUri.AbsolutePath.Contains("/readonly/", StringComparison.Ordinal) =>
                incompleteOutput ? "{}" : "{\"applies\":\"received\"}",
            "value" when request.RequestUri.AbsolutePath.Contains("/recordStruct/", StringComparison.Ordinal) =>
                "{\"Count\":7}",
            "value" => incompleteOutput ? "{}" : CheckNumeric(args.GetProperty("input")),
            _ => throw new Exception("Unknown request.")
        };
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"type\":\"server-response\",\"rpcId\":" + document.RootElement.GetProperty("rpcId").GetRawText() +
                ",\"result\":{\"ok\":true,\"value\":" + value + "}}",
                Encoding.UTF8,
                "application/json")
        };
    }

    private static string CheckPassiveDefault(JsonElement input)
    {
        var policy = input.GetProperty("Policy");
        if (policy.GetProperty("Enabled").GetBoolean() || policy.GetProperty("Overrides").GetArrayLength() != 0)
            throw new Exception("Defaulted input was not sent by the generated client.");
        return "{\"Id\":7,\"Policy\":{\"Enabled\":true,\"Overrides\":[\"result\"]}}";
    }

    private static string CheckGenericInt(JsonElement input, bool invalid)
    {
        if (input.GetProperty("Value").GetInt32() != 3)
            throw new Exception("Closed generic integer request changed.");
        return invalid ? "{\"Value\":null,\"Accepted\":false}" : "{\"Value\":7,\"Accepted\":false}";
    }

    private static string CheckGenericNull(JsonElement input)
    {
        if (input.GetProperty("Value").ValueKind != JsonValueKind.Null)
            throw new Exception("Closed generic null request changed.");
        return "{\"Value\":null,\"Accepted\":true}";
    }

    private static string CheckGenericArray(JsonElement input)
    {
        var values = input.GetProperty("Value");
        if (values.GetArrayLength() != 2 || values[0].ValueKind != JsonValueKind.Null || values[1].GetString() != "request")
            throw new Exception("Nested generic array request changed.");
        return "{\"Value\":[null,\"wire\"],\"Accepted\":false}";
    }

    private static string Check(JsonElement args)
    {
        if (args.EnumerateObject().Count() != 5 || args.GetProperty("request").ValueKind != JsonValueKind.Null ||
            args.GetProperty("cancellationToken").GetInt32() != 0 || args.GetProperty("httpClient").GetBoolean() ||
            args.GetProperty("__cordisRequest").ValueKind != JsonValueKind.Null ||
            args.GetProperty("_").GetInt32() != 0)
            throw new Exception("Null/default args omitted or changed.");
        return "0";
    }

    private static string CheckImmutable(JsonElement input, int invalidOutput)
    {
        var items = input.GetProperty("Items");
        var labels = input.GetProperty("Labels");
        var strictLabels = input.GetProperty("StrictLabels");
        var optionalItems = input.GetProperty("OptionalItems");
        if (items.GetArrayLength() == 0)
        {
            if (labels.GetArrayLength() != 0 || strictLabels.GetArrayLength() != 0 ||
                optionalItems.ValueKind != JsonValueKind.Null)
                throw new Exception("Empty immutable request collections changed.");
            return invalidOutput switch
            {
                1 => "{\"Items\":[],\"Labels\":[],\"StrictLabels\":[null]}",
                2 => "{\"Items\":[],\"Labels\":[],\"StrictLabels\":[],\"OptionalStrictLabels\":[null]}",
                _ => "{\"Items\":[],\"Labels\":[],\"StrictLabels\":[]}"
            };
        }
        if (items.GetArrayLength() != 2 || items[0].GetProperty("Name").GetString() != "input" ||
            !items[0].GetProperty("Values").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(new[] { 1, 2 }) ||
            items[1].ValueKind != JsonValueKind.Null || labels.GetArrayLength() != 2 ||
            labels[0].GetString() != "input" || labels[1].ValueKind != JsonValueKind.Null ||
            strictLabels.GetArrayLength() != 1 || strictLabels[0].GetString() != "input" ||
            optionalItems.GetArrayLength() != 1 || optionalItems[0].ValueKind != JsonValueKind.Null)
            throw new Exception("Immutable request elements or nested DTO values changed.");
        return "{\"Items\":[{\"Name\":null,\"Values\":[3,4]},null],\"Labels\":[\"result\",null],\"StrictLabels\":[\"result\"],\"OptionalItems\":[null]}";
    }

    private static string CheckTimestamp(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Null)
            return "null";
        if (input.GetString() != "2026-10-24T08:09:10.7654321-02:00")
            throw new Exception("DateTimeOffset request offset or precision changed.");
        return "\"2026-10-24T12:34:56.1234567+05:30\"";
    }

    private static string CheckMode(JsonElement input, bool invalidOutput)
    {
        if (invalidOutput)
            return "\"Unknown\"";
        if (input.ValueKind == JsonValueKind.Number && input.GetInt32() == 17)
            return "\"17\"";
        return input.GetString() switch
        {
            "Read" => "\"read\"",
            "Read, Write" => "\"Read, Write\"",
            "wire-special" => "\"wire-special\"",
            _ => throw new Exception("String enum request changed.")
        };
    }

    private static string CheckNumeric(JsonElement input)
    {
        if (input.EnumerateObject().Count() != 4)
            throw new Exception("Nullable numeric request fields omitted.");
        var ratio = input.GetProperty("Ratio");
        var amount = input.GetProperty("Amount");
        var emptyRatio = input.GetProperty("EmptyRatio");
        var emptyAmount = input.GetProperty("EmptyAmount");
        if (ratio.ValueKind == JsonValueKind.Null && amount.ValueKind == JsonValueKind.Null &&
            emptyRatio.ValueKind == JsonValueKind.Null && emptyAmount.ValueKind == JsonValueKind.Null)
            return "{\"Ratio\":null,\"Amount\":null,\"EmptyRatio\":null,\"EmptyAmount\":null}";
        if (ratio.GetSingle() == 1.25f && amount.GetDecimal() == 0.1m &&
            emptyRatio.ValueKind == JsonValueKind.Null && emptyAmount.ValueKind == JsonValueKind.Null)
            return "{\"Ratio\":1.25,\"Amount\":0.1,\"EmptyRatio\":null,\"EmptyAmount\":null}";
        if (ratio.GetSingle() == 2.5f && amount.GetDecimal() == 12.75m &&
            emptyRatio.GetSingle() == 3.5f && emptyAmount.GetDecimal() == 4.25m)
            return "{\"Ratio\":2.5,\"Amount\":12.75,\"EmptyRatio\":3.5,\"EmptyAmount\":4.25}";
        throw new Exception("Nullable numeric request values changed.");
    }
}
