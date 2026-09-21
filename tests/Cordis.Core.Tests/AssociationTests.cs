using Xunit;

namespace Cordis.Core.Tests;

public sealed class AssociationTests
{
    private sealed class MutableState
    {
        public int Qux = 1;
    }

    private sealed class Foo : Service<MutableState>
    {
        public Foo(Context context) : base(context, "foo", new())
        {
        }

        private Foo(Foo original, Context caller) : base(original, caller)
        {
        }

        protected override Service CreateView(Context caller) => new Foo(this, caller);
        public int Qux
        {
            get => State.Qux; set => State.Qux = value;
        }
        public object? Bar
        {
            get => Associate("bar"); set => Associate("bar", value);
        }

        public object? Baz() => ((TrackedCallback)Associate("baz")!)(this, []);
        public TrackedObject Session() => new(Context, "session");
        public void AcceptType(Type value)
        {
            Assert.Equal(typeof(Foo), value);
            ForwardType(value);
        }

        private static void ForwardType(Type value) => Assert.Equal(typeof(Foo), value);
    }

    private sealed class Bar : Service
    {
        public Bar(Context context, string name = "bar") : base(context, name)
        {
        }

        private Bar(Bar original, Context caller) : base(original, caller)
        {
        }

        protected override Service CreateView(Context caller) => new Bar(this, caller);
        public int Answer() => 42;
    }

    private sealed class Callable : Service
    {
        private IReadOnlyDictionary<string, object?> _config;
        public Callable(Context context, IReadOnlyDictionary<string, object?> config) : base(context, "foo") => _config = config;
        private Callable(Callable original, Context caller) : base(original, caller) => _config = original._config;
        protected override Service CreateView(Context caller) => new Callable(this, caller);
        public IReadOnlyDictionary<string, object?> Invoke(IReadOnlyDictionary<string, object?>? head = null) => ResolveConfig(_config, head);
        public Callable ExtendConfig(IReadOnlyDictionary<string, object?> config) => Extend<Callable>(copy => copy._config = _config.Concat(config).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value));
    }

    [Fact]
    public async Task AssociatedServiceInjection()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new Foo(c) }).WaitAsync();
            var nested = ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new Bar(c, "foo.bar") });
            await nested.WaitAsync();
            var foo = ctx.Get<Foo>("foo")!;
            Assert.IsType<Foo>(foo);
            Assert.IsType<Bar>(foo.Bar);
            Assert.Equal(1, foo.Qux);
            await nested.DisposeAsync();
            Assert.Null(foo.Bar);
        });
    }

    [Fact]
    public async Task AssociatedPropertyInjectionAndSharedMutableState()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            ctx.Provide("foo.bar", null);
            ctx.Provide("foo.baz", new TrackedCallback((receiver, _) => receiver));
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new Foo(c) }).WaitAsync();
            var foo = ctx.Get<Foo>("foo")!;
            foo.Qux = 2;
            foo.Bar = 3;
            await ctx.Inject(["foo"], c =>
            {
                var view = c.Get<Foo>("foo")!;
                Assert.Equal(2, view.Qux);
                Assert.Equal(3, view.Bar);
                Assert.Throws<InvalidOperationException>(() => c.Reflect.Read("foo.qux"));
                Assert.Equal(3, c.Reflect.Read("foo.bar"));
                Assert.IsType<Foo>(view.Baz());
                view.Qux = 10;
            }).WaitAsync();
            Assert.Equal(10, foo.Qux);
            Assert.Equal(10, ctx.Get<Foo>("foo")!.Qux);
        });
    }

    [Fact]
    public async Task AssociatedTypeServiceMixin()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new Foo(c) }).WaitAsync();
            await ctx.Inject(["foo"], async c =>
            {
                var session = c.Get<Foo>("foo")!.Session();
                Assert.IsType<TrackedObject>(session);
                Assert.Null(session.Get("bar"));
                await c.Plugin(new Plugin<object?>
                {
                    Apply = (owner, _) =>
                {
                    new Bar(owner);
                    owner.Reflect.Mixin(new Dictionary<string, Accessor> { { "session.answer", new((caller, _) => new TrackedCallback((_, _) => ((Bar)caller.Reflect.Read("bar")!).Answer())) } });
                }
                }).WaitAsync();
                await c.Inject(["bar"], nested =>
                {
                    var current = nested.Get<Foo>("foo")!.Session();
                    var traced = (TrackedObject)current.ForContext(nested);
                    Assert.Equal(42, traced.Call("answer"));
                }).WaitAsync();
            }).WaitAsync();
        });
    }

    [Fact]
    public async Task AssociatedAccessorReceiverAndInjection()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new Foo(c) }).WaitAsync();
            await ctx.Plugin(new Plugin<object?>
            {
                Apply = (owner, _) =>
            {
                owner.Provide("bar", new object());
                owner.Reflect.Mixin(new Dictionary<string, Accessor> { { "session.bar", new((caller, receiver) =>
                {
                    caller.Reflect.Read("bar");
                    return ((TrackedObject)receiver!).Get("secret");
                }, (caller, value, receiver) =>
                {
                    caller.Reflect.Read("bar");
                    ((TrackedObject)receiver!).Set("secret", (int)value! + 1);
                    return true;
                }) } });
            }
            }).WaitAsync();
            await ctx.Inject(["foo"], async c =>
            {
                var session = (TrackedObject)c.Get<Foo>("foo")!.Session().ForContext(c);
                Assert.Throws<InvalidOperationException>(() => session.Get("bar"));
                await c.Inject(["bar"], nested =>
                {
                    var current = (TrackedObject)nested.Get<Foo>("foo")!.Session().ForContext(nested);
                    Assert.Null(current.Get("bar"));
                    current.Set("bar", 100);
                    Assert.Equal(101, current.Get("bar"));
                }).WaitAsync();
            }).WaitAsync();
        });
    }

    [Fact]
    public async Task TypeArgumentsPreserveIdentity()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new Foo(c) }).WaitAsync();
            ctx.Get<Foo>("foo")!.AcceptType(typeof(Foo));
        });
    }

    [Fact]
    public async Task CallableServiceExtensionsAndInterception()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new Callable(c, new Dictionary<string, object?> { { "a", 1 } }) }).WaitAsync();
            var foo = ctx.Get<Callable>("foo")!;
            Assert.Equal(1, foo.Invoke()["a"]);
            var intercepted = ctx.Intercept("foo", new Dictionary<string, object?> { { "b", 2 } }).Get<Callable>("foo")!;
            Assert.Equal(2, intercepted.Invoke()["b"]);
            var foo2 = foo.ExtendConfig(new Dictionary<string, object?> { { "c", 3 } });
            Assert.IsType<Callable>(foo2);
            Assert.Equal(3, foo2.Invoke()["c"]);
            Assert.False(foo2.Invoke().ContainsKey("b"));
            var foo3 = intercepted.ExtendConfig(new Dictionary<string, object?> { { "d", 4 } });
            Assert.Equal(2, foo3.Invoke()["b"]);
            Assert.Equal(4, foo3.Invoke()["d"]);
            Assert.False(intercepted.Invoke().ContainsKey("d"));
        });
    }

    [Fact]
    public async Task ServiceMixinIsPropertyNotServiceAndMetadataInherits()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            ctx.Reflect.Mixin(new Dictionary<string, Accessor> { { "bar", new((caller, _) => ((Dictionary<string, object?>)caller.Reflect.Read("foo")!)["bar"]) } });
            ctx.Provide("foo", new Dictionary<string, object?> { { "bar", 1 } });
            Assert.NotNull(ctx.Get("foo"));
            Assert.Null(ctx.Get("bar"));
            Assert.Null(ctx.Get("root"));
            await ctx.Inject(["foo"], async c =>
            {
                var extended = c.Extend();
                extended.Metadata["baz"] = 2;
                await extended.Plugin(new Plugin<object?>
                {
                    Apply = (child, _) =>
                {
                    Assert.Equal(2, child.FindMetadata("baz"));
                    Assert.Equal(1, child.Reflect.Read("bar"));
                }
                }).WaitAsync();
            }).WaitAsync();
        });
    }

    private sealed class CounterState
    {
        public int Value;
    }

    private sealed class Counter : IContextualService
    {
        private readonly Context _context;
        private readonly CounterState _state;
        public Counter(Context context) => (_context, _state) = (context, new());
        private Counter(Counter original, Context context) => (_context, _state) = (context, original._state);
        public object ForContext(Context caller) => new Counter(this, caller);
        public int Value => _state.Value;

        public EffectHandle Increase() => _context.Effect(() =>
        {
            _state.Value++;
            return (Action)(() => _state.Value--);
        });
    }

    private sealed class CounterReader : Service
    {
        public CounterReader(Context context) : base(context, "foo")
        {
        }

        private CounterReader(CounterReader original, Context caller) : base(original, caller)
        {
        }

        protected override Service CreateView(Context caller) => new CounterReader(this, caller);
        public int Value => ((Counter)Context.Reflect.Read("counter")!).Value;

        public EffectHandle Increase() => ((Counter)Context.Reflect.Read("counter")!).Increase();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TraceableCounterSourceSequence(bool declare)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            ctx.Provide("counter", null);
            ctx.Set("counter", new Counter(ctx));
            await ctx.Plugin(new Plugin<object?> { Inject = declare ? ["counter"] : [], Apply = (c, _) => new CounterReader(c) }).WaitAsync();
            ctx.Get<CounterReader>("foo")!.Increase();
            Assert.Equal(1, ctx.Get<CounterReader>("foo")!.Value);
            var consumer = ctx.Inject(["foo"], c =>
            {
                ctx.Get<CounterReader>("foo")!.Increase();
                Assert.Equal(2, c.Get<CounterReader>("foo")!.Value);
            });
            await consumer.WaitAsync();
            await consumer.DisposeAsync();
            ctx.Get<CounterReader>("foo")!.Increase();
            Assert.Equal(3, ctx.Get<CounterReader>("foo")!.Value);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HookSnapshotsRestoreAfterDeleteAndReapply(bool service)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var plugin = new Plugin<object?>
            {
                ApplyAsync = async (c, _) =>
                {
                    if (service)
                    {
                        new Foo(c);
                        c.Inject(["foo"], _ =>
                        {
                        });
                        return;
                    }

                    c.On("event", (_, _) => null);
                    await c.Plugin(new Plugin<object?>
                    {
                        ApplyAsync = async (child, _) =>
                    {
                        child.On("event", (_, _) => null);
                        await child.Plugin(new Plugin<object?> { Apply = (nested, _) => nested.On("event", (_, _) => null) }).WaitAsync();
                    }
                    }).WaitAsync();
                }
            };
            var before = ctx.Events.ListenerCounts;
            await ctx.Plugin(plugin).WaitAsync();
            var after = ctx.Events.ListenerCounts;
            await ctx.Registry.DeleteAsync(plugin);
            Assert.Equal(before, ctx.Events.ListenerCounts);
            await ctx.Plugin(plugin).WaitAsync();
            Assert.Equal(after, ctx.Events.ListenerCounts);
        });
    }
}
