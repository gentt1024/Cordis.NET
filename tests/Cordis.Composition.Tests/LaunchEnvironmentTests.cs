using Xunit;
namespace Cordis.Composition.Tests;

public sealed class LaunchEnvironmentTests
{
    [Theory]
    [InlineData("cordis.yml", "replay", "cordis.snapshot.yml")]
    [InlineData("deep/cordis.yaml", "replay", "deep/cordis.snapshot.yml")]
    [InlineData("custom.yml", "replay", "custom.yml")]
    [InlineData("cordis.yml", "record", "cordis.yml")]
    public void SnapshotPath(string input, string mode, string expected) => Assert.Equal(Path.GetFullPath(expected), LaunchEnvironment.ResolveConfigPath(input, mode));

    [Fact]
    public void EnvironmentLayersRetainProvenanceAndInheritedPrecedence()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Home, ".env"), "SHARED=user\nUSER=user-only\nINHERITED=loses\nHTTP_PROXY=http://home\n");
        File.WriteAllText(Path.Combine(fixture.Project, ".env"), "SHARED=project\nPROJECT=project-only\nINHERITED=loses\n");
        var environment = new Dictionary<string, string> { ["INHERITED"] = "shell" };
        var snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, environment);
        Assert.Equal("project", environment["SHARED"]); Assert.Equal("user-only", environment["USER"]);
        Assert.Equal("project-only", environment["PROJECT"]); Assert.Equal("shell", environment["INHERITED"]);
        Assert.Equal(new("http://home", "user-env", Path.Combine(fixture.Home, ".env")), snapshot.Get("HTTP_PROXY"));
        Assert.Null(snapshot.GetFrom("PROJECT", ["process", "user-env"]));
        environment["SHARED"] = "changed"; Assert.Equal("project", snapshot.Get("SHARED")!.Value);
    }

    [Theory]
    [InlineData("DSH_PERMISSION_MODE")]
    [InlineData("PATH")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("DSH_AGENTS_HOME")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("https_proxy")]
    [InlineData("BROWSER")]
    public void RefusesBootstrapNamesBeforeApplyingAnything(string key)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Project, ".env"), $"SAFE=value\n{key}=forbidden\n");
        var environment = new Dictionary<string, string>();
        Assert.Contains("only the launching environment may set", Assert.Throws<InvalidOperationException>(() => LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, environment)).Message);
        Assert.Empty(environment);
    }

    [Fact]
    public void HomeProxyExceptionDoesNotPermitTrustAndValidatesBothBeforeMaterialization()
    {
        using var fixture = new Fixture(); var env = new Dictionary<string, string>();
        File.WriteAllText(Path.Combine(fixture.Project, ".env"), "SAFE=value");
        File.WriteAllText(Path.Combine(fixture.Home, ".env"), "SSL_CERT_FILE=forbidden");
        Assert.Throws<InvalidOperationException>(() => LaunchEnvironment.LoadLayered(fixture.Home, fixture.Project, env)); Assert.Empty(env);
        File.WriteAllText(Path.Combine(fixture.Home, ".env"), "HTTP_PROXY=http://home");
        var snapshot = LaunchEnvironment.LoadLayered(fixture.Home, fixture.Home, env);
        Assert.Equal("http://home", snapshot.Get("HTTP_PROXY")!.Value); Assert.Equal("project-env", snapshot.Get("HTTP_PROXY")!.Source);
    }

    [Fact]
    public void MissingEnvironmentIsSilentAndReadFailureWarnsOnce()
    {
        using var fixture = new Fixture(); List<string> warnings = []; var env = new Dictionary<string, string>();
        LaunchEnvironment.Load(fixture.Project, env, warnings.Add); Assert.Empty(warnings);
        Directory.CreateDirectory(Path.Combine(fixture.Project, ".env"));
        LaunchEnvironment.Load(fixture.Project, env, warnings.Add, "test"); Assert.StartsWith("test: failed to load .env:", Assert.Single(warnings));
        File.WriteAllText(Path.Combine(fixture.Home, ".env"), "DSH_TEST=loaded");
        LaunchEnvironment.Load(fixture.Home, env); Assert.Equal("loaded", env["DSH_TEST"]);
    }

    [Fact]
    public void DotenvQuotesCommentsAndExports()
    {
        var values = LaunchEnvironment.Parse("# comment\nexport A = hello # suffix\nB='two\nlines'\nC=\"hello\\nworld\"\nD=first\nD=last\n");
        Assert.Equal("hello", values["A"]); Assert.Equal("two\nlines", values["B"]); Assert.Equal("hello\nworld", values["C"]); Assert.Equal("last", values["D"]);
    }

    [Fact]
    public async Task FatalBoundaryReportsBeforeReleaseCoalescesAndRetainsAssembledReasons()
    {
        List<string> order = []; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var guard = new FatalLoadGuard(_ => order.Add("diagnostic"), code => order.Add("exit:" + code), () => release.Task);
        var assembled = new Exception("assembled");
        using (guard.RetainAssembled(assembled)) { await guard.ReportAsync(assembled); Assert.Empty(order); }
        var first = guard.ReportAsync(new Exception("first")); await guard.ReportAsync(new Exception("second"));
        Assert.Equal(["diagnostic"], order); release.SetException(new Exception("cleanup")); await first;
        Assert.Equal(["diagnostic", "exit:1"], order);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cordis-env-");
        public string Home { get; }
        public string Project { get; }
        public Fixture() { Home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home")).FullName; Project = Directory.CreateDirectory(Path.Combine(_root.FullName, "project")).FullName; }
        public void Dispose() => _root.Delete(true);
    }
}
