namespace Cordis.Extensions;

/// <summary>Adapts external subscriptions to an existing Cordis execution domain and effect owner.</summary>
public static class ExternalCallbacks
{
    /// <summary>
    /// Subscribe an Action-based source while inside a Cordis callback or Context.RunAsync.
    /// The returned effect owns this registration, including callbacks queued before disposal.
    /// Synchronous notifications during subscription may run immediately. Disposal closes admission
    /// before unsubscribing; a later activation requires a new registration.
    /// </summary>
    /// <remarks>
    /// Already-started asynchronous callbacks may finish after disposal. This adapter neither cancels
    /// nor drains them, and it observes their eventual errors. The error sink is required because
    /// failures can arrive after the root has closed. Cancellation is sent only to the optional
    /// cancellation sink. Sinks can run on an external thread and must not throw; if they do, both
    /// errors are written to standard error. An external source must release any registration it
    /// creates before throwing from subscribe. Retaining its callback can retain plugin objects.
    /// </remarks>
    public static EffectHandle SubscribeExternal<T>(this Context context,
        Func<Action<T>, IDisposable> subscribe, Func<T, Task> callback,
        Action<Exception> reportError, Action<OperationCanceledException>? reportCancellation = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(reportError);
        var registration = new Registration<T>(context, callback, reportError, reportCancellation);
        // Finish effect setup before calling arbitrary code: subscribe may notify synchronously,
        // and that callback may reenter owner disposal. Cleanup must not await this subscription.
        var effect = context.Effect(() => (Action)registration.Close, "external subscription");
        try
        {
            registration.Attach(subscribe(registration.Notify)
                ?? throw new InvalidOperationException("The external source returned a null subscription."));
            return effect;
        }
        catch
        {
            _ = registration.RollbackAsync(effect);
            throw;
        }
    }

    private sealed class Registration<T>(Context context, Func<T, Task> callback,
        Action<Exception> reportError, Action<OperationCanceledException>? reportCancellation)
    {
        private int active = 1;
        private IDisposable? subscription;

        internal void Attach(IDisposable value)
        {
            // Attach and Close run in the owner domain, but subscribe can reenter that domain.
            if (Volatile.Read(ref active) == 0) value.Dispose();
            else subscription = value;
        }

        internal void Close()
        {
            if (Interlocked.Exchange(ref active, 0) == 0) return;
            var previous = subscription;
            subscription = null;
            previous?.Dispose();
        }

        internal void Notify(T value)
        {
            if (Volatile.Read(ref active) != 0) _ = DispatchAsync(value);
        }

        private async Task DispatchAsync(T value)
        {
            var entered = false;
            try
            {
                await context.RunAsync(async _ =>
                {
                    entered = true;
                    // Check at execution, not merely enqueue, and never reactivate this flag.
                    if (Volatile.Read(ref active) == 0
                        || context.Fiber.State is FiberState.Unloading or FiberState.Disposed) return;
                    await (callback(value)
                        ?? throw new InvalidOperationException("The external callback returned a null Task."));
                });
            }
            catch (ObjectDisposedException) when (!entered) { }
            catch (OperationCanceledException cancellation) { Report(reportCancellation, cancellation); }
            catch (Exception error) { Report(reportError, error); }
        }

        internal async Task RollbackAsync(EffectHandle effect)
        {
            try { await effect.DisposeAsync(); }
            catch (Exception error) { Report(reportError, error); }
        }

        private static void Report<TException>(Action<TException>? observer, TException error)
            where TException : Exception
        {
            try { observer?.Invoke(error); }
            catch (Exception observerError)
            {
                // The root may already be closed, so its logger is no longer an error outlet.
                try { Console.Error.WriteLine(new AggregateException(error, observerError)); }
                catch (Exception) { } // An unavailable host error stream cannot fault a detached task.
            }
        }
    }
}
