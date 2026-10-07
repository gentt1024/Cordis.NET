using Xunit;
using Cordis.Clr;

namespace Cordis.Platform.Tests;

public sealed class DevelopmentBuildProcessTests
{
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
