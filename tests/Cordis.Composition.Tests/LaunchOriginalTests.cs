using Xunit;

namespace Cordis.Composition.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

// Adapts original app-boot environment/fatal-boundary assertions using caller-owned
// environment dictionaries and explicit Task error routing instead of global Node hooks.
[Collection("Process environment")]
public sealed class LaunchOriginalTests
{
    [Fact]
    public void OriginalResolveConfigPath()
    {
        var directory = Path.GetFullPath("base");
        Assert.Equal(Path.Combine(directory, "cordis.yml"), LaunchEnvironment.ResolveConfigPath("./cordis.yml", null, directory));
        Assert.Equal(Path.Combine(directory, "conf", "app.yaml"), LaunchEnvironment.ResolveConfigPath("conf/app.yaml", "record", directory));
        Assert.Equal(Path.Combine(directory, "cordis.snapshot.yml"), LaunchEnvironment.ResolveConfigPath("./cordis.yml", "replay", directory));
        Assert.Equal(Path.Combine(directory, "deep", "cordis.snapshot.yml"), LaunchEnvironment.ResolveConfigPath("deep/cordis.yaml", "replay", directory));
        Assert.Equal(Path.Combine(directory, "custom.yml"), LaunchEnvironment.ResolveConfigPath("custom.yml", "replay", directory));
        Assert.Equal(Path.GetFullPath("x.yml"), LaunchEnvironment.ResolveConfigPath("./x.yml"));
    }

    [Fact]
    public void SimpleLoadDefaultsAndReporter()
    {
        using var fixture = new Files();
        var originalDirectory = Environment.CurrentDirectory;
        var originalError = Console.Error;
        using var errors = new StringWriter();
        try
        {
            File.WriteAllText(fixture.ProjectFile, "DSH_TEST_DEFAULT=yes\n");
            Environment.CurrentDirectory = fixture.Project;
            var env = new Dictionary<string, string>();
            LaunchEnvironment.Load(env);
            Assert.Equal("yes", env["DSH_TEST_DEFAULT"]);
            File.Delete(fixture.ProjectFile); Directory.CreateDirectory(fixture.ProjectFile);
            Console.SetError(errors);
            LaunchEnvironment.Load(env, diagnosticName: "test");
            Assert.StartsWith("test: failed to load .env: ", errors.ToString());
            Assert.Single(errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally { Environment.CurrentDirectory = originalDirectory; Console.SetError(originalError); }
    }

    [Fact]
    public void HomeProxyValuesKeepCaseProvenanceAndInheritedPrecedence()
    {
        using var fixture = new Files();
        File.WriteAllText(fixture.HomeFile, "HTTP_PROXY=http://from-home:8080\nno_proxy=example.com\nHTTPS_PROXY=http://from-home:8443\n");
        var env = new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://exported:8080" };
        var snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, env);
        Assert.Equal(new("http://from-home:8080", "user-env", fixture.HomeFile), snapshot.Get("HTTP_PROXY"));
        Assert.Equal(new("example.com", "user-env", fixture.HomeFile), snapshot.Get("no_proxy"));
        Assert.Equal(new("http://exported:8080", "process"), snapshot.Get("HTTPS_PROXY"));
        Assert.Equal("http://from-home:8080", env["HTTP_PROXY"]);
        Assert.Equal("http://exported:8080", env["HTTPS_PROXY"]);
    }

    [Fact]
    public void ProjectProxyDiagnosticNamesHomeRemedy()
    {
        using var fixture = new Files();
        File.WriteAllText(fixture.ProjectFile, "HTTP_PROXY=http://attacker.example\n");
        var env = new Dictionary<string, string>();
        var error = Assert.Throws<InvalidOperationException>(() => LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, env));
        Assert.Contains($"export HTTP_PROXY, or put it in {fixture.HomeFile}, which does not travel with a repository", error.Message);
        Assert.False(env.ContainsKey("HTTP_PROXY"));
    }

    [Fact]
    public void SnapshotReportsBothAbsolutePathsAndFiltersSources()
    {
        using var fixture = new Files();
        File.WriteAllText(fixture.HomeFile, "USER_VALUE=u\n"); File.WriteAllText(fixture.ProjectFile, "PROJECT_VALUE=p\n");
        var env = new Dictionary<string, string>();
        var snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, env);
        Assert.Equal(new("u", "user-env", fixture.HomeFile), snapshot.Get("USER_VALUE"));
        Assert.Equal(new("p", "project-env", fixture.ProjectFile), snapshot.Get("PROJECT_VALUE"));
        Assert.Null(snapshot.GetFrom("PROJECT_VALUE", ["process", "user-env"]));
        Assert.Equal("u", env["USER_VALUE"]); Assert.Equal("p", env["PROJECT_VALUE"]);
    }

    [Fact]
    public void MissingAndUnreadableLayersContinueWithOneWarningAndDefaultSink()
    {
        using var fixture = new Files();
        File.WriteAllText(fixture.ProjectFile, "PROJECT_VALUE=project-only\n");
        var env = new Dictionary<string, string>(); var warnings = new List<string>();
        var snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, env, warnings.Add);
        Assert.Empty(warnings); Assert.Equal(new("project-only", "project-env", fixture.ProjectFile), snapshot.Get("PROJECT_VALUE"));
        Directory.CreateDirectory(fixture.HomeFile);
        env.Clear(); snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, env, warnings.Add, "test");
        Assert.StartsWith("test: failed to load .env", Assert.Single(warnings));
        Assert.Null(snapshot.Get("USER_VALUE")); Assert.Equal("project-only", env["PROJECT_VALUE"]);
        var originalError = Console.Error; using var error = new StringWriter();
        try
        {
            Console.SetError(error); env.Clear();
            snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, env, diagnosticName: "test");
            Assert.Contains("test: failed to load .env", error.ToString());
            Assert.Equal(new("project-only", "project-env", fixture.ProjectFile), snapshot.Get("PROJECT_VALUE"));
            Assert.Equal("project-only", env["PROJECT_VALUE"]);
        }
        finally { Console.SetError(originalError); }
    }

    [Fact]
    public void AbsentLayersCarryInheritedOnlyAndSameDirectoryIsOneProjectLayer()
    {
        using var fixture = new Files();
        var env = new Dictionary<string, string> { ["INHERITED"] = "shell" };
        var snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, env);
        Assert.Equal(new("shell", "process"), snapshot.Get("INHERITED"));
        File.WriteAllText(fixture.HomeFile, "PROJECT_VALUE=one-file\n");
        snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Home, env);
        Assert.Equal(new("one-file", "project-env", fixture.HomeFile), snapshot.Get("PROJECT_VALUE"));
        Assert.Null(snapshot.GetFrom("PROJECT_VALUE", ["user-env"]));
    }

    [Fact]
    public async Task FatalDiagnosticFormatsErrorsAndNonErrorsAndDisposalUnregistersBoundary()
    {
        foreach (object error in new object[] { new InvalidOperationException("boom"), "plain failure" })
        {
            var output = new List<string>(); var exits = new List<int>();
            using var guard = new FatalLoadGuard(output.Add, exits.Add, diagnosticName: "test");
            await guard.ReportAsync(error);
            Assert.Contains("test: fatal load failure: " + error, Assert.Single(output));
            Assert.Equal([1], exits);
        }
        var disabledOutput = new List<string>(); var disabledExits = new List<int>();
        var disabled = new FatalLoadGuard(disabledOutput.Add, disabledExits.Add);
        disabled.Dispose(); await disabled.ReportAsync(new Exception("ignored"));
        Assert.Empty(disabledOutput); Assert.Empty(disabledExits);
    }

    [Fact]
    public async Task FatalReleaseWaitsAndTimeoutIsBoundedWithoutSleeping()
    {
        var clock = new DeadlineClock(); var exits = new List<int>(); var output = new List<string>();
        using var guard = new FatalLoadGuard(output.Add, exits.Add, () => new TaskCompletionSource().Task, clock);
        var report = guard.ReportAsync(new Exception("never releases"));
        Assert.Empty(exits); Assert.Single(output);
        Assert.Equal(FatalLoadGuard.ReleaseTimeout, clock.Delay);
        clock.Fire(); await report;
        Assert.Equal([1], exits);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private Action? _fire;
        public TimeSpan Delay { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Delay = dueTime; _fire = () => callback(state); return new TimerHandle(); }
        public void Fire() => _fire!();
        private sealed class TimerHandle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Files : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cordis-launch-original-");
        public string Home { get; }
        public string Project { get; }
        public string HomeFile => Path.Combine(Home, ".env");
        public string ProjectFile => Path.Combine(Project, ".env");
        public Files() { Home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home")).FullName; Project = Directory.CreateDirectory(Path.Combine(_root.FullName, "project")).FullName; }
        public void Dispose() => _root.Delete(true);
    }
}
