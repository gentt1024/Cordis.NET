using System.Diagnostics;
using System.Reflection;
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
        if (!OperatingSystem.IsWindows())
            return;
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
                var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>() };
                var command = new StringBuilder($"dotnet \"{typeof(Program).Assembly.Location}\" writer \"{args[1]}\"");
                if (!Native.CreateProcess(null, command, 0, 0, false, 0x08000000, 0, null, ref startup, out var process))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                Native.CloseHandle(process.Thread);
                Native.CloseHandle(process.Process);
                using var child = Process.GetProcessById(process.Id);
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

    [WindowsFact]
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

    [WindowsFact]
    public async Task Cancelled_preparation_stops_child_with_independent_output_pipes_before_returning()
    {
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
        var type = typeof(DotnetPluginToolchain).Assembly.GetType(
            "Cordis.Clr.PackageProcessLease",
            throwOnError: true)!;
        var lease = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [typeof(string)],
            null)!.Invoke([path]);
        return (Task)type.GetMethod("BeginAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            lease,
            [token])!;
    }
}
