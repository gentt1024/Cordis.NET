using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class HmrDiagnosticTests
{
    public static TheoryData<object?> UnknownFailures => new()
    {
        null, "failure", new Dictionary<string, object?>(),
        new Dictionary<string, object?> { ["errors"] = null },
        new Dictionary<string, object?> { ["errors"] = new object?[] { null } },
        new Dictionary<string, object?> { ["errors"] = new object?[] { 7 } },
        new Dictionary<string, object?> { ["errors"] = new object?[] { new Dictionary<string, object?>() } },
        new Dictionary<string, object?> { ["errors"] = new object?[] { new Dictionary<string, object?> { ["text"] = 7 } } }
    };

    [Theory]
    [MemberData(nameof(UnknownFailures))]
    public async Task Unrecognized_failure_is_forwarded_without_losing_identity(object? error)
    {
        await using var context = new Context();
        var warnings = new List<object?>();
        await context.RunAsync(ctx =>
        {
            ctx.Logger.Exporter(new DelegateLogExporter(message => warnings.Add(Assert.Single(message.Arguments)), (int)LogLevel.Warn));
            HmrDiagnostics.Report(ctx, error);
            return Task.CompletedTask;
        });
        Assert.Same(error, Assert.Single(warnings));
    }

    [Fact]
    public void Compiler_locations_include_message_and_missing_source_reports_file_error()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-diagnostics-");
        try
        {
            var file = Path.Combine(directory.FullName, "broken.cs"); File.WriteAllText(file, "const broken = ;\n");
            var warnings = new List<object?>();
            HmrDiagnostics.Report(warnings.Add, new Dictionary<string, object?>
            {
                ["errors"] = new object?[]
            {
                new Dictionary<string, object?> { ["text"] = "without location" },
                new Dictionary<string, object?> { ["text"] = "unexpected token", ["location"] = new Dictionary<string, object?> { ["file"] = file, ["line"] = 1, ["column"] = 15 } },
                new Dictionary<string, object?> { ["text"] = "source disappeared", ["location"] = new Dictionary<string, object?> { ["file"] = Path.Combine(directory.FullName, "missing.cs"), ["line"] = 1, ["column"] = 1 } }
            }
            });
            Assert.Equal("without location", warnings[0]);
            var frame = Assert.IsType<string>(warnings[1]);
            Assert.Contains("unexpected token", frame); Assert.Contains("const broken = ;", frame); Assert.Contains($"{file}:1:15", frame);
            Assert.IsType<FileNotFoundException>(warnings[2]);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Watch_refresh_failure_reports_path_and_original_reason_then_recovers()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-errors-");
        try
        {
            ControlledWatcher? native = null;
            await using var hmr = new HmrCoordinator(path => native = new(path));
            var warnings = new List<object?>(); var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new Exception("42"); int calls = 0;
            hmr.Warning += warnings.Add; hmr.Error += _ => first.TrySetResult();
            var filename = Path.Combine(directory.FullName, "config.yml");
            await using var watch = hmr.WatchConfig(filename, () =>
            {
                if (++calls == 1) throw failure;
                second.TrySetResult(); return Task.CompletedTask;
            });
            native!.Change("config.yml"); await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(filename, Assert.IsType<string>(warnings[0])); Assert.Same(failure, warnings[1]);
            native.Change("config.yml"); await second.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, calls);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Native_compiler_adapter_diagnostics_flow_through_real_watch_error_channel()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-compiler-");
        try
        {
            ControlledWatcher? native = null;
            await using var hmr = new HmrCoordinator(path => native = new(path));
            var warning = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            hmr.Warning += value => { if (Equals(value, "compile failed")) warning.TrySetResult(value); };
            await using var watch = hmr.WatchConfig(Path.Combine(directory.FullName, "config.yml"),
                () => throw new HmrBuildFailureException([new("compile failed")]));
            native!.Change("config.yml");
            Assert.Equal("compile failed", await warning.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { directory.Delete(true); }
    }

    private sealed class ControlledWatcher(string directory) : FileSystemWatcher(directory)
    { public void Change(string name) => OnChanged(new(WatcherChangeTypes.Changed, Path, name)); }
}
