using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ScopeStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Entry_tables_preserve_live_order_exact_undo_and_drained_generations(bool anonymous)
    {
        var duplicate = new InvalidOperationException("caller duplicate");
        var named = new NamedEntries<Borrowed>(_ => duplicate);
        var unnamed = new AnonymousEntries<Borrowed>();
        Func<string, Borrowed, Action> insert = anonymous ? (_, value) => unnamed.Append(value) : named.Insert;
        IEnumerable<Borrowed> Values() => anonymous ? unnamed.Values : named.Values;
        var value = new Borrowed();
        var replacement = new Borrowed();
        var first = insert("a", value);
        using var iterator = Values().GetEnumerator();
        Assert.True(iterator.MoveNext());
        Assert.Same(value, iterator.Current);
        var second = insert("b", value);
        if (!anonymous)
        {
            Assert.Same(duplicate, Assert.Throws<InvalidOperationException>(() => named.Insert("a", replacement)));
            Assert.True(named.ContainsKey("a"));
            Assert.False(named.TryGetValue("missing", out _));
            Assert.Equal(["a", "b"], named.Keys);
        }

        first();
        var third = insert("a", replacement);
        first();
        Assert.True(iterator.MoveNext());
        Assert.Same(value, iterator.Current);
        Assert.True(iterator.MoveNext());
        Assert.Same(replacement, iterator.Current);
        second();
        Assert.Equal([replacement], Values());
        third();
        Assert.True(anonymous ? unnamed.IsEmpty : named.IsEmpty);
        insert("fresh", value);
        Assert.False(iterator.MoveNext());
        Assert.Equal([value], Values());
        Assert.False(value.Disposed);
        Assert.False(replacement.Disposed);
    }

    [Fact]
    public async Task Layers_read_without_creating_and_merge_parent_shadows_in_order()
    {
        var created = new List<object?>();
        var layers = new ScopedLayers<Layer>(
            key =>
            {
                created.Add(key);
                return new();
            },
            () =>
            {
            });
        var parentKey = new Key(1);
        var childKey = new Key(2);
        layers.Global.Named.Insert("first", 1);
        layers.Global.Named.Insert("shared", 2);
        Assert.Null(layers.Peek(childKey));
        Assert.Empty(layers.ChainLayers(childKey));
        Assert.Equal([1, 2], layers.Merge(childKey, layer => layer.Named).Values);
        Assert.Equal([null], created);
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await using var parent = Scope.Create(ctx, parentKey);
            await using var child = Scope.Create(parent.Context, childKey, parentKey);
            layers.Effect(parent.Context, layer => layer.Named.Insert("shared", 3), "parent");
            layers.Effect(parent.Context, layer => layer.Named.Insert("parent-only", 4), "parent-only");
            layers.Effect(child.Context, layer => layer.Named.Insert("shared", 5), "child");
            Assert.Null(layers.Peek(new Key(2)));
            Assert.Equal([null, parentKey, childKey], created);
            Assert.Equal(
                [Assert.IsType<Layer>(layers.Peek(parentKey)), Assert.IsType<Layer>(layers.Peek(childKey))],
                layers.ChainLayers(childKey));
            var merged = layers.Merge(childKey, layer => layer.Named);
            Assert.Equal(["first", "shared", "parent-only"], merged.Keys);
            Assert.Equal([1, 5, 4], merged.Values);
            merged["shared"] = 99;
            Assert.Equal(5, layers.Merge(childKey, layer => layer.Named)["shared"]);
            Assert.Single(layers.Peek(childKey)!.Named.Entries);
        });
    }

    [Fact]
    public async Task Registration_context_owns_cleanup_and_only_empty_aggregate_layers_are_reclaimed()
    {
        var changes = 0;
        var layers = new ScopedLayers<Layer>(_ => new(), () => changes++);
        var key = new object();
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            layers.Effect(ctx, layer => layer.Named.Insert("global", 1), "global", notify: false);
            await using var scope = Scope.Create(ctx, key);
            var named = layers.Effect(scope.Context.Extend(), layer => layer.Named.Insert("name", 2), "named");
            layers.Effect(scope.Context, layer => layer.Anonymous.Append(3), "anonymous", notify: false);
            var layer = layers.Peek(key);
            Assert.NotNull(layer);
            Assert.Equal(1, changes);
            await named.DisposeAsync();
            Assert.Same(layer, layers.Peek(key));
            Assert.Equal([3], layer.Anonymous.Values);
            await scope.DisposeAsync();
            Assert.Null(layers.Peek(key));
            Assert.Equal(2, changes);
            Assert.Equal([1], layers.Global.Named.Values);
        });
    }

    [Fact]
    public async Task Failed_setup_reclaims_empty_layers_and_collects_undo_before_notification()
    {
        var changed = 0;
        var notification = new InvalidOperationException("notify");
        var mutation = new InvalidOperationException("mutation");
        var layers = new ScopedLayers<Layer>(
            _ => new(),
            () =>
            {
                if (++changed == 1)
                    throw notification;
            });
        var key = new object();
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await using var scope = Scope.Create(ctx, key);
            Assert.Same(
                mutation,
                Assert.Throws<InvalidOperationException>(() =>
                    layers.Effect(scope.Context, _ => throw mutation, "mutation")));
            Assert.Null(layers.Peek(key));
            Assert.Equal(0, changed);
            Assert.Same(
                notification,
                Assert.Throws<InvalidOperationException>(() =>
                    layers.Effect(scope.Context, layer => layer.Named.Insert("entry", 1), "notify")));
        });
        Assert.Null(layers.Peek(key));
        Assert.Equal(2, changed);
    }

    private sealed record Key(int Value);

    private sealed class Layer : IScopeLayer
    {
        public NamedEntries<int> Named
        {
            get;
        } = new(name => new InvalidOperationException(name));

        public AnonymousEntries<int> Anonymous
        {
            get;
        } = new();

        public bool IsEmpty => Named.IsEmpty && Anonymous.IsEmpty;
    }

    private sealed class Borrowed : IDisposable
    {
        public bool Disposed
        {
            get;
            private set;
        }

        public void Dispose() => Disposed = true;
    }
}
