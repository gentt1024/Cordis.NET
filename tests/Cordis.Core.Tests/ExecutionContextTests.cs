using Xunit;

namespace Cordis.Core.Tests;

public sealed class ExecutionContextTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Concurrent_entries_keep_waking_the_context_and_remain_serial(int producers)
    {
        var context = new Context();
        const int entries = 50_000;
        int completed = 0;
        int active = 0;
        var tasks = Enumerable
            .Range(0, producers)
            .Select(_ => Task.Run(async () =>
            {
                for (int i = 0;i < entries;i++)
                {
                    await context.RunAsync(ctx =>
                    {
                        Assert.Equal(1, Interlocked.Increment(ref active));
                        completed++;
                        Assert.Equal(0, Interlocked.Decrement(ref active));
                        return Task.CompletedTask;
                    });
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(producers * entries, completed);
        await context.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
