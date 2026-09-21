using System.Text.Json;
using Cordis.Conformance;

// Explicit registration avoids a reflection-discovered test framework in the native executable.
// The same scenario methods execute under JIT and Native AOT; this is a conformance harness,
// complementary to the discoverable unit and integration test projects.
bool requireAot = args.Length == 1 && args[0] == "--require-aot";
if (requireAot && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
{
    Console.Error.WriteLine("Expected an actual Native AOT executable, not JIT execution.");
    return 2;
}
string? filter = args.Length == 2 && args[0] == "--case" ? args[1] : null;
if (args.Length != 0 && filter is null && !requireAot)
{
    Console.Error.WriteLine("Usage: Cordis.Conformance [--case ID | --require-aot]");
    return 2;
}
var cases = Scenarios.Cases.Concat(ExtendedScenarios.Cases).Concat(TimerScenarios.Cases).Where(c => filter is null || c.Id == filter).ToArray();
if (cases.Length == 0) { Console.Error.WriteLine("Unknown scenario."); return 2; }
try
{
    await PlatformScenarios.CheckAsync().WaitAsync(TimeSpan.FromSeconds(15));
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL .NET execution-boundary checks\n{error}");
    return 1;
}
var results = new List<(string Id, string[] Trace)>();
foreach (var test in cases)
{
    try
    {
        // A watchdog, never a timing oracle; progress inside each scenario uses explicit gates.
        string[] trace = await test.Run().WaitAsync(TimeSpan.FromSeconds(15));
        results.Add((test.Id, trace));
        Console.Error.WriteLine($"PASS {test.Id}");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {test.Id}\n{error}");
        return 1;
    }
}
using var stream = new MemoryStream();
using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteString("format", "cordis-core-trace/v1");
    writer.WriteStartArray("cases");
    foreach (var result in results)
    {
        writer.WriteStartObject();
        writer.WriteString("id", result.Id);
        writer.WriteStartArray("trace");
        foreach (string entry in result.Trace) writer.WriteStringValue(entry);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
}
Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
return 0;
