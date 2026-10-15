using Cordis.Clr;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed class ClrAdmissionTests
{
    [Theory]
    [InlineData("AdmissionEntry", false, false)]
    [InlineData("AdmissionFailureEntry", false, false)]
    [InlineData("AdmissionSchemaFailureEntry", false, false)]
    [InlineData("AdmissionEntry", true, false)]
    [InlineData("AdmissionEntry", false, true)]
    public async Task Admission_precedes_switching_and_caches_the_actual_candidate(
        string entry,
        bool accept,
        bool cancel)
    {
        await using var root = new Context();
        await using var resolver = new ClrModuleResolver();
        using var cancellation = new CancellationTokenSource();
        var uri = new Uri("file:///");
        var artifacts = (ITypertArtifactResolver)resolver;
        var oldDefinition = Definition("v1", "AdmissionEntry");
        resolver.Register("one", oldDefinition);
        resolver.Register("alias", oldDefinition);
        resolver.Register("inactive", Definition("v1", "ImmutableEntry"));
        resolver.Register("other", Definition("other-v1", "AdmissionEntry"));
        var old = await resolver.ResolveAsync("one", uri);
        var other = await resolver.ResolveAsync("other", uri);
        var previous = await artifacts.ResolveAsync("one", uri);
        await root.RunAsync(async owner =>
        {
            var registry = new TypertRegistry(root);
            var oldRegistration = registry.Register(owner, previous!);
            Fiber? active = null;
            await root.RunAsync(async context =>
            {
                active = context.Plugin(old);
                await active.WaitAsync();
            });
            var oldIdentity = root.Get<string>("candidate-identity");
            ClrReplacementCandidate? escaped = null;
            TypertContribution? read = null;
            var switched = false;
            var rejection = new InvalidOperationException("Application refused candidate.");
            var failure = await Record.ExceptionAsync(async () => await resolver.ReplaceAsync(
                new Dictionary<string, ClrModuleDefinition>
                {
                    ["one"] = Definition("v2", entry),
                    ["alias"] = Definition("v2", entry),
                    ["inactive"] = Definition("v2", "ImmutableEntry")
                },
                async pairs =>
                {
                    switched = true;
                    Assert.Throws<ObjectDisposedException>(() => escaped!.GetContribution("one"));
                    await oldRegistration.DisposeAsync();
                    await active!.DisposeAsync();
                    await root.RunAsync(async context =>
                    {
                        active = context.Plugin(pairs[old]);
                        await active.WaitAsync();
                    });
                },
                async candidate =>
                {
                    escaped = candidate;
                    Assert.Equal(new[] { "one", "alias", "inactive" }, candidate.Specifiers);
                    Assert.Same(old, await resolver.ResolveAsync("one", uri));
                    Assert.Same(previous, await artifacts.ResolveAsync("one", uri));
                    Assert.Same(other, await resolver.ResolveAsync("other", uri));
                    Assert.Equal(oldIdentity, root.Get<string>("candidate-identity"));
                    Assert.NotNull(registry.GetPackage(previous!.Package));
                    Assert.Null(candidate.GetContribution("inactive"));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.DisposeAsync().AsTask());
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        resolver
                            .ReplaceAsync("one", Definition("v2", entry), (_, _) => ValueTask.CompletedTask)
                            .AsTask());
                    read = candidate.GetContribution("one");
                    Assert.Same(read, candidate.GetContribution("alias"));
                    foreach (var schema in read!.Schemas)
                        _ = schema.Create();
                    if (cancel)
                        cancellation.Cancel();
                    else if (!accept)
                        throw rejection;
                },
                cancellation.Token));
            Assert.NotNull(escaped);
            Assert.Throws<ObjectDisposedException>(() => escaped.GetContribution("one"));
            Assert.Same(other, await resolver.ResolveAsync("other", uri));
            if (accept)
            {
                Assert.Null(failure);
                Assert.True(switched);
                Assert.Same(read, await artifacts.ResolveAsync("one", uri));
                Assert.Same(read, await artifacts.ResolveAsync("alias", uri));
                Assert.Equal(read!.Package, root.Get<string>("candidate-identity"));
            }
            else
            {
                Assert.False(switched);
                if (cancel)
                    Assert.IsAssignableFrom<OperationCanceledException>(failure);
                else if (entry == "AdmissionEntry")
                    Assert.Same(rejection, failure);
                else
                    Assert.Equal(
                        entry == "AdmissionFailureEntry"
                            ? "Candidate contribution refused."
                            : "Candidate schema refused.",
                        failure!.Message);
                Assert.Same(old, await resolver.ResolveAsync("one", uri));
                Assert.Equal(oldIdentity, root.Get<string>("candidate-identity"));
                Assert.NotNull(registry.GetPackage(previous!.Package));
            }

            Assert.True(Assert.Single(resolver.Unloads).UnloadRequested);
            await oldRegistration.DisposeAsync();
        });
        await root.DisposeAsync();
    }

    private static ClrModuleDefinition Definition(string version, string entry) =>
        new(
            Path.Combine(AppContext.BaseDirectory, "fixtures", version),
            "VersionedPlugin.dll",
            "VersionedPlugin." + entry);
}
