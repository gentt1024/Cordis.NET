using Cordis.Composition;

namespace Cordis.Conformance;

public static class ExtendedScenarios
{
    public static IReadOnlyList<(string Id, Func<Task<string[]>> Run)> Cases { get; } =
    [
        ("C19-event-bail-values-and-once", Events), ("C20-event-waterfall-veto", Waterfall),
        ("C21-service-shared-realm", Realms), ("C22-logger-exporter-removal", LoggerRemoval),
        ("C23-patch-aliases-and-layer-order", Patches), ("C24-patch-group-include-boundaries", Boundaries),
        ("C25-fiber-local-update-hooks-survive-reload", UpdateHooks),
        ("C28-internal-set-has-no-dispatch-receiver", InternalSet),
        ("C29-yaml-nonfinite-round-trip", YamlNonFinite),
        ("C30-json-nonfinite-matches-stringify", JsonNonFinite),
    ];
    private static async Task<string[]> Run(Func<Context, List<string>, Task> body)
    {
        await using var root = new Context(); List<string> trace = [];
        await root.RunAsync(ctx => body(ctx, trace)); return [.. trace];
    }
    private static Task<string[]> Events() => Run((ctx, trace) =>
    {
        ctx.On("check", (_, _) => { trace.Add("false"); return false; });
        ctx.On("check", (_, _) => { trace.Add("null"); return null; });
        ctx.On("check", (_, _) => { trace.Add("undefined"); return Undefined.Value; });
        ctx.On("check", (_, _) => { trace.Add("zero"); return 0; });
        ctx.On("check", (_, _) => { trace.Add("not-called"); return 1; });
        Scenarios.Equal<object?>(0, ctx.Events.Bail("check"));
        ctx.Events.Once("once", (_, _) => { trace.Add("once"); ctx.Events.Emit("once"); return null; });
        ctx.Events.Emit("once"); ctx.Events.Emit("once"); return Task.CompletedTask;
    });
    private static Task<string[]> Waterfall() => Run((ctx, trace) =>
    {
        ctx.On("water", (e, _) => { trace.Add("before"); var result = e.Next(); trace.Add("after"); return (int)result! + 1; });
        ctx.On("water", (e, _) => { trace.Add("inner"); return e.Next(); });
        Scenarios.Equal<object?>(5, ctx.Events.Waterfall("water", () => { trace.Add("final"); return 4; }, 4));
        ctx.On("veto", (_, _) => { trace.Add("veto"); return null; });
        Scenarios.Equal<object?>(null, ctx.Events.Waterfall("veto", () => { trace.Add("must-not-run"); return null; }));
        return Task.CompletedTask;
    });
    private static Task<string[]> Realms() => Run((ctx, trace) =>
    {
        var left = ctx.Isolate("s", "shared"); var right = ctx.Isolate("s", "shared"); var privateContext = ctx.Isolate("s");
        var value = new object(); left.Provide("s", value);
        Scenarios.Equal(true, ReferenceEquals(value, right.Get<object>("s")));
        Scenarios.Equal<object?>(null, ctx.Get<object>("s")); Scenarios.Equal<object?>(null, privateContext.Get<object>("s"));
        trace.Add("shared-visible;root-hidden;private-hidden"); return Task.CompletedTask;
    });
    private static Task<string[]> LoggerRemoval() => Run(async (ctx, trace) =>
    {
        var first = ctx.Logger.Exporter(new DelegateLogExporter(_ => trace.Add("first")));
        var second = ctx.Logger.Exporter(new DelegateLogExporter(_ => trace.Add("second")));
        await first.DisposeAsync(); ctx.Logger.Info("message"); await second.DisposeAsync(); ctx.Logger.Info("ignored");
        Scenarios.Equal("second", string.Join('|', trace));
    });
    private static Task<string[]> Patches() => Run((_, trace) =>
    {
        var data = ConfigurationFile.ParseEntries("[{id: a, name: alpha, config: {old: 1, keep: 2}}]");
        var arrayCopy = EntryPatches.Apply(data, []);
        Scenarios.Equal(false, ReferenceEquals(data, arrayCopy)); Scenarios.Equal(true, ReferenceEquals(data[0], arrayCopy[0]));
        var inserted = new EntryOptions { Id = "b", Name = "beta", Config = new EntryOptions { ["v"] = 1 } };
        List<EntryOptions> patch = [new() { ["insert"] = new List<EntryOptions> { inserted } }, new() { Id = "b", Config = new EntryOptions { ["v"] = 2 } },
            new() { Id = "a", Name = "wrong", Disabled = true }, new() { Id = "a", Config = new EntryOptions { ["new"] = 3 } }, new() { Id = "missing", Disabled = true }];
        List<string> warnings = [];
        var result = EntryPatches.Apply(data, patch, warnings.Add);
        Scenarios.Equal(false, ReferenceEquals(data[0], result[0])); Scenarios.Equal(true, ReferenceEquals(inserted, result[1]));
        Scenarios.Equal<object?>(2, ((EntryOptions)inserted.Config!)["v"]);
        Scenarios.Equal(1, ((EntryOptions)result[0].Config!).Count); Scenarios.Equal(2, ((EntryOptions)data[0].Config!).Count);
        Scenarios.Equal<object?>(null, result[0].Disabled); Scenarios.Equal(2, warnings.Count);
        trace.Add("array-copy;shared-unpatched;cloned-base;insert-alias;later-hit;replace-config;two-warnings"); return Task.CompletedTask;
    });
    private static Task<string[]> Boundaries() => Run((_, trace) =>
    {
        var data = ConfigurationFile.ParseEntries("[{id: g, name: 'cordis:group', group: true, config: [{id: child, name: a}]}, {id: i, name: 'cordis:include', config: {initial: [{id: hidden, name: b}]}}]");
        var patch = ConfigurationFile.ParseEntries("[{id: child, disabled: true}, {id: hidden, disabled: true}, {id: i, insert: [{id: x, name: x}]}]");
        List<string> warnings = []; var rows = EntryPatches.Apply(data, patch, warnings.Add);
        Scenarios.Equal<object?>(true, Data.Entries(rows[0].Config)[0].Disabled);
        Scenarios.Equal<object?>(null, Data.Entries(((EntryOptions)rows[1].Config!)["initial"])[0].Disabled);
        Scenarios.Equal(2, warnings.Count); trace.Add("group-traversed;include-boundary;two-warnings"); return Task.CompletedTask;
    });
    private static Task<string[]> UpdateHooks() => Run(async (ctx, trace) =>
    {
        var generation = 0;
        var fiber = ctx.Plugin(new Plugin<object?>
        {
            Apply = (child, _) =>
            {
                var current = ++generation;
                trace.Add($"apply:{current}");
                child.On("internal/update", (e, _) =>
                {
                    trace.Add($"hook:{current}");
                    return e.Next();
                });
            },
        }, 0);
        await fiber.WaitAsync();
        fiber.Update(1); await fiber.WaitAsync();
        fiber.Update(2); await fiber.WaitAsync();
        Scenarios.Equal("apply:1|hook:1|apply:2|hook:1|hook:2|apply:3", string.Join('|', trace));
    });
    private static Task<string[]> InternalSet() => Run((ctx, trace) =>
    {
        ctx.Provide("value", 1);
        ctx.Filter = _ => false;
        var calls = 0;
        ctx.On("internal/set", (evt, args) =>
        {
            calls++;
            Scenarios.Equal<object?>(null, evt.Receiver);
            Scenarios.Equal(true, ReferenceEquals(ctx, args[0]));
            Scenarios.Equal<object?>("value", args[1]); Scenarios.Equal<object?>(2, args[2]);
            Scenarios.Equal(true, args[3] is InvalidOperationException);
            trace.Add("hook");
            return evt.Next();
        });
        ctx.Reflect.Write("value", 2);
        Scenarios.Equal(2, ctx.Get<int>("value")); Scenarios.Equal(1, calls);
        ctx.Set("value", 3);
        Scenarios.Equal(3, ctx.Get<int>("value")); Scenarios.Equal(1, calls);
        trace.Add("write:2;direct:3;calls:1");
        return Task.CompletedTask;
    });
    private static Task<string[]> YamlNonFinite() => Run((_, trace) =>
    {
        var first = (EntryOptions)ConfigurationFile.Parse("positive: .inf\nnegative: -.inf\nnan: .nan\n")!;
        var second = (EntryOptions)ConfigurationFile.Parse(ConfigurationFile.Write(first))!;
        Scenarios.Equal(double.PositiveInfinity, (double)second["positive"]!);
        Scenarios.Equal(double.NegativeInfinity, (double)second["negative"]!);
        Scenarios.Equal(true, double.IsNaN((double)second["nan"]!));
        trace.Add("positive:infinity;negative:-infinity;nan:number");
        return Task.CompletedTask;
    });
    private static Task<string[]> JsonNonFinite() => Run((_, trace) =>
    {
        var value = new EntryOptions
        {
            ["positive"] = double.PositiveInfinity,
            ["negative"] = double.NegativeInfinity,
            ["nan"] = double.NaN,
            ["array"] = new List<object?> { double.PositiveInfinity, "finite", null, new EntryOptions { ["nested"] = double.NaN } },
            ["finite"] = 1.25,
            ["text"] = "unchanged",
            ["nil"] = null,
        };
        trace.Add(ConfigurationFile.Write(value, json: true).TrimEnd());
        return Task.CompletedTask;
    });
}
