using Xunit;
using Cordis.Clr;

namespace Cordis.Platform.Tests;

public sealed class DevelopmentBuildProcessTests
{
    [Fact]
    public async Task Active_tool_keeps_its_recovery_record_until_cancellation_has_settled()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-tool-owner-").FullName;
        var record = Path.Combine(directory, "process.json");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/d", "/c", "echo ready & ping -n 1000 127.0.0.1 >nul"]
            : ["-c", "printf 'ready\\n'; sleep 1000"];
        var running = DevelopmentBuildProcess.RunAsync(
            command,
            arguments,
            directory,
            record,
            _ =>
            {
                ready.TrySetResult();
                return Task.CompletedTask;
            },
            stop.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var original = await File.ReadAllTextAsync(record);
            var refusal = await Assert.ThrowsAsync<IOException>(() => DevelopmentBuildProcess.RunAsync(
                "dotnet",
                ["--version"],
                directory,
                record,
                _ => Task.CompletedTask));
            Assert.Contains("still active", refusal.Message);
            Assert.Equal(original, await File.ReadAllTextAsync(record));
            stop.Cancel();
            var cancellation = await Assert.ThrowsAsync<Cordis.Composition.PackageToolException>(() => running);
            Assert.True(cancellation.Cancelled, cancellation.Message);
            Assert.False(File.Exists(record));
        }
        finally
        {
            stop.Cancel();
            await Record.ExceptionAsync(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Tool_exit_preserves_output_and_releases_its_run_record(int exitCode)
    {
        var directory = Directory.CreateTempSubdirectory("cordis tool 退出 ").FullName;
        var record = Path.Combine(directory, "process.json");
        var lines = new List<DevelopmentBuildLine>();
        try
        {
            var command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/d", "/c", $"echo tool-output& echo tool-error 1>&2& exit /b {exitCode}"]
                :
                [
                    "-c", "printf '%s\\n' \"$1\"; printf 'tool-error\\n' >&2; exit \"$2\"", "tool", "tool-output",
                    exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ];
            var error = await Record.ExceptionAsync(() => DevelopmentBuildProcess.RunAsync(
                command,
                arguments,
                directory,
                record,
                line =>
                {
                    lines.Add(line);
                    return Task.CompletedTask;
                }));
            if (exitCode == 0)
                Assert.Null(error);
            else
                Assert.Equal(exitCode, Assert.IsType<Cordis.Composition.PackageToolException>(error).ExitCode);
            Assert.Contains(new("tool-output", false), lines);
            Assert.Contains(new("tool-error", true), lines);
            Assert.False(File.Exists(record));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Output_consumer_failure_stops_real_development_process_and_cannot_complete_successfully()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-development-output-").FullName;
        var record = Path.Combine(directory, "process.json");
        try
        {
            var command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/d", "/c", "echo ready & ping -n 1000 127.0.0.1 >nul"]
                : ["-c", "printf 'ready\\n'; sleep 1000"];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var error = await Assert.ThrowsAsync<IOException>(() => DevelopmentBuildProcess.RunAsync(
                command,
                arguments,
                directory,
                record,
                _ => throw new InvalidDataException("Output consumer rejected the completed line."),
                deadline.Token));
            Assert.Contains("Output consumer rejected", error.ToString());
            // The existing owner only releases this record after process-tree exit and pipe settlement.
            Assert.False(File.Exists(record));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
