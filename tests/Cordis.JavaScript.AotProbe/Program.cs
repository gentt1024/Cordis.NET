using Cordis;
using Cordis.JavaScript;

await using var context = new Context();
await context.RunAsync(ctx =>
{
    ctx.Provide("value", 40);
    ctx.Provide("increment", (Func<double, double>)(value => value + 1));
    var evaluator = new JintExpressionEvaluator();
    var result = evaluator.Evaluate("increment(value) + 1", ctx);
    if (Convert.ToDouble(result, System.Globalization.CultureInfo.InvariantCulture) != 42)
        throw new InvalidOperationException("JavaScript/CLR function bridge returned an unexpected result.");
    evaluator.Evaluate("ctx.value = 43", ctx);
    if (Convert.ToDouble(ctx.Get("value"), System.Globalization.CultureInfo.InvariantCulture) != 43)
        throw new InvalidOperationException("JavaScript/CLR write bridge did not update the service.");
    var runtime = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT";
    Console.WriteLine($"Jint expression/function/write bridge passed ({runtime}).");
    return Task.CompletedTask;
});
