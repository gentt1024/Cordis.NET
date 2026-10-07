using System.Diagnostics;
using System.Text;
using Cordis.Composition;

namespace Cordis.Clr;

internal static class DotnetPackageProcess
{
    internal static async Task RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        string directory,
        string runRecord,
        Action<string> output,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string, bool>? streamOutput = null)
    {
        var grouped = OperatingSystem.IsLinux();
        var start = new ProcessStartInfo(grouped ? "/usr/bin/setsid" : command)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (grouped)
            start.ArgumentList.Add(command);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        // Package builds receive a small runtime environment, not the host's service credentials.
        var inherited = new Dictionary<string, string?>();
        foreach (var name in new[]
                 {
                     "PATH",
                     "SystemRoot",
                     "WINDIR",
                     "TEMP",
                     "TMP",
                     "HOME",
                     "USERPROFILE",
                     "APPDATA",
                     "LOCALAPPDATA",
                     "DOTNET_ROOT",
                     "DOTNET_ROOT_X64",
                     "ProgramFiles",
                     "ProgramFiles(x86)"
                 })
            inherited[name] = Environment.GetEnvironmentVariable(name);
        start.Environment.Clear();
        foreach (var (name, value) in inherited)
            if (value is not null)
                start.Environment[name] = value;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using var fallback = new Process
        {
            StartInfo = start
        };
        var process = fallback;
        WindowsPackageProcess? windows = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var tail = new StringBuilder();
        var tailGate = new object();

        string OutputTail()
        {
            lock (tailGate)
                return tail.ToString();
        }

        var lease = new PackageProcessLease(runRecord);
        await lease.BeginAsync(cancellationToken);
        try
        {
            var started = false;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    windows = WindowsPackageProcess.Start(start);
                    process = windows.Process;
                }
                else
                    process.Start();

                started = true;
                lease.Started(process, grouped, OperatingSystem.IsWindows() ? windows?.JobName : null);
                if (OperatingSystem.IsWindows())
                    windows!.Resume();
            }
            catch (Exception error)
            {
                string? recovery = null;
                if (!started)
                    lease.NotStarted();
                else
                {
                    try
                    {
                        if (OperatingSystem.IsWindows())
                            windows!.Stop();
                        else if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception cleanup)
                    {
                        recovery = " Recovery: " + cleanup.Message;
                    }
                    // Recording failed after creation. Retain the reservation even after
                    // termination so a successor cannot infer an unrecorded process tree.
                }

                throw new PackageToolException(error.Message + recovery, directory);
            }

            async Task DrainAsync(StreamReader reader, bool isError)
            {
                var buffer = new char[2048];
                while (await reader.ReadAsync(buffer) is var length && length != 0)
                {
                    var chunk = new string(buffer, 0, length);
                    lock (tailGate)
                    {
                        tail.Append(chunk);
                        if (tail.Length > 16384)
                            tail.Remove(0, tail.Length - 16384);
                    }

                    try
                    {
                        output(chunk);
                    }
                    catch
                    {
                        /* Log consumers do not own process termination or its exit result. */
                    }

                    try
                    {
                        streamOutput?.Invoke(chunk, isError);
                    }
                    catch
                    {
                        /* A stream-specific observer has the same secondary ownership. */
                    }
                }
            }

            var drains = Task.WhenAll(
                DrainAsync(
                    OperatingSystem.IsWindows() ? windows!.StandardOutput : process.StandardOutput,
                    isError: false),
                DrainAsync(
                    OperatingSystem.IsWindows() ? windows!.StandardError : process.StandardError,
                    isError: true));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                if (OperatingSystem.IsWindows() &&
                    !await windows!.WaitUntilEmptyAsync(TimeSpan.FromSeconds(5), deadline.Token))
                    throw new IOException("The requested tool exited while child processes remained active.");
                // Pipe EOF alone does not establish process-tree exit. Both boundaries
                // must settle before output can be advertised as complete.
                await drains.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                lease.Finish(pipesDrained: true);
                if (process.ExitCode != 0)
                    throw new PackageToolException(
                        $"The requested tool exited with {process.ExitCode}: {OutputTail()}",
                        directory,
                        process.ExitCode);
            }
            catch (Exception error)
            {
                var recovery = new List<string>();
                var exited = false;
                var drained = false;
                var childrenExited = !OperatingSystem.IsWindows();
                try
                {
                    if (OperatingSystem.IsWindows())
                        windows!.Stop();
                    else
                    {
                        lease.StopGroup();
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }

                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    exited = true;
                    if (OperatingSystem.IsWindows())
                        childrenExited = await windows!.WaitUntilEmptyAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception cleanup)
                {
                    recovery.Add(cleanup.Message);
                }

                try
                {
                    await drains.WaitAsync(TimeSpan.FromSeconds(5));
                    drained = true;
                }
                catch (Exception cleanup)
                {
                    recovery.Add(cleanup.Message);
                }

                try
                {
                    lease.Finish(exited && childrenExited && drained);
                }
                catch (IOException cleanup)
                {
                    recovery.Add(cleanup.Message);
                }

                var exitCode = exited ? process.ExitCode : (int?)null;
                var cancellationRequested = cancellationToken.IsCancellationRequested;
                // "cancelled" is a settled management outcome, not merely a request.
                // Unconfirmed termination/drain remains a failure with its recovery record.
                var cancelled = cancellationRequested && recovery.Count == 0;
                var timedOut = !cancellationRequested && deadline.IsCancellationRequested;
                var message = cancellationRequested ? "Package preparation cancellation requested. " + OutputTail() :
                    timedOut ? $"Package preparation exceeded {timeout}. " + OutputTail() : error.Message;
                if (recovery.Count != 0)
                    message += " Recovery: " + string.Join("; ", recovery);
                throw new PackageToolException(message, directory, exitCode, cancelled, timedOut);
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                windows?.Dispose();
        }
    }
}
