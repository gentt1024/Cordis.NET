using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class SettingsControllerTests
{
    [Theory]
    [InlineData("success", null, 1, 1)]
    [InlineData("missing-provider", "gateway/internal", 0, 0)]
    [InlineData("missing-provider-cancel", "gateway/internal", 0, 0)]
    [InlineData("cancel-before", "gateway/cancelled", 0, 0)]
    [InlineData("prepare-failure", "gateway/internal", 1, 0)]
    [InlineData("prepare-cancel", "gateway/cancelled", 1, 0)]
    [InlineData("prepared-cancel", "gateway/cancelled", 1, 0)]
    [InlineData("open-failure", "gateway/internal", 1, 1)]
    [InlineData("open-cancel", "gateway/cancelled", 1, 1)]
    [InlineData("accepted-after-cancel", null, 1, 1)]
    public async Task Document_open_preserves_provider_order_cancellation_and_error_boundaries(
        string scenario,
        string? code,
        int preparations,
        int openings)
    {
        using var stop = new CancellationTokenSource();
        var prepared = 0;
        var opened = 0;
        var provider = new CountingProvider
        {
            Prepare = async () =>
            {
                prepared++;
                await Task.Yield();
                if (scenario is "prepare-cancel" or "prepared-cancel")
                    stop.Cancel();
                if (scenario is "prepare-failure" or "prepare-cancel")
                    throw new IOException("prepare refused");
                return "provider-owned-document";
            }
        };
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            if (scenario is not ("missing-provider" or "missing-provider-cancel"))
                ctx.Provide("settings", provider);
            var controller = new SettingsController(
                ctx,
                async (path, signal) =>
                {
                    Assert.Equal("provider-owned-document", path);
                    Assert.Equal(stop.Token, signal);
                    opened++;
                    await Task.Yield();
                    if (scenario is "open-cancel" or "accepted-after-cancel")
                        stop.Cancel();
                    if (scenario is "open-failure" or "open-cancel")
                        throw new IOException("open refused");
                });
            if (scenario is "cancel-before" or "missing-provider-cancel")
                stop.Cancel();
            if (code is null)
                Assert.True((await controller.OpenSettingsDocumentAsync(stop.Token)).Opened);
            else
            {
                var error = await Assert.ThrowsAsync<RemoteError>(() =>
                    controller.OpenSettingsDocumentAsync(stop.Token));
                Assert.Equal(code, error.Code);
                if (scenario is "prepare-failure" or "open-failure")
                    Assert.IsType<IOException>(error.InnerException);
            }
        });
        Assert.Equal(preparations, prepared);
        Assert.Equal(openings, opened);
    }

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

        internal Func<Task<string>> Prepare = () =>
            throw new InvalidOperationException("Unexpected document preparation.");

        public Task<string> PrepareDocumentAsync() => Prepare();

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
