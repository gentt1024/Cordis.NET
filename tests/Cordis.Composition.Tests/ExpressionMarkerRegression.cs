using Cordis;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ExpressionMarkerRegression
{
    private sealed class ParentExpressionEvaluator : IExpressionEvaluator
    {
        public int Calls { get; private set; }
        public object? Evaluate(string expression, Context context)
        {
            Calls++;
            return new EntryOptions
            {
                ["__jsExpr"] = expression switch
                {
                    "source.a" => "first",
                    "source.b" => "second",
                    _ => throw new InvalidOperationException("Unexpected fixture expression.")
                }
            };
        }
    }

    private static object Transport(string source, bool jsonMarker) => jsonMarker
        ? new EntryOptions { ["__jsExpr"] = source }
        : new JsExpression(source);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Parent_expression_is_opaque_unless_the_entire_node_is_volatile(bool jsonMarker, bool wholeVolatile)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var applies = 0;
            var evaluator = new ParentExpressionEvaluator();
            var description = wholeVolatile
                ? ConfigDescriptor.Object(("__jsExpr", ConfigDescriptor.String())).Volatile()
                : ConfigDescriptor.Object(("__jsExpr", ConfigDescriptor.String().Volatile()));
            var schema = new ConfigSchema<IReadOnlyDictionary<string, object?>>(
                raw => raw is EntryOptions value
                    ? ConfigResult<IReadOnlyDictionary<string, object?>>.Success(value)
                    : ConfigResult<IReadOnlyDictionary<string, object?>>.Failure("Expected object from expression."),
                description);
            schema = wholeVolatile ? schema.WithVolatileValue() : schema.WithVolatile("__jsExpr", value => (string)value["__jsExpr"]!);
            var plugin = new Plugin<IReadOnlyDictionary<string, object?>>
            {
                Configuration = schema,
                Apply = (_, _) => applies++
            };
            var loader = new Loader(ctx,
                new StaticModuleResolver().Register("parent-expression", plugin),
                expressionEvaluator: evaluator);
            await loader.Root.UpdateAsync([
                new() { Id = "p", Name = "parent-expression", Config = Transport("source.a", jsonMarker) }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            object original = wholeVolatile
                ? entry.Fiber!.GetConfigReference<IReadOnlyDictionary<string, object?>>()
                : entry.Fiber!.GetConfigReference<string>("__jsExpr");
            string Read(object reference) => wholeVolatile
                ? (string)((ConfigReference<IReadOnlyDictionary<string, object?>>)reference).Value["__jsExpr"]!
                : ((ConfigReference<string>)reference).Value;
            var calls = evaluator.Calls;

            // Re-parsing equal source must not evaluate or restart.
            await entry.UpdateAsync(new() { Config = Transport("source.a", jsonMarker) });
            await loader.WaitAsync();
            Assert.Equal(calls, evaluator.Calls);
            Assert.Equal(1, applies);

            // The source changed at the ordinary parent boundary. It is not a live child edit.
            await entry.UpdateAsync(new() { Config = Transport("source.b", jsonMarker) });
            await loader.WaitAsync();
            Assert.Equal(wholeVolatile ? 1 : 2, applies);
            Assert.Equal(wholeVolatile ? "second" : "first", Read(original));
            object replacement = wholeVolatile
                ? entry.Fiber!.GetConfigReference<IReadOnlyDictionary<string, object?>>()
                : entry.Fiber!.GetConfigReference<string>("__jsExpr");
            if (wholeVolatile) Assert.Same(original, replacement);
            else Assert.NotSame(original, replacement);
            Assert.Equal("second", Read(replacement));
        });
    }
}
