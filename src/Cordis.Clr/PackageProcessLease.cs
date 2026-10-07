using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cordis.Composition;

namespace Cordis.Clr;

// The profile lock can be released by a crashed host before its SDK process exits.
// This is a single run record, not a durable task/result store. An ambiguous record blocks takeover.
internal sealed class PackageProcessLease(string path)
{
    private int pid;
    private bool grouped;
    private string? windowsJob;

    internal async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            using var record = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var root = record.RootElement;
            if (!root.TryGetProperty("pid", out var id) || !id.TryGetInt32(out var previous) || previous <= 0 ||
                !root.TryGetProperty("started", out var started) || !started.TryGetInt64(out var ticks) ||
                !root.TryGetProperty("grouped", out var group) ||
                group.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new IOException(
                    "The prior package run is unresolved; inspect its processes before removing the run record.");
            var previousJob = root.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.String
                ? job.GetString()
                : null;
            if (Alive(previous, ticks, group.GetBoolean(), previousJob))
                throw new IOException(
                    $"Earlier package process {previous} is still active. Wait for it before retrying.");
            File.Delete(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await reservation.WriteAsync("{\"state\":\"starting\"}"u8.ToArray(), cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error)
        {
            // This handle was created exclusively here, and no process can start before
            // Begin completes. Failed takeover of an earlier record never enters this block.
            try
            {
                await reservation.DisposeAsync();
                File.Delete(path);
            }
            catch (Exception cleanup)
            {
                throw new IOException(
                    error.Message + " Recovery: could not remove the new run reservation " + path + ": " +
                    cleanup.Message,
                    error);
            }

            throw;
        }
    }

    internal void Started(Process process, bool processGroup, string? jobName = null)
    {
        pid = process.Id;
        grouped = processGroup;
        windowsJob = jobName;
        var temporary = path + ".tmp";
        var record = new EntryOptions
        {
            ["pid"] = pid,
            ["started"] = process.StartTime.ToUniversalTime().Ticks,
            ["grouped"] = grouped,
        };
        if (windowsJob is not null)
            record["job"] = windowsJob;
        File.WriteAllText(temporary, ConfigurationFile.Write(record, true));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }

    internal void NotStarted() => File.Delete(path);

    internal void StopGroup()
    {
        if (grouped && OperatingSystem.IsLinux() && pid > 0)
            Kill(-pid, 9);
    }

    internal void Finish(bool pipesDrained)
    {
        if (!pipesDrained || grouped && OperatingSystem.IsLinux() && Kill(-pid, 0) == 0 ||
            OperatingSystem.IsWindows() && (windowsJob is null || WindowsPackageProcess.IsAlive(windowsJob)))
        {
            // Keep ambiguous Windows descendants or a remaining POSIX group visible to a successor.
            if (!pipesDrained && !grouped && windowsJob is null)
                File.WriteAllText(path, "{\"state\":\"undrained\"}");
            throw new IOException("The package process has not fully drained; its run record remains for recovery.");
        }

        File.Delete(path);
    }

    private static bool Alive(int processId, long started, bool processGroup, string? job)
    {
        if (OperatingSystem.IsWindows())
        {
            // A root PID cannot establish that its descendants have stopped. Legacy or
            // incomplete records need explicit recovery rather than an unsafe takeover.
            if (job is null)
                throw new IOException(
                    "The prior Windows package run has no job identity; inspect its processes before removing the run record.");
            return WindowsPackageProcess.IsAlive(job);
        }

        if (processGroup && OperatingSystem.IsLinux())
            return Kill(-processId, 0) == 0 || Marshal.GetLastPInvokeError() == 1;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
