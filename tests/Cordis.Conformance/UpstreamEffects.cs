// Adapted from cordiverse/cordis@56b3d4f725681cf4556c1a8695a709cc3b6eed74
// packages/core/tests/dispose.spec.ts, describe('Effects'). MIT, Shigma.
// These five named cases retain all reachable assertions from their source cases.
// They must be executed against the DSH-patched implementation, not the old upstream runtime.
using Cordis;

namespace Cordis.Conformance;

public static partial class Scenarios
{
    private static Task<string[]> UpstreamDisposeByPlugin() => InContext(async (root, log) =>
    {
        int calls = 0;
        Fiber fiber = root.Plugin(Plugin(ctx => ctx.Effect(() => () => calls++, "test")));
        await fiber.WaitAsync();
        IReadOnlyList<EffectMetadata> effects = fiber.GetEffects();
        Equal(1, effects.Count);
        Equal("test", effects[0].Label);
        Equal(0, effects[0].Children.Count);
        Equal(0, calls);
        await fiber.DisposeAsync();
        Equal(1, calls);
        await fiber.DisposeAsync();
        Equal(1, calls);
        log.Add("metadata:test;calls:0,1,1");
    });

    private static Task<string[]> UpstreamDisposeManually() => InContext(async (root, log) =>
    {
        int calls = 0;
        EffectHandle disposer = root.Effect(() => () => calls++);
        IReadOnlyList<EffectMetadata> effects = root.Fiber.GetEffects();
        Equal(1, effects.Count);
        Equal("anonymous", effects[0].Label);
        Equal(0, effects[0].Children.Count);
        Equal(0, calls);
        ValueTask first = disposer.DisposeAsync();
        Equal(1, calls); // Original test deliberately checks BEFORE awaiting.
        ValueTask second = disposer.DisposeAsync();
        Equal(1, calls);
        await first;
        await second;
        log.Add("metadata:anonymous;calls:0,1,1");
    });


    private static Task<string[]> UpstreamReturnWithError() => InContext((root, log) =>
    {
        var sequence = new List<int>();
        InvalidOperationException error = Throws<InvalidOperationException>(() => root.Effect(
            (Func<Action>)(() => throw new InvalidOperationException("test"))));
        Equal("test", error.Message);
        Equal(0, sequence.Count);
        log.Add("throws;sequence:empty");
        return Task.CompletedTask;
    });

    private static Task<string[]> UpstreamYieldWithError() => InContext((root, log) =>
    {
        var sequence = new List<int>();
        IEnumerable<IAsyncDisposable> Setup()
        {
            yield return new AsyncCallback(() => { sequence.Add(1); return Task.CompletedTask; });
            throw new InvalidOperationException("test");
        }
        InvalidOperationException error = Throws<InvalidOperationException>(() => root.Effect(Setup));
        Equal("test", error.Message);
        Equal("1", string.Join(',', sequence)); // Rollback is observable before the call returns.
        log.Add("throws;sequence:1");
        return Task.CompletedTask;
    });

    private static Task<string[]> UpstreamAsyncReturnWithError() => InContext(async (root, log) =>
    {
        var sequence = new List<int>();
        EffectHandle effect = root.Effect(() => Task.FromException<IAsyncDisposable>(
            new InvalidOperationException("test")));
        Equal(0, sequence.Count);
        await ThrowsAsync<InvalidOperationException>(() => effect.Ready);
        Equal(0, sequence.Count);
        log.Add("rejects;sequence:empty");
    });
}
