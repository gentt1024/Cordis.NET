using System.Text.Json;

if (args.Length != 2)
    throw new ArgumentException("Supply the packaged declaration model and its catalog.");

using var modelDocument = JsonDocument.Parse(File.ReadAllText(args[0]));
using var catalogDocument = JsonDocument.Parse(File.ReadAllText(args[1]));
var model = modelDocument.RootElement;
var catalog = catalogDocument.RootElement;
Check(model.GetProperty("formatVersion").GetInt32() == 2, "ordinary model format");
Check(!model.TryGetProperty("services", out _), "ordinary declarations remain separate from remote services");
Check(model.GetProperty("diagnostics").GetArrayLength() == 0, "ordinary declarations need no unsupported projection");
Check(
    Text(model.GetProperty("compilation"), "assemblyName") == "IndependentDeclarations.Provider",
    "authored assembly");
Check(Text(model.GetProperty("compilation"), "nullable") == "Enable", "authored nullable mode");

var identity = Text(model, "contractIdentity");
Check(identity.StartsWith("sha256:", StringComparison.Ordinal) && identity.Length == 71, "contract identity");
Check(Text(catalog, "contractIdentity") == identity, "catalog retains the packaged contract identity");
Check(catalog.GetProperty("diagnostics").GetArrayLength() == 0, "catalog diagnostics");

var declarations = model.GetProperty("declarations").EnumerateArray().ToArray();
var byName = declarations.ToDictionary(value => Text(value, "metadataName"), StringComparer.Ordinal);
var byId = declarations.ToDictionary(value => Text(value, "id"), StringComparer.Ordinal);
var rootNames = model
    .GetProperty("roots")
    .EnumerateArray()
    .Select(value => Text(byId[value.GetString()!], "metadataName"))
    .ToHashSet(StringComparer.Ordinal);
Check(
    rootNames.SetEquals(
        new[]
        {
            "IndependentDeclarations.Config",
            "IndependentDeclarations.PluginAuthor",
            "IndependentDeclarations.CatalogService",
            "IndependentDeclarations.IPage`1",
            "IndependentDeclarations.Page`1",
            "IndependentDeclarations.Page`1+Cursor",
            "IndependentDeclarations.PageExtensions"
        }),
    "explicit roots use CLR arity and nested metadata names");
Check(!byName.ContainsKey("IndependentDeclarations.Unselected"), "unreachable declarations stay outside the graph");

const string providerIdentity =
    "IndependentDeclarations.Provider, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
foreach (var declaration in declarations.Where(value => Text(value, "provenance") == "source"))
{
    Check(Text(declaration, "assembly") == providerIdentity, "source assembly provenance");
    Check(Text(declaration, "id") == providerIdentity + ":" + Text(declaration, "metadataName"), "definition identity");
}

foreach (var reference in References(model))
{
    if (Text(reference, "kind") == "named")
        Check(byId.ContainsKey(Text(reference, "name")), "named references resolve to declaration definitions");
}

var page = byName["IndependentDeclarations.Page`1"];
var pageId = Text(page, "id");
Check(Text(page, "kind") == "class", "ordinary class");
var pageParameter = page.GetProperty("typeParameters").EnumerateArray().Single();
Check(
    Text(pageParameter, "name") == "T" && pageParameter.GetProperty("ordinal").GetInt32() == 0,
    "type parameter name and ordinal");
Check(Bool(pageParameter, "referenceType") && Bool(pageParameter, "constructor"), "class and constructor constraints");

var pageInterface = byName["IndependentDeclarations.IPage`1"];
Check(Text(pageInterface, "kind") == "interface", "ordinary interface");
Check(
    Text(pageInterface.GetProperty("typeParameters").EnumerateArray().Single(), "variance") == "Out",
    "interface variance");
var implemented = page.GetProperty("interfaces").EnumerateArray().Single();
Named(implemented, pageInterface, 1);
Parameter(implemented.GetProperty("arguments")[0], "T", pageId, 0);

var item = Member(page, "Item");
Parameter(item.GetProperty("type"), "T", pageId, 0);
Check(Text(item.GetProperty("type"), "nullability") == "Annotated", "nullable type parameter use");
var required = Member(page, "Name");
Check(Bool(required, "required") && Bool(required, "initOnly"), "required init member");
Primitive(required.GetProperty("type"), "System_String", "NotAnnotated");
Check(required.GetProperty("initializer").ValueKind == JsonValueKind.Null, "required has no invented default");
var note = Member(page, "Note");
Primitive(note.GetProperty("type"), "System_String", "Annotated");
Check(
    !Bool(note, "required") && note.GetProperty("initializer").ValueKind == JsonValueKind.Null,
    "nullable does not imply a default");
Check(Text(Member(page, "Label").GetProperty("initializer"), "json") == "\"untitled\"", "constant string initializer");
Check(Text(Member(page, "Limit").GetProperty("initializer"), "json") == "7", "numeric initializer");
Attributes(Member(page, "ReadLabel"), "returnAttributes", "System.Diagnostics.CodeAnalysis.MaybeNullAttribute");
Primitive(Member(page, "ReadLabel").GetProperty("type"), "System_String", "NotAnnotated");
var accessor = Member(page, "AccessorLabel");
Attributes(accessor, "getterAttributes", "System.Diagnostics.DebuggerStepThroughAttribute");
Attributes(accessor, "setterAttributes", "System.Diagnostics.DebuggerNonUserCodeAttribute");
Attributes(accessor, "setterValueAttributes", "System.Diagnostics.CodeAnalysis.DisallowNullAttribute");

var count = Member(byName["IndependentDeclarations.PageExtensions"], "Count");
Check(
    count.GetProperty("modifiers").EnumerateArray().Select(value => value.GetString()).Contains("extension"),
    "extension method modifier");
var receiver = count.GetProperty("parameters")[0];
Check(Bool(receiver, "this") && Text(receiver, "scoped") == "None", "extension receiver facts");
Named(receiver.GetProperty("type"), page, 1);
Named(receiver.GetProperty("type").GetProperty("arguments")[0], byName["IndependentDeclarations.Config"], 0);
var scoped = count.GetProperty("parameters")[1];
Check(!Bool(scoped, "this") && Text(scoped, "scoped") == "ScopedValue", "scoped parameter facts");
Named(scoped.GetProperty("type"), byName["System.ReadOnlySpan`1"], 1);
Primitive(scoped.GetProperty("type").GetProperty("arguments")[0], "System_Byte", "NotAnnotated");

var overloads = Members(page, "Find").ToArray();
Check(overloads.Length == 2, "both ordinary overloads survive");
Check(overloads.Select(value => Text(value, "id")).Distinct().Count() == 2, "overload identities are distinct");
Check(
    overloads.Any(value =>
        Text(value, "id").EndsWith(":M:IndependentDeclarations.Page`1.Find(System.Int32)", StringComparison.Ordinal)),
    "integer overload signature identity");
var textOverload = overloads.Single(value => value.GetProperty("parameters").GetArrayLength() == 2);
Check(
    Text(textOverload, "id")
        .EndsWith(":M:IndependentDeclarations.Page`1.Find(System.String,System.Int32)", StringComparison.Ordinal),
    "string overload signature identity");
var optional = textOverload.GetProperty("parameters")[1];
Check(
    Text(optional, "name") == "limit" && Bool(optional, "optional") &&
    Text(optional.GetProperty("default"), "json") == "7",
    "ordinary parameter default");

var task = byName["System.Threading.Tasks.Task`1"];
var load = Member(page, "LoadAsync");
Named(load.GetProperty("type"), task, 1);
var taskResult = load.GetProperty("type").GetProperty("arguments")[0];
Named(taskResult, page, 1);
Parameter(taskResult.GetProperty("arguments")[0], "T", pageId, 0);
var cancellation = load.GetProperty("parameters").EnumerateArray().Single();
Check(Text(cancellation, "name") == "cancellationToken", "cancellation parameter remains in ordinary methods");
Named(cancellation.GetProperty("type"), byName["System.Threading.CancellationToken"], 0);
Check(Bool(cancellation, "optional"), "cancellation optionality");

var convert = Member(page, "Convert");
var methodParameter = convert.GetProperty("typeParameters").EnumerateArray().Single();
Check(
    Text(methodParameter, "name") == "TResult" && Bool(methodParameter, "referenceType") &&
    Bool(methodParameter, "constructor"),
    "method generic constraints");
Parameter(convert.GetProperty("type"), "TResult", Text(convert, "id"), 0);
Parameter(convert.GetProperty("parameters")[0].GetProperty("type"), "T", pageId, 0);
Named(
    methodParameter.GetProperty("constraints").EnumerateArray().Single(),
    byName["IndependentDeclarations.External.IExternalMarker"],
    0);

var envelope = byName["IndependentDeclarations.External.Envelope`1"];
Check(Text(envelope, "provenance") == "metadata-reference", "external package provenance");
Check(
    Text(envelope, "assembly") ==
    "IndependentDeclarations.External, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
    "external assembly identity");
Check(envelope.GetProperty("members").GetArrayLength() == 0, "external definitions retain reference provenance");
var export = Member(page, "Export").GetProperty("type");
Named(export, envelope, 1);
Named(export.GetProperty("arguments")[0], page, 1);
Parameter(export.GetProperty("arguments")[0].GetProperty("arguments")[0], "T", pageId, 0);

var changed = Member(page, "Changed");
Check(Text(changed, "kind") == "event", "ordinary event");
var callback = byName["IndependentDeclarations.PageChanged`1"];
Check(Text(callback, "kind") == "delegate", "source delegate declaration");
Named(changed.GetProperty("type"), callback, 1);
Check(Text(changed.GetProperty("type"), "nullability") == "Annotated", "nullable event delegate");
Parameter(changed.GetProperty("type").GetProperty("arguments")[0], "T", pageId, 0);
var invoke = callback.GetProperty("members").EnumerateArray().Single(value => Text(value, "kind") == "DelegateInvoke");
Primitive(invoke.GetProperty("type"), "System_Void", "NotAnnotated");
Check(invoke.GetProperty("parameters").GetArrayLength() == 2, "delegate event signature");
Named(invoke.GetProperty("parameters")[0].GetProperty("type"), page, 1);
Parameter(
    invoke.GetProperty("parameters")[0].GetProperty("type").GetProperty("arguments")[0],
    "T",
    Text(callback, "id"),
    0);
Primitive(invoke.GetProperty("parameters")[1].GetProperty("type"), "System_String", "Annotated");

var cursor = byName["IndependentDeclarations.Page`1+Cursor"];
Check(Text(cursor, "containingType") == pageId, "nested declaration container");
Parameter(Member(cursor, "Current").GetProperty("type"), "T", pageId, 0);
var config = byName["IndependentDeclarations.Config"];
var plugin = Member(byName["IndependentDeclarations.PluginAuthor"], "Plugin").GetProperty("type");
Named(plugin, byName["Cordis.Plugin`1"], 1);
Named(plugin.GetProperty("arguments")[0], config, 0);
var schema = Member(byName["IndependentDeclarations.PluginAuthor"], "Schema").GetProperty("type");
Named(schema, byName["Cordis.ConfigSchema`1"], 1);
Named(schema.GetProperty("arguments")[0], config, 0);
var service = byName["IndependentDeclarations.CatalogService"];
Named(service.GetProperty("baseType"), byName["Cordis.Service`1"], 1);
Named(service.GetProperty("baseType").GetProperty("arguments")[0], config, 0);
var snapshot = Member(service, "Snapshot").GetProperty("type");
Named(snapshot, page, 1);
Named(snapshot.GetProperty("arguments")[0], config, 0);
var openCursor = Member(service, "OpenCursor").GetProperty("type");
Named(openCursor, cursor, 0);
Named(openCursor.GetProperty("containingType"), page, 1);
Named(openCursor.GetProperty("containingType").GetProperty("arguments")[0], config, 0);

var entries = catalog
    .GetProperty("entries")
    .EnumerateArray()
    .ToDictionary(value => Text(value, "metadataName"), StringComparer.Ordinal);
Check(
    entries
        .Keys.ToHashSet(StringComparer.Ordinal)
        .SetEquals(
            declarations
                .Where(value => Text(value, "provenance") == "source")
                .Select(value => Text(value, "metadataName"))),
    "catalog includes authored declarations");
Check(
    !entries.ContainsKey("IndependentDeclarations.External.Envelope`1"),
    "catalog leaves external definitions as references");
Check(
    entries
        .Where(value => Bool(value.Value, "root"))
        .Select(value => value.Key)
        .ToHashSet(StringComparer.Ordinal)
        .SetEquals(rootNames),
    "catalog identifies explicit roots");
Check(Members(entries["IndependentDeclarations.Page`1"], "Find").Count() == 2, "catalog publishes both overloads");
Check(
    Text(Member(entries["IndependentDeclarations.Page`1"], "Changed"), "kind") == "event",
    "catalog publishes the event");
Check(
    Text(entries["IndependentDeclarations.Page`1"], "documentation")
        .Contains("An ordinary page with no remote serialization contract.", StringComparison.Ordinal),
    "catalog publishes source documentation");
Console.WriteLine("ordinary declaration catalog consumer passed");

static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
static bool Bool(JsonElement value, string property) => value.GetProperty(property).GetBoolean();

static IEnumerable<JsonElement> Members(JsonElement type, string name) => type
    .GetProperty("members")
    .EnumerateArray()
    .Where(value => Text(value, "name") == name);

static JsonElement Member(JsonElement type, string name) => Members(type, name).Single();

static void Attributes(JsonElement member, string property, string name) =>
    Check(
        member
            .GetProperty(property)
            .EnumerateArray()
            .Select(value => Text(value, "name"))
            .SequenceEqual(new[] { name }),
        "attribute target " + property);

static void Check(bool condition, string contract)
{
    if (!condition)
        throw new InvalidDataException("Declaration catalog contract failed: " + contract);
}

static void Named(JsonElement reference, JsonElement definition, int arguments)
{
    Check(
        Text(reference, "kind") == "named" && Text(reference, "name") == Text(definition, "id"),
        "applied reference uses a definition identity");
    Check(reference.GetProperty("arguments").GetArrayLength() == arguments, "applied reference arity");
}

static void Parameter(JsonElement reference, string name, string owner, int ordinal)
{
    Check(Text(reference, "kind") == "parameter" && Text(reference, "name") == name, "type parameter reference");
    Check(
        Text(reference, "owner") == owner && reference.GetProperty("ordinal").GetInt32() == ordinal,
        "type parameter owner and ordinal");
}

static void Primitive(JsonElement reference, string name, string nullability)
{
    Check(Text(reference, "kind") == "primitive" && Text(reference, "name") == name, "primitive type reference");
    Check(Text(reference, "nullability") == nullability, "primitive nullability");
}

static IEnumerable<JsonElement> References(JsonElement value)
{
    if (value.ValueKind == JsonValueKind.Object)
    {
        if (value.TryGetProperty("arguments", out _) && value.TryGetProperty("nullability", out _))
            yield return value;
        foreach (var property in value.EnumerateObject())
            foreach (var reference in References(property.Value))
                yield return reference;
    }
    else if (value.ValueKind == JsonValueKind.Array)
        foreach (var element in value.EnumerateArray())
            foreach (var reference in References(element))
                yield return reference;
}
