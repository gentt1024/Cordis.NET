using Cordis.Composition;
using IndependentWeb.Caller;
using System.Text.Json;

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
using var explicitNull = JsonDocument.Parse("null");
var withNull = await client.DirectionAsync(
    new DirectionalData
    {
        Count = 0,
        GlobalCount = 0,
        Value = "input",
        OutputNullable = "input",
        OptionalJson = explicitNull.RootElement
    });
if (!withNull.Ok)
    throw withNull.Error;
if (withNull.Value.Count != 1)
    throw new Exception("Explicit JSON null was collapsed into field omission.");
var enums = await client.EnumsAsync(
    new EnumData
    {
        Status = (NumericStatus)37,
        History = [NumericStatus.Alias, (NumericStatus)(-7)],
        Flags = NumericFlags.Read | NumericFlags.Write
    });
if (!enums.Ok || enums.Value.Status != (NumericStatus)37 || enums.Value.Optional != NumericStatus.Unavailable ||
    enums.Value.History[0] != NumericStatus.Succeeded || enums.Value.History[1] != (NumericStatus)(-7) ||
    enums.Value.Flags != (NumericFlags)3 || enums.Value.Always != 0 ||
    new EnumData().Status != NumericStatus.Unavailable ||
    Enum.GetUnderlyingType(typeof(NumericFlags)) != typeof(byte) ||
    !typeof(NumericFlags).IsDefined(typeof(FlagsAttribute), false))
    throw new Exception("Generated enum declarations or numeric wire values changed.");
Console.WriteLine("PASS directional .NET package caller");
var defaults = await client.NullableEnumsAsync();
var explicitEnumNull = await client.NullableEnumsAsync(null, null);
var unnamed = await client.NullableEnumsAsync((NumericStatus)(-7), (NumericStatus)37);
if (!defaults.Ok || !defaults.Value.SequenceEqual(
        new NumericStatus?[] { NumericStatus.Unavailable, NumericStatus.Succeeded, null, null }) ||
    !explicitEnumNull.Ok || explicitEnumNull.Value.Any(value => value is not null) ||
    !unnamed.Ok ||
    !unnamed.Value.SequenceEqual(new NumericStatus?[] { (NumericStatus)(-7), (NumericStatus)37, null, null }) ||
    new EnumData().Previous != NumericStatus.Succeeded || new EnumData().Missing != NumericStatus.Unavailable ||
    new EnumData().Absent is not null || new EnumData().Empty is not null)
    throw new Exception("Nullable enum defaults, null or generated type identity changed.");
