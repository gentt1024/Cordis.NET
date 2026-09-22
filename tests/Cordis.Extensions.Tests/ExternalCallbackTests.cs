using System.Collections.Concurrent;
using Cordis.Extensions;
using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class ExternalCallbackTests
{
    [Fact]
    public async Task ForeignThreadCallbacksEnterOwnerDomainAndUnsubscribeClosesAdmissionFirst()
    {
        await using var root = new Context();
        Action<int> notify = null!;
        EffectHandle effect = null!;
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var disposed = 0;
        var errors = new ConcurrentQueue<Exception>();
        await root.RunAsync(ctx =>
        {
            effect = ctx.SubscribeExternal<int>(callback =>
            {
                notify = callback;
                return new Cleanup(() => { disposed++; callback(99); });
            }, value =>
            {
                ctx.Get<object>("optional", strict: false); // Throws if dispatch did not enter the domain.
                calls++;
                received.TrySetResult(value);
                return Task.CompletedTask;
            }, errors.Enqueue);
            return Task.CompletedTask;
        });
        OnForeignThread(() => notify(7));
        Assert.Equal(7, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await effect.DisposeAsync();
        OnForeignThread(() => notify(8));
        await root.RunAsync(_ => Task.CompletedTask);
        Assert.Equal(1, calls);
        Assert.Equal(1, disposed);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task QueuedCallbackChecksRegistrationAfterItWasDisposed()
    {
        await using var root = new Context();
        var calls = 0;
        var errors = new ConcurrentQueue<Exception>();
        await root.RunAsync(async ctx =>
        {
            Action<int> notify = null!;
            var effect = ctx.SubscribeExternal<int>(callback =>
            {
                notify = callback;
                return new Cleanup(() => { });
            }, _ => { calls++; return Task.CompletedTask; }, errors.Enqueue);
            // Keep this synchronous turn occupied until the foreign callback is queued.
            OnForeignThread(() => notify(1));
            await effect.DisposeAsync();
            await Task.Yield();
            Assert.Equal(0, calls);
        });
        Assert.Empty(errors);
    }

    [Fact]
    public async Task DependencyReactivationDoesNotReviveQueuedOrRetainedOldCallbacks()
    {
        await using var root = new Context();
        var callbacks = new List<Action<int>>();
        var values = new List<int>();
        var errors = new ConcurrentQueue<Exception>();
        var disposed = 0;
        await root.RunAsync(async ctx =>
        {
            var provider = ctx.Provide("dependency", new object());
            var consumer = ctx.Plugin(new Plugin<object?>
            {
                Inject = ["dependency"],
                Apply = (owner, _) => owner.SubscribeExternal<int>(callback =>
                {
                    callbacks.Add(callback);
                    return new Cleanup(() => disposed++);
                }, value => { values.Add(value); return Task.CompletedTask; }, errors.Enqueue),
            });
            await consumer.WaitAsync();
            Assert.Single(callbacks);
            OnForeignThread(() => callbacks[0](1));
            await provider.DisposeAsync();
            Assert.Equal(FiberState.Pending, consumer.State);
            ctx.Provide("dependency", new object());
            await consumer.WaitAsync();
            Assert.Equal(FiberState.Active, consumer.State);
            Assert.Equal(2, callbacks.Count);
            callbacks[0](2);
            callbacks[1](3);
            Assert.Equal([3], values);
            Assert.Equal(1, disposed);
        });
        Assert.Empty(errors);
    }

    [Fact]
    public async Task SynchronousSubscribeNotificationMayDisposeOwnerWithoutWaitingOnSetup()
    {
        await using var root = new Context();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var disposed = 0;
        await root.RunAsync(ctx =>
        {
            ctx.SubscribeExternal<int>(callback =>
            {
                Assert.Contains(ctx.Fiber.GetEffects(), effect => effect.Label == "external subscription");
                callback(1);
                return new Cleanup(() => disposed++);
            }, async _ =>
            {
                await ctx.Fiber.DisposeAsync();
                finished.TrySetResult();
            }, errors.Enqueue);
            return Task.CompletedTask;
        });
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, disposed);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ThrowingSubscribeRemovesEffectAndInvalidatesCapturedCallback()
    {
        await using var root = new Context();
        var errors = new ConcurrentQueue<Exception>();
        var failure = new InvalidOperationException("subscribe failed");
        var calls = 0;
        await root.RunAsync(ctx =>
        {
            Action<int> captured = null!;
            var thrown = Assert.Throws<InvalidOperationException>(() => ctx.SubscribeExternal<int>(callback =>
            {
                captured = callback;
                throw failure;
            }, _ => { calls++; return Task.CompletedTask; }, errors.Enqueue));
            Assert.Same(failure, thrown);
            Assert.Empty(ctx.Fiber.GetEffects());
            captured(1);
            Assert.Equal(0, calls);
            return Task.CompletedTask;
        });
        Assert.Empty(errors);
    }

    [Fact]
    public async Task NullSubscriptionRemovesEffectAndNullCallbackTaskReportsFailure()
    {
        await using var root = new Context();
        var errors = new List<Exception>();
        await root.RunAsync(ctx =>
        {
            Assert.Throws<InvalidOperationException>(() => ctx.SubscribeExternal<int>(_ => null!,
                _ => Task.CompletedTask, errors.Add));
            Assert.Empty(ctx.Fiber.GetEffects());
            ctx.SubscribeExternal<int>(callback =>
            {
                callback(1);
                return new Cleanup(() => { });
            }, _ => null!, errors.Add);
            Assert.Contains("null Task", Assert.Single(errors).Message);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StartedWorkIsNotDrainedAndErrorsAreObservedAfterRootClosure()
    {
        await using var root = new Context();
        var continueWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("late failure");
        var unsubscribed = false;
        await root.RunAsync(ctx =>
        {
            ctx.SubscribeExternal<int>(callback =>
            {
                callback(1);
                return new Cleanup(() => unsubscribed = true);
            }, async _ =>
            {
                started.SetResult();
                await continueWork.Task;
                throw failure;
            }, error => reported.TrySetResult(error));
            return Task.CompletedTask;
        });
        await started.Task;
        await root.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(unsubscribed);
        Assert.False(reported.Task.IsCompleted);
        continueWork.SetResult();
        Assert.Same(failure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CancellationHasSeparateOutcomeAndCallbackDisposalErrorsAreNotSilenced()
    {
        await using var root = new Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var errors = new List<Exception>();
        var cancellations = new List<OperationCanceledException>();
        await root.RunAsync(ctx =>
        {
            Action<int> notify = null!;
            ctx.SubscribeExternal<int>(callback =>
            {
                notify = callback;
                return new Cleanup(() => { });
            }, value => value switch
            {
                1 => Task.FromCanceled(cancellation.Token),
                _ => throw new ObjectDisposedException("callback-resource"),
            }, errors.Add, cancellations.Add);
            notify(1);
            notify(2);
            Assert.Equal(cancellation.Token, Assert.Single(cancellations).CancellationToken);
            Assert.IsType<ObjectDisposedException>(Assert.Single(errors));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FailedUnsubscribeStillRejectsItsRetainedCallback()
    {
        await using var root = new Context();
        var errors = new ConcurrentQueue<Exception>();
        var calls = 0;
        await root.RunAsync(async ctx =>
        {
            Action<int> notify = null!;
            var failure = new InvalidOperationException("unsubscribe failed");
            var effect = ctx.SubscribeExternal<int>(callback =>
            {
                notify = callback;
                return new Cleanup(() => throw failure);
            }, _ => { calls++; return Task.CompletedTask; }, errors.Enqueue);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => effect.DisposeAsync().AsTask()));
            notify(1);
            Assert.Equal(0, calls);
            Assert.Empty(ctx.Fiber.GetEffects());
        });
        Assert.Empty(errors);
    }

    [Fact]
    public async Task SubscriptionRequiresOwnerDomainBeforeCallingSource()
    {
        await using var root = new Context();
        var subscribed = false;
        Assert.Throws<InvalidOperationException>(() => root.SubscribeExternal<int>(_ =>
        {
            subscribed = true;
            return new Cleanup(() => { });
        }, _ => Task.CompletedTask, _ => { }));
        Assert.False(subscribed);
    }

    private static void OnForeignThread(Action operation)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            try { operation(); }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The external callback did not return.");
        if (failure is not null) throw failure;
    }

    private sealed class Cleanup(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
