using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class SettingsControllerTests
{
    [Theory]
    [InlineData("update", "patch", "{}", false)]
    [InlineData("replace", "section", "{}", false)]
    [InlineData("mutate", "ops", "[]", false)]
    [InlineData("update", "patch", "{}", true)]
    [InlineData("replace", "section", "{}", true)]
    [InlineData("mutate", "ops", "[]", true)]
    public async Task Empty_namespace_is_rejected_before_optional_provider_selection_or_execution(
        string method,
        string field,
        string input,
        bool provide)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var provider = new CountingProvider();
        await root.RunAsync(ctx =>
        {
            new SettingsController(ctx);
            registry.Register(ctx, SettingsControllerTypert.Contribution("Cordis.Tests.SettingsBoundary"));
            if (provide)
                ctx.Provide("settings", provider);
            return Task.CompletedTask;
        });
        using var request = JsonDocument.Parse("{\"ns\":\"\",\"" + field + "\":" + input + "}");
        var result = await new TypertGateway(root, registry).InvokeAsync("settings/" + method, request.RootElement);
        Assert.False(result.Ok);
        Assert.Equal("gateway/bad-request", result.Error?.Code);
        Assert.Equal(0, provider.Calls);
    }

    private sealed class CountingProvider : ISettingsProvider
    {
        internal int Calls;

        public Task<SettingsDescribeSnapshot> DescribeAsync() =>
            throw new InvalidOperationException("Unexpected describe.");

        public Task UpdateAsync(string ns, JsonElement patch, [DisallowNull] double? expectedRevision = null!) =>
            Write();

        public Task ReplaceAsync(string ns, JsonElement section, [DisallowNull] double? expectedRevision = null!) =>
            Write();

        public Task MutateAsync(
            string ns,
            IReadOnlyList<SettingsPathOp> ops,
            [DisallowNull] double? expectedRevision = null!) => Write();

        private Task Write()
        {
            Calls++;
            throw new InvalidOperationException("Provider code executed before namespace validation.");
        }
    }
}
