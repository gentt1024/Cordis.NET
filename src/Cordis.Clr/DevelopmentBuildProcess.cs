using System.Text;
using System.Threading.Channels;

namespace Cordis.Clr;

/// <summary>One complete output line from an explicitly requested development compiler.</summary>
public sealed record DevelopmentBuildLine(string Text, bool StandardError);

/// <summary>Runs an optional development compiler under the existing package process owner.</summary>
/// <remarks>
/// The caller owns command approval and cancellation. This entry does not select plugins or publish artifacts.
/// Output callbacks run serially outside the process drains; failure or bounded-output overflow stops the process
/// and is reported after process-tree termination and pipe settlement. Callbacks must cooperate with the caller's
/// cancellation: this helper waits for an active callback to settle before completing. Ordinary C# plugin authors need no Node.
/// </remarks>
public static class DevelopmentBuildProcess
{
    /// <summary>Run until exit or cancellation, consuming complete stdout/stderr lines without mixing streams.</summary>
    public static async Task RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        string directory,
        string runRecord,
        Func<DevelopmentBuildLine, Task> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lines = Channel.CreateBounded<DevelopmentBuildLine>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        var buffers = new[] { new StringBuilder(), new StringBuilder() };
        var gate = new object();
        Exception? outputFailure = null;

        void Fail(Exception error)
        {
            lock (gate)
                outputFailure ??= error;
            stop.Cancel();
        }

        void Emit(string text, bool standardError)
        {
            if (!lines.Writer.TryWrite(new(text, standardError)))
                Fail(new IOException("Development output exceeded the bounded consumer queue."));
        }

        void Accumulate(string chunk, bool standardError)
        {
            lock (gate)
            {
                if (outputFailure is not null)
                    return;
                var buffer = buffers[standardError ? 1 : 0];
                foreach (var character in chunk)
                {
                    if (character == '\n')
                    {
                        Emit(buffer.ToString().TrimEnd('\r'), standardError);
                        buffer.Clear();
                    }
                    else
                    {
                        buffer.Append(character);
                    }

                    if (buffer.Length > 65536)
                    {
                        Fail(new IOException("Development output line exceeded 64 KiB."));
                        return;
                    }
                }
            }
        }

        async Task ConsumeAsync()
        {
            try
            {
                // Drain output until the producer completes, including after process cancellation.
#pragma warning disable CA2016
                await foreach (var line in lines.Reader.ReadAllAsync())
#pragma warning restore CA2016
                    await consume(line);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stop.Cancel();
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        var consumption = ConsumeAsync();
        Exception? processFailure = null;
        try
        {
            await DotnetPackageProcess.RunAsync(
                command,
                arguments,
                directory,
                runRecord,
                _ =>
                {
                },
                Timeout.InfiniteTimeSpan,
                stop.Token,
                Accumulate);
        }
        catch (Exception error)
        {
            processFailure = error;
        }
        finally
        {
            lock (gate)
                for (var index = 0;index < buffers.Length;index++)
                    if (buffers[index].Length > 0 && outputFailure is null)
                        Emit(buffers[index].ToString(), index == 1);
            lines.Writer.TryComplete();
        }

        await consumption;
        if (outputFailure is not null)
            throw new IOException(
                "Development compiler output was not consumed; no completion is implied.",
                processFailure is null ? outputFailure : new AggregateException(outputFailure, processFailure));
        if (processFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(processFailure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
