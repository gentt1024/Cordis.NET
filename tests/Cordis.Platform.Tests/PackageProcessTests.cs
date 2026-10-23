using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text.Json;
using Cordis.Clr;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

[Collection("Collectible CLR")]
public sealed class PackageProcessTests : IAsyncLifetime
{
    private readonly string directory = Directory.CreateTempSubdirectory("cordis package process ").FullName;
    private string Feed => Path.Combine(directory, "feed");
    private string Helper => Path.Combine(directory, "helper", "bin", "Release", "net10.0", "ProcessHelper.dll");

    public async Task InitializeAsync()
    {
        var helper = Directory.CreateDirectory(Path.Combine(directory, "helper")).FullName;

        string Reference(string file) =>
            $"<Reference Include=\"{Path.GetFileNameWithoutExtension(file)}\"><HintPath>{SecurityElement.Escape(file)}</HintPath></Reference>";

        await File.WriteAllTextAsync(
            Path.Combine(helper, "ProcessHelper.csproj"),
            $$"""
              <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>{{Reference(typeof(ClrModuleResolver).Assembly.Location)}}{{Reference(typeof(Profiles).Assembly.Location)}}{{Reference(typeof(IPlugin).Assembly.Location)}}{{Reference(Path.Combine(AppContext.BaseDirectory, "YamlDotNet.dll"))}}</ItemGroup></Project>
              """);
        await File.WriteAllTextAsync(
            Path.Combine(helper, "Program.cs"),
            """
            using System.Diagnostics;
            using System.Runtime.InteropServices;
            using System.Text;
            using Cordis.Clr;
            using Cordis.Composition;
            if (args[0] == "linux-owner")
            {
                if (Native.Prctl(36, 1, 0, 0, 0) != 0) throw new Exception("Subreaper setup failed.");
                var record = Path.Combine(args[1], "process.json");
                foreach (var cancel in new[] { true, false })
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    try
                    {
                        var command = cancel
                            ? "sleep 1000 & printf 'ready\\n'; wait"
                            : "sleep 1000 >/dev/null 2>&1 & printf 'ready\\n'";
                        try
                        {
                            await DevelopmentBuildProcess.RunAsync("/bin/sh", ["-c", command], args[1], record,
                                _ => { if (cancel) stop.Cancel(); return Task.CompletedTask; }, stop.Token);
                            throw new Exception("An interrupted tool completed successfully.");
                        }
                        catch (PackageToolException error)
                        {
                            if (error.Cancelled != cancel) throw new Exception("Wrong cancellation classification: " + error);
                            if (!cancel && !error.Message.Contains("not fully drained")) throw;
                        }
                        if (File.Exists(record)) throw new Exception("Terminated children retained the run reservation.");
                        var children = Native.Children();
                        if (children.Length == 0) throw new Exception("The fixture did not retain unreaped descendants.");
                        foreach (var child in children)
                        {
                            var stat = File.ReadAllText($"/proc/{child}/stat");
                            if (stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0] != "Z")
                                throw new Exception("A child is still able to execute.");
                        }
                    }
                    finally
                    {
                        foreach (var child in Native.Children())
                        {
                            Native.Kill(child, 9);
                            Native.WaitPid(child, out _, 0);
                        }
                    }
                }
                return;
            }
            if (args[0] == "writer")
            {
                File.WriteAllText(Path.Combine(args[1], "writer-pulse.txt"), ".");
                File.WriteAllText(Path.Combine(args[1], "child.pid"), Environment.ProcessId.ToString());
                for (var index = 0; index < 120; index++)
                {
                    File.AppendAllText(Path.Combine(args[1], "writer-pulse.txt"), ".");
                    Thread.Sleep(250);
                }
                return;
            }
            if (args[0] == "launch")
            {
                // Do not inherit any SDK pipe handles. The child stays in the ordinary
                // parent job: there is no CREATE_BREAKAWAY_FROM_JOB or shell delegation.
                Process child;
                if (OperatingSystem.IsWindows())
                {
                    var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>() };
                    var command = new StringBuilder($"dotnet \"{typeof(Program).Assembly.Location}\" writer \"{args[1]}\"");
                    if (!Native.CreateProcess(null, command, 0, 0, false, 0x08000000, 0, null, ref startup, out var process))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                    Native.CloseHandle(process.Thread);
                    Native.CloseHandle(process.Process);
                    child = Process.GetProcessById(process.Id);
                }
                else
                {
                    var start = new ProcessStartInfo("dotnet")
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    foreach (var argument in new[] { typeof(Program).Assembly.Location, "writer", args[1] })
                        start.ArgumentList.Add(argument);
                    child = Process.Start(start)!;
                }
                using var ownedChild = child;
                var timer = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(args[1], "child.pid")))
                {
                    if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Writer did not start.");
                    Thread.Sleep(20);
                }
                if (args[2] == "wait") child.WaitForExit();
                return;
            }
            Profiles.Initialize(args[1], []);
            await using var resolver = new ClrModuleResolver(Path.Combine(args[1], "shadow"));
            using var toolchain = new DotnetPluginToolchain(args[1], resolver, [args[2]]);
            var inspection = await toolchain.InspectAsync(new("ProcessWriter", "2.0.0", args[2]));
            await toolchain.PrepareAsync(inspection, true, _ => { });
            static class Native
            {
                public static int[] Children() => File.ReadAllText($"/proc/self/task/{Environment.ProcessId}/children")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                [DllImport("libc", EntryPoint = "prctl")]
                public static extern int Prctl(int option, nuint value, nuint arg3, nuint arg4, nuint arg5);
                [DllImport("libc", EntryPoint = "waitpid")]
                public static extern int WaitPid(int pid, out int status, int options);
                [DllImport("libc", EntryPoint = "kill")]
                public static extern int Kill(int pid, int signal);
                [StructLayout(LayoutKind.Sequential)]
                public struct StartupInfo
                {
                    public int Size;
                    public nint Reserved, Desktop, Title;
                    public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
                    public short Show, ReservedSize;
                    public nint ReservedBytes, Input, Output, Error;
                }
                [StructLayout(LayoutKind.Sequential)]
                public struct ProcessInfo { public nint Process, Thread; public int Id, ThreadId; }
                [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                public static extern bool CreateProcess(string application, StringBuilder command, nint processSecurity, nint threadSecurity,
                    [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, int flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
                [DllImport("kernel32.dll")]
                public static extern bool CloseHandle(nint handle);
            }
            """);
        await RunAsync(helper, "build", "-c", "Release");
        var author = Directory.CreateDirectory(Path.Combine(directory, "author")).FullName;
        Directory.CreateDirectory(Feed);
        var tools = SecurityElement.Escape(Path.GetDirectoryName(Helper)!);
        await File.WriteAllTextAsync(
            Path.Combine(author, "ProcessWriter.csproj"),
            $$"""
              <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>ProcessWriter</PackageId><Version>1.0.0</Version></PropertyGroup>
              <ItemGroup><None Include="cordis.plugin.json" Pack="true" PackagePath="/" />
              <None Include="Launch.targets" Pack="true" PackagePath="buildTransitive/ProcessWriter.targets" />
              <None Include="{{tools}}/*" Pack="true" PackagePath="buildTransitive/tools/" />
              </ItemGroup></Project>
              """);
        await File.WriteAllTextAsync(Path.Combine(author, "Entry.cs"), "public sealed class Entry { }");
        await File.WriteAllTextAsync(
            Path.Combine(author, "cordis.plugin.json"),
            """{"assembly":"ProcessWriter.dll","entryType":"Entry"}""");
        await WriteTargetAsync(author, "leave");
        await RunAsync(author, "pack", "-c", "Release", "-o", Feed);
        await WriteTargetAsync(author, "wait");
        await RunAsync(author, "pack", "-c", "Release", "--no-build", "-o", Feed, "-p:Version=2.0.0");
    }

    [Fact]
    public async Task Exited_sdk_cannot_report_prepared_output_while_its_child_still_writes()
    {
        var profile = NewProfile();
        await using var resolver = new ClrModuleResolver(Path.Combine(profile, "shadow"));
        using var toolchain = new DotnetPluginToolchain(profile, resolver, [Feed]);
        try
        {
            var inspection = await toolchain.InspectAsync(new("ProcessWriter", "1.0.0", Feed));
            var error = await Record.ExceptionAsync(() => toolchain.PrepareAsync(
                inspection,
                true,
                _ =>
                {
                }));
            if (error is null)
            {
                var file = await WaitForWriterAsync(profile);
                using var writer = Process.GetProcessById(
                    int.Parse(await File.ReadAllTextAsync(file), System.Globalization.CultureInfo.InvariantCulture));
                Assert.False(
                    writer.HasExited,
                    "The red counterexample must still have a writer when preparation returns.");
            }

            var failure = Assert.IsType<PackageToolException>(error);
            Assert.Equal(0, failure.ExitCode);
            Assert.Contains("process", failure.Message, StringComparison.OrdinalIgnoreCase);
            await AssertWriterStoppedAsync(profile);
        }
        finally
        {
            StopResidualProcesses(profile);
        }
    }

    [Fact]
    public async Task Cancelled_preparation_stops_child_with_independent_output_pipes_before_returning()
    {
        if (OperatingSystem.IsLinux())
        {
            await RunAsync(directory, Helper, "linux-owner", directory);
            return;
        }

        var profile = NewProfile();
        await using var resolver = new ClrModuleResolver(Path.Combine(profile, "shadow"));
        using var toolchain = new DotnetPluginToolchain(profile, resolver, [Feed]);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var inspection = await toolchain.InspectAsync(new("ProcessWriter", "2.0.0", Feed));
            var pending = toolchain.PrepareAsync(
                inspection,
                true,
                _ =>
                {
                },
                cancellation.Token);
            await WaitForWriterAsync(profile);
            cancellation.Cancel();
            var failure =
                await Assert.ThrowsAsync<PackageToolException>(() => pending.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.True(failure.Cancelled, failure.Message);
            await AssertWriterStoppedAsync(profile);
        }
        finally
        {
            StopResidualProcesses(profile);
        }
    }

    [WindowsFact]
    public async Task Host_exit_terminates_owned_package_children_without_waiting_for_sdk_exit()
    {
        var profile = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { Helper, "host", profile, Feed })
            start.ArgumentList.Add(argument);
        using var host = Process.Start(start)!;
        var output = host.StandardOutput.ReadToEndAsync();
        var error = host.StandardError.ReadToEndAsync();
        try
        {
            var file = await WaitForWriterAsync(profile);
            var pid = int.Parse(await File.ReadAllTextAsync(file), System.Globalization.CultureInfo.InvariantCulture);
            using var writer = Process.GetProcessById(pid);
            // Capture the child identity before killing its owner. Closing the last job
            // handle initiates termination; the host's exit signal does not join its children.
            _ = writer.SafeHandle;
            host.Kill(entireProcessTree: false);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // The writer runs for 30 seconds naturally. A bounded wait still rejects an
            // orphan, without requiring Windows to signal parent and child in order.
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await AssertWriterStoppedAsync(profile);
        }
        finally
        {
            if (!host.HasExited)
                host.Kill(entireProcessTree: true);
            StopResidualProcesses(profile);
            await host.WaitForExitAsync();
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private string NewProfile()
    {
        var profile = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Profiles.Initialize(profile, []);
        return profile;
    }

    private static Task WriteTargetAsync(string author, string mode) =>
        File.WriteAllTextAsync(
            Path.Combine(author, "Launch.targets"),
            $"<Project><Target Name=\"StartIndependentWriter\" BeforeTargets=\"Build\"><Exec Command=\"dotnet &amp;quot;$(MSBuildThisFileDirectory)tools/ProcessHelper.dll&amp;quot; launch &amp;quot;$(MSBuildProjectDirectory)&amp;quot; {mode}\" /></Target></Project>"
                .Replace("&amp;quot;", "&quot;", StringComparison.Ordinal));

    private static async Task<string> WaitForWriterAsync(string profile)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (Directory.Exists(profile))
            {
                var file = Directory
                    .EnumerateFiles(profile, "child.pid", SearchOption.AllDirectories)
                    .SingleOrDefault();
                if (file is not null && new FileInfo(file).Length > 0)
                    return file;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Package writer did not start.");
    }

    private static async Task AssertWriterStoppedAsync(string profile)
    {
        var file = await WaitForWriterAsync(profile);
        var pid = int.Parse(await File.ReadAllTextAsync(file), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var writer = Process.GetProcessById(pid);
            Assert.True(writer.HasExited, "An owned package child is still running after completion.");
        }
        catch (ArgumentException)
        {
        }

        var pulse = Path.Combine(Path.GetDirectoryName(file)!, "writer-pulse.txt");
        var before = await File.ReadAllTextAsync(pulse);
        await Task.Delay(650);
        Assert.Equal(before, await File.ReadAllTextAsync(pulse));
    }

    private static void StopResidualProcesses(string profile)
    {
        // Cleanup also handles the deliberately broken implementation used for the red run.
        if (!Directory.Exists(profile))
            return;
        var pids = Directory
            .EnumerateFiles(profile, "child.pid", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Select(int.Parse)
            .ToList();
        var run = Path.Combine(profile, ".cordis", "package-run.json");
        if (File.Exists(run))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(run));
            if (json.RootElement.TryGetProperty("pid", out var pid))
                pids.Add(pid.GetInt32());
        }

        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private static async Task RunAsync(string path, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return Task.CompletedTask;
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Windows package process ownership regression.";
        }
    }
}

public sealed class PackageProcessReservationTests
{
    [Fact]
    public async Task Exited_root_keeps_descendant_and_pipe_ownership_until_both_have_settled()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-exited-root-").FullName;
        var path = Path.Combine(directory, "run.json");
        var lease = CreateLease(path);
        Process? process = null;
        object? owner = null;
        Task<string>? output = null, error = null;
        var recordingAttempted = false;
        var windows = OperatingSystem.IsWindows();

        void Stop()
        {
            if (windows && owner is not null)
                Invoke(owner, "Stop");
            else if (recordingAttempted)
                Invoke(lease, "StopGroup");
            else if (process is not null && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        try
        {
            await BeginAsync(path, CancellationToken.None);
            var start = new ProcessStartInfo(
                windows ? "cmd.exe" : OperatingSystem.IsLinux() ? "/usr/bin/setsid" : "/bin/sh")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (OperatingSystem.IsLinux())
                start.ArgumentList.Add("/bin/sh");
            foreach (var argument in windows
                         ? new[]
                         {
                             "/d", "/c", "start /b ping -n 1000 127.0.0.1 & echo root-output & 1>&2 echo root-error"
                         }
                         : new[] { "-c", "sleep 1000 & printf 'root-output\\n'; printf 'root-error\\n' >&2" })
                start.ArgumentList.Add(argument);
            if (windows || OperatingSystem.IsMacOS())
            {
                var type = typeof(DotnetPluginToolchain).Assembly.GetType(
                    windows ? "Cordis.Clr.WindowsPackageProcess" : "Cordis.Clr.MacPackageProcess",
                    throwOnError: true)!;
                owner = type.GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [start])!;
                process = Property<Process>(owner, "Process");
                // Widen the exact race deliberately: the real owner normally records before Resume.
                Invoke(owner, "Resume");
            }
            else
                process = Process.Start(start)!;

            var exited = OperatingSystem.IsMacOS()
                ? (Task)Invoke(owner!, "WaitForExitAsync", CancellationToken.None)!
                : process.WaitForExitAsync();
            await exited.WaitAsync(TimeSpan.FromSeconds(10));
            recordingAttempted = true;
            Invoke(lease, "Started", process, !windows, windows ? Property<string>(owner!, "JobName") : null);
            output = (owner is null ? process.StandardOutput : Property<StreamReader>(owner, "StandardOutput"))
                .ReadToEndAsync();
            error = (owner is null ? process.StandardError : Property<StreamReader>(owner, "StandardError"))
                .ReadToEndAsync();
            Assert.False(output.IsCompleted, "The descendant must still hold the inherited output pipe.");
            var recorded = await File.ReadAllTextAsync(path);
            var refusal = await Assert.ThrowsAsync<IOException>(() => BeginAsync(path, CancellationToken.None));
            Assert.Contains("still active", refusal.Message);
            Assert.Equal(recorded, await File.ReadAllTextAsync(path));
            Assert.Throws<IOException>(() => Invoke(lease, "Finish", true));
            Assert.Equal(recorded, await File.ReadAllTextAsync(path));

            Stop();
            var empty = windows
                ? (Task<bool>)Invoke(owner!, "WaitUntilEmptyAsync", TimeSpan.FromSeconds(5), CancellationToken.None)!
                : (Task<bool>)Invoke(lease, "WaitForGroupExitAsync", TimeSpan.FromSeconds(5))!;
            Assert.True(await empty);
            Assert.Throws<IOException>(() => Invoke(lease, "Finish", false));
            Assert.Equal(recorded, await File.ReadAllTextAsync(path));
            Assert.Contains("root-output", await output.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("root-error", await error.WaitAsync(TimeSpan.FromSeconds(5)));
            Invoke(lease, "Finish", true);
            Assert.False(File.Exists(path));

            // Older group/job records carry root start ticks. They still use the same actual owner.
            var legacy = (EntryOptions)ConfigurationFile.Parse(recorded, true)!;
            legacy["started"] = DateTime.UtcNow.Ticks;
            await File.WriteAllTextAsync(path, ConfigurationFile.Write(legacy, true));
            await BeginAsync(path, CancellationToken.None);
            Assert.Contains("starting", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Stop();
            if (output is not null)
                await output.WaitAsync(TimeSpan.FromSeconds(5));
            if (error is not null)
                await error.WaitAsync(TimeSpan.FromSeconds(5));
            if (owner is IDisposable disposable)
                disposable.Dispose();
            else
                process?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancelled_write_of_a_new_reservation_does_not_block_a_later_operation()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-run-reservation-").FullName;
        var path = Path.Combine(directory, "run.json");
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BeginAsync(path, cancelled.Token));
            // A retry must acquire a fresh reservation. No process was ever launched.
            await BeginAsync(path, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"state\":\"starting\"}")]
    public async Task Failed_takeover_preserves_an_existing_ambiguous_run_record(string contents)
    {
        var directory = Directory.CreateTempSubdirectory("cordis-run-reservation-").FullName;
        var path = Path.Combine(directory, "run.json");
        try
        {
            await File.WriteAllTextAsync(path, contents);
            Assert.NotNull(await Record.ExceptionAsync(() => BeginAsync(path, CancellationToken.None)));
            Assert.Equal(contents, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task BeginAsync(string path, CancellationToken token)
    {
        // Inject cancellation at the real ownership boundary, before any process exists.
        // Public preparation observes cancellation in several earlier async writes, so
        // racing a watcher against this point would not provide a deterministic regression.
        return (Task)Invoke(CreateLease(path), "BeginAsync", token)!;
    }

    private static object CreateLease(string path) =>
        typeof(DotnetPluginToolchain).Assembly.GetType("Cordis.Clr.PackageProcessLease", throwOnError: true)!
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [typeof(string)],
                null)!.Invoke([path]);

    private static T Property<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static object? Invoke(object instance, string name, params object?[] arguments)
    {
        try
        {
            return instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(instance, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
