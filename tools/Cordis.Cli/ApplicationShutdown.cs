/// <summary>Launcher-only shutdown deadline, matching the fixed upstream five-second grace.</summary>
/// <remarks>The timer begins at the first exit request, including requests made during activation.
/// It bounds cooperative plugin cleanup without imposing a timeout on ordinary startup.</remarks>
internal sealed class ApplicationShutdown : IDisposable
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<int> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Timer? deadline;
    private int exitCode;
    private bool disposed;

    internal Task<int> Requested => request.Task;

    internal void Request(int code)
    {
        lock (gate)
        {
            if (disposed || request.Task.IsCompleted) return;
            exitCode = code;
            deadline = new Timer(_ =>
            {
                Console.Error.WriteLine("Application shutdown exceeded its five-second grace; forcing process exit.");
                Environment.Exit(Volatile.Read(ref exitCode));
            }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
            request.TrySetResult(code);
        }
    }

    internal void Fail() => Volatile.Write(ref exitCode, 1);

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            deadline?.Dispose();
        }
    }
}
