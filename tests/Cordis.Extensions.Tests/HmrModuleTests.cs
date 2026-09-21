using Cordis.Composition;
using Xunit;

namespace Cordis.Extensions.Tests;

/// <summary>Portable assertions from pinned boot/hmr/modules.spec.ts; module mapping is explicit on CLR.</summary>
public sealed class HmrModuleTests
{
    [Fact]
    public async Task Tracked_loader_resolution_warns_for_missing_entries_and_ignores_framework_and_uncached_modules()
    {
        await using var context = new Context(); await using var hmr = new HmrCoordinator();
        Loader? loader = null;
        var plugin = new Plugin<object?> { Apply = (_, _) => { } };
        await context.RunAsync(async ctx =>
        {
            loader = new Loader(ctx, new StaticModuleResolver().Register("framework", plugin).Register("gone", plugin).Register("uncached", plugin));
            await loader.Root.UpdateAsync([new() { Id = "framework", Name = "framework" }, new() { Id = "gone", Name = "gone" }, new() { Id = "uncached", Name = "uncached" }]);
            await loader.WaitAsync();
        });
        var failure = new FileNotFoundException("entry gone"); var warnings = new List<object?>(); hmr.Warning += warnings.Add;
        using var tracking = hmr.TrackLoader(loader!, (name, _) => name switch
        {
            "gone" => throw failure,
            "uncached" => ValueTask.FromResult<string?>(null),
            _ => ValueTask.FromResult<string?>("framework.dll")
        });
        hmr.RegisterFramework("framework.dll");
        int replacements = 0; hmr.RegisterModule("framework.dll", () => { replacements++; return Task.CompletedTask; });
        await hmr.NotifyChangedAsync("dependency.dll");
        Assert.Same(failure, Assert.Single(warnings)); Assert.Equal(0, replacements); Assert.Empty(hmr.GetLinked("missing.dll"));
        Assert.All(loader!.Entries(), entry => Assert.Equal(FiberState.Active, entry.Fiber!.State));
        tracking.Dispose(); warnings.Clear(); await hmr.NotifyChangedAsync("dependency.dll"); Assert.Empty(warnings);
    }

    [Fact]
    public async Task Explicit_dependency_graph_follows_transitive_edges_and_terminates_cycles_without_touching_unrelated_leaves()
    {
        await using var hmr = new HmrCoordinator();
        var replaced = new List<string>();
        foreach (var name in new[] { "changed", "parent", "leaf", "cycle-a", "cycle-b" })
            hmr.RegisterModule(name + ".dll", () => { replaced.Add(name); return Task.CompletedTask; });
        hmr.RegisterDependencies("parent.dll", ["middle.dll", "leaf.dll", "framework.dll"]);
        hmr.RegisterDependencies("middle.dll", ["changed.dll"]);
        hmr.RegisterDependencies("cycle-a.dll", ["cycle-b.dll"]);
        hmr.RegisterDependencies("cycle-b.dll", ["cycle-a.dll"]);
        hmr.RegisterFramework("framework.dll");
        await hmr.NotifyChangedAsync("changed.dll");
        Assert.Equal(["changed", "parent"], replaced);
        Assert.Empty(hmr.GetLinked("missing.dll"));
        Assert.Equal([Path.GetFullPath("changed.dll")], hmr.GetLinked("middle.dll"));
        replaced.Clear();
        await hmr.NotifyChangedAsync("cycle-a.dll");
        Assert.Equal(["cycle-a", "cycle-b"], replaced);
    }

    [Fact]
    public async Task Module_replacement_waits_for_mutation_and_runs_after_mutation_failure()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        var before = new Plugin<string> { Apply = (_, _) => { } };
        int calls = 0;
        var after = new Plugin<string> { Apply = (_, _) => calls++ };
        var loader = await MountAsync(root, before, "entry");
        hmr.RegisterModule("plugin.dll", () => loader.ReplacePluginAsync(before, after));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = hmr.RunExclusiveAsync(async () => { started.SetResult(); await release.Task; throw new IOException("mutation failed"); });
        await started.Task;
        var reload = hmr.NotifyChangedAsync("plugin.dll"); Assert.Equal(0, calls);
        release.SetResult(); await Assert.ThrowsAsync<IOException>(() => mutation); await reload;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Replacement_retains_latest_entry_configuration_and_remains_updatable()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        var mounted = new List<string>(); var disposed = new List<string>();
        IPlugin Before(string version) => new Plugin<string>
        {
            Apply = (ctx, value) =>
        { mounted.Add(version + ":" + value); ctx.Effect(() => (Action)(() => disposed.Add(version))); }
        };
        var before = Before("before"); var after = Before("after");
        var loader = await MountAsync(root, before, "initial");
        await loader.UpdateAsync("row", new() { Config = "current" }); await loader.WaitAsync();
        var events = 0; hmr.Reloaded += _ => events++;
        hmr.RegisterModule("plugin.dll", () => loader.ReplacePluginAsync(before, after));
        await hmr.NotifyChangedAsync("plugin.dll");
        Assert.Equal("after:current", mounted[^1]); Assert.Equal(["before", "before"], disposed); Assert.Equal(1, events);
        await loader.UpdateAsync("row", new() { Config = "next" }); await loader.WaitAsync();
        Assert.Equal("after:next", mounted[^1]);
    }

    [Fact]
    public async Task Replaced_consumer_reactivates_after_provider_and_consumer_updates()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        var mounted = new List<string>();
        var provider = new Plugin<int> { Apply = (ctx, revision) => ctx.Provide("revision", revision) };
        var before = new Plugin<string> { Inject = ["revision"], Apply = (_, _) => { } };
        var after = new Plugin<string> { Inject = ["revision"], Apply = (ctx, value) => mounted.Add($"{value}:{ctx.Get("revision")}") };
        Loader? loader = null;
        await root.RunAsync(async ctx =>
        {
            loader = new Loader(ctx, new StaticModuleResolver().Register("provider", provider).Register("consumer", before));
            await loader.Root.UpdateAsync([new() { Id = "provider", Name = "provider", Config = 1 }, new() { Id = "consumer", Name = "consumer", Config = "initial" }]);
            await loader.WaitAsync();
        });
        hmr.RegisterModule("consumer.dll", () => loader!.ReplacePluginAsync(before, after));
        await hmr.NotifyChangedAsync("consumer.dll"); Assert.Equal(["initial:1"], mounted);
        await Task.WhenAll(loader!.UpdateAsync("provider", new() { Config = 2 }), loader.UpdateAsync("consumer", new() { Config = "updated" }));
        await loader.WaitAsync(); Assert.Equal("updated:2", mounted[^1]);
    }

    [Fact]
    public async Task Replacement_failure_restores_old_plugin_and_does_not_emit_success()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        int mounted = 0, notifications = 0;
        var original = new Plugin<string> { Apply = (_, _) => mounted++ };
        var candidate = new Plugin<string> { Apply = (_, _) => throw new InvalidOperationException("activation failed") };
        var loader = await MountAsync(root, original, "entry");
        hmr.Reloaded += _ => notifications++;
        hmr.RegisterModule("plugin.dll", () => loader.ReplacePluginAsync(original, candidate));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hmr.NotifyChangedAsync("plugin.dll"));
        Assert.Equal("activation failed", error.Message); Assert.Equal(2, mounted); Assert.Equal(0, notifications);
        await root.RunAsync(ctx => { Assert.Same(loader.Resolve("row").Fiber, Assert.Single(ctx.Registry.Get(original)!.Fibers)); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Disposed_child_instances_are_left_to_the_replacing_parent()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        var seen = new List<string>();
        var before = new Plugin<string> { Apply = (_, value) => seen.Add(value) };
        var after = new Plugin<string> { Apply = (_, value) => seen.Add("new:" + value) };
        var loader = await MountAsync(root, before, "entry");
        await root.RunAsync(async ctx =>
        {
            await loader.Resolve("row").Fiber!.Context.Plugin(before, "child").WaitAsync();
            await ctx.Plugin(before, "independent").WaitAsync();
        });
        hmr.RegisterModule("plugin.dll", () => loader.ReplacePluginAsync(before, after));
        await hmr.NotifyChangedAsync("plugin.dll");
        Assert.Contains("new:entry", seen); Assert.Contains("new:independent", seen); Assert.DoesNotContain("new:child", seen);
        Assert.Equal("entry", loader.Resolve("row").Fiber!.RawConfig);
    }

    [Fact]
    public async Task Disabled_entries_are_not_activated_by_invalidation()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        var calls = 0;
        var old = new Plugin<object?> { Apply = (_, _) => { } };
        var candidate = new Plugin<object?> { Apply = (_, _) => calls++ };
        Loader? loader = null;
        await root.RunAsync(async ctx =>
        {
            loader = new Loader(ctx, new StaticModuleResolver().Register("disabled", old));
            await loader.CreateAsync(new() { Id = "row", Name = "disabled", Disabled = true });
        });
        hmr.RegisterModule("disabled.dll", () => loader!.ReplacePluginAsync(old, candidate));
        await hmr.NotifyChangedAsync("disabled.dll");
        Assert.Null(loader!.Resolve("row").Fiber); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Failed_first_module_does_not_disturb_later_runtime()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        var original = new Plugin<string> { Apply = (_, _) => { } };
        var bad = new Plugin<string> { Apply = (_, _) => throw new InvalidOperationException("failed before second") };
        var loader = await MountAsync(root, original, "entry");
        var calls = 0;
        await root.RunAsync(async ctx => await ctx.Plugin(new Plugin<object?> { Apply = (_, _) => calls++ }).WaitAsync());
        hmr.RegisterModule("first.dll", () => loader.ReplacePluginAsync(original, bad));
        hmr.RegisterModule("second.dll", () => { calls++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => hmr.NotifyChangedAsync(["first.dll", "second.dll"]));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Earlier_activation_failure_does_not_prevent_replacement()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        var original = new Plugin<string> { Apply = (_, _) => throw new InvalidOperationException("earlier activation failed") };
        int calls = 0;
        var replacement = new Plugin<string> { Apply = (_, _) => calls++ };
        var loader = await MountAsync(root, original, "entry");
        hmr.RegisterModule("plugin.dll", () => loader.ReplacePluginAsync(original, replacement));
        await hmr.NotifyChangedAsync("plugin.dll"); Assert.Equal(1, calls);
        Assert.Equal(FiberState.Active, loader.Resolve("row").Fiber!.State);
    }

    [Fact]
    public async Task Rollback_failure_reports_but_preserves_original_replacement_error()
    {
        await using var root = new Context(); await using var hmr = new HmrCoordinator();
        var errors = new List<Exception>(); bool restoring = false;
        var original = new Plugin<string> { Apply = (_, _) => { if (restoring) throw new InvalidOperationException("restore failed"); } };
        var candidate = new Plugin<string> { Apply = (_, _) => throw new InvalidOperationException("replacement failed") };
        var loader = await MountAsync(root, original, "entry", errors.Add); restoring = true;
        hmr.RegisterModule("plugin.dll", () => loader.ReplacePluginAsync(original, candidate));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hmr.NotifyChangedAsync("plugin.dll"));
        Assert.Equal("replacement failed", error.Message); Assert.Contains(errors, item => item.Message == "restore failed");
    }

    [Fact]
    public async Task Explicit_dependency_graph_selects_dependents_and_framework_changes_take_precedence()
    {
        await using var hmr = new HmrCoordinator(); int replacements = 0, restarts = 0;
        hmr.RegisterModule("plugin.dll", () => { replacements++; return Task.CompletedTask; }, ["dependency.dll"]);
        hmr.RegisterFramework("framework.dll"); hmr.RestartHost = () => { restarts++; return Task.CompletedTask; };
        await hmr.NotifyChangedAsync("unrelated.dll"); Assert.Equal(0, replacements);
        await hmr.NotifyChangedAsync("dependency.dll"); Assert.Equal(1, replacements);
        await hmr.NotifyChangedAsync(["dependency.dll", "framework.dll"]); Assert.Equal(1, replacements); Assert.Equal(1, restarts);
    }

    private static async Task<Loader> MountAsync(Context context, IPlugin plugin, object? config, Action<Exception>? diagnostic = null)
    {
        Loader? loader = null;
        await context.RunAsync(async ctx =>
        {
            loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin), diagnostic: diagnostic);
            await loader.CreateAsync(new() { Id = "row", Name = "plugin", Config = config });
            await loader.WaitAsync();
        });
        return loader!;
    }
}
