using Cordis.Hosting;
using Cordis.Composition;
using Cordis.JavaScript;
using Jint.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed class HostAndJavaScriptTests
{
    [Fact]
    public async Task Borrowed_host_service_is_disposed_only_by_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<BorrowedService>();
        services.AddCordis(options => options.Borrow<BorrowedService>("borrowed").Configure(async (ctx, _, _) =>
        {
            var fiber = ctx.Plugin(new Plugin<object?> { Inject = ["borrowed"], Apply = (child, _) => Assert.NotNull(child.Get<BorrowedService>("borrowed")) });
            await fiber.WaitAsync();
        }));
        var provider = services.BuildServiceProvider();
        var borrowed = provider.GetRequiredService<BorrowedService>();
        var hosted = provider.GetRequiredService<IHostedService>();
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
        Assert.Equal(0, borrowed.DisposeCount);
        await provider.DisposeAsync();
        Assert.Equal(1, borrowed.DisposeCount);
    }

    [Fact]
    public async Task JavaScript_scope_clr_views_functions_and_errors_are_real()
    {
        await using var context = new Context();
        var evaluator = new JintExpressionEvaluator();
        await context.RunAsync(ctx =>
        {
            var view = new JavaScriptView();
            ctx.Provide("answer", view);
            ctx.Provide("doubleIt", (Func<int, int>)(value => value * 2));
            ctx.Provide("nothing", null);
            ctx.Provide("voidValue", Undefined.Value);
            Assert.Equal(42d, evaluator.Evaluate("doubleIt(answer.Value)", ctx));
            Assert.Same(view, evaluator.Evaluate("answer", ctx));
            Assert.Equal(42d, evaluator.Evaluate("[1,2,3].map(x => x * 7).reduce((a,b) => a+b, 0)", ctx));
            Assert.Equal(true, evaluator.Evaluate("nothing === null && ctx.missing === undefined", ctx));
            Assert.Equal(true, evaluator.Evaluate("voidValue === undefined", ctx));
            Assert.Same(Undefined.Value, evaluator.Evaluate("undefined", ctx));
            var values = Assert.IsAssignableFrom<IList<object?>>(evaluator.Evaluate("[undefined, null, false, 0]", ctx));
            Assert.Same(Undefined.Value, values[0]);
            Assert.Null(values[1]);
            Assert.Equal(false, values[2]);
            var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(evaluator.Evaluate("({absent: undefined, nothing: null})", ctx));
            Assert.Same(Undefined.Value, map["absent"]);
            Assert.Null(map["nothing"]);
            ctx.Provide("mutable", 3);
            Assert.Equal(4d, evaluator.Evaluate("mutable = mutable + 1", ctx));
            Assert.Equal(4d, ctx.Get<object>("mutable"));
            Assert.Same(Undefined.Value, evaluator.Evaluate("mutable = undefined", ctx));
            Assert.Same(Undefined.Value, ctx.Get<object>("mutable"));
            Assert.Equal("OK", evaluator.Evaluate("answer.Echo('ok').toUpperCase()", ctx));
            var reference = Assert.Throws<JavaScriptExpressionException>(() => evaluator.Evaluate("missingIdentifier + 1", ctx));
            Assert.Equal("ReferenceError", reference.ErrorName); Assert.IsType<JavaScriptException>(reference.InnerException);
            var thrown = Assert.Throws<JavaScriptExpressionException>(() => evaluator.Evaluate("(() => { throw new Error('expression-failed') })()", ctx));
            Assert.Equal("Error", thrown.ErrorName); Assert.Contains("expression-failed", thrown.Message); Assert.IsType<JavaScriptException>(thrown.InnerException);
            var syntax = Assert.Throws<JavaScriptExpressionException>(() => evaluator.Evaluate("JSON.parse('invalid')", ctx));
            Assert.Equal("SyntaxError", syntax.ErrorName); Assert.StartsWith("SyntaxError:", syntax.ToString()); Assert.IsType<JavaScriptException>(syntax.InnerException);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task JavaScript_evaluation_is_lazy_and_does_not_cache_provider_values()
    {
        await using var context = new Context();
        var evaluator = new JintExpressionEvaluator();
        await context.RunAsync(async ctx =>
        {
            var provide = ctx.Provide("number", 1);
            Assert.Equal(2d, evaluator.Evaluate("number + 1", ctx));
            await provide.DisposeAsync();
            ctx.Provide("number", 7);
            Assert.Equal(8d, evaluator.Evaluate("number + 1", ctx));
        });
    }

    [Fact]
    public async Task Loader_resolves_real_js_only_when_dependencies_are_ready_and_again_after_replacement()
    {
        await using var context = new Context();
        var values = new List<object?>();
        var modules = new StaticModuleResolver().Register("consumer", new Plugin<object?>
        {
            Inject = ["number"],
            Apply = (_, config) => values.Add(config)
        });
        await context.RunAsync(async ctx =>
        {
            var loader = new Loader(ctx, modules, expressionEvaluator: new JintExpressionEvaluator());
            var raw = new JsExpression("number * 2");
            await loader.Root.UpdateAsync([new EntryOptions { Id = "consumer", Name = "consumer", Config = raw }]);
            await loader.WaitAsync();
            Assert.Empty(values);
            Assert.Same(raw, loader.Resolve("consumer").Fiber!.RawConfig);
            var provider = ctx.Provide("number", 4);
            await loader.WaitAsync();
            Assert.Equal(new object?[] { 8d }, values);
            await provider.DisposeAsync();
            ctx.Provide("number", 9);
            await loader.WaitAsync();
            Assert.Equal(new object?[] { 8d, 18d }, values);
            Assert.Same(raw, loader.Resolve("consumer").Options.Config);
        });
    }

    [Fact]
    public async Task Loader_treats_real_jint_undefined_disabled_expression_as_false()
    {
        await using var context = new Context();
        var applies = 0;
        await context.RunAsync(async ctx =>
        {
            var loader = new Loader(ctx,
                new StaticModuleResolver().Register("plugin", new Plugin<object?> { Apply = (_, _) => applies++ }),
                expressionEvaluator: new JintExpressionEvaluator());
            await loader.CreateAsync(new() { Id = "row", Name = "plugin", Disabled = new JsExpression("undefined") });
            await loader.WaitAsync();
            Assert.False(loader.Resolve("row").Disabled);
            Assert.Equal(1, applies);
        });
    }

    [Fact]
    public async Task Host_start_failure_cleans_cordis_resources_without_disposing_borrowed_service()
    {
        var stopped = false;
        var services = new ServiceCollection();
        services.AddSingleton<BorrowedService>();
        services.AddCordis(options => options.Borrow<BorrowedService>("borrowed").Configure((ctx, _, _) =>
        {
            ctx.Effect(() => (Action)(() => stopped = true));
            throw new InvalidOperationException("startup");
        }));
        await using var provider = services.BuildServiceProvider();
        var borrowed = provider.GetRequiredService<BorrowedService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken.None));
        Assert.True(stopped);
        Assert.Equal(0, borrowed.DisposeCount);
    }

    public sealed class JavaScriptView
    {
        public int Value => 21;
        public string Echo(string value) => value;
    }
    public sealed class BorrowedService : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
