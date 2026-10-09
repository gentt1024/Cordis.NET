using System.Text.Json.Nodes;
using Cordis.Clr;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed partial class PackageManagementTests
{
    [Fact]
    public async Task Offline_deletion_refuses_an_active_profile_writer_then_succeeds_after_it_settles()
    {
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        Assert.Null(
            (await owner.InstallPackageAsync(
                host.Toolchain,
                new("IndependentPlugin", "1.0.0", Feed),
                "retained-before-writer",
                true,
                enabled: false)).Error);
        var retained = host.Toolchain.Bundles["IndependentPlugin"];
        var receipt = Path.Combine(Path.GetDirectoryName(retained)!, ".1.0.0.files.json");
        var assembly = await File.ReadAllBytesAsync(Path.Combine(retained, "IndependentPlugin.dll"));
        var receiptBytes = await File.ReadAllBytesAsync(receipt);
        Assert.Null((await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin")).Error);

        var document = await owner.ReadProfileAsync();
        var manifest = JsonNode.Parse(document.ManifestJson)!;
        manifest["description"] = "saved by the coordinated writer";
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.AdmitProfileAsync = async _ =>
        {
            admitted.SetResult();
            await proceed.Task;
        };
        var save = owner.SaveProfileMetadataAsync(manifest.ToJsonString(), document.Revision);
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAnyAsync<IOException>(() => DotnetPluginToolchain.DeleteRetainedArtifactAsync(
                host.Profile,
                "IndependentPlugin",
                "1.0.0"));
            Assert.Equal(assembly, await File.ReadAllBytesAsync(Path.Combine(retained, "IndependentPlugin.dll")));
            Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(receipt));
        }
        finally
        {
            proceed.TrySetResult();
            Assert.Null((await save).Error);
        }

        await DotnetPluginToolchain.DeleteRetainedArtifactAsync(host.Profile, "IndependentPlugin", "1.0.0");
        Assert.False(Directory.Exists(retained));
        Assert.False(File.Exists(receipt));
        Assert.Equal(
            "saved by the coordinated writer",
            PackageManifest.Read(Path.Combine(host.Profile, "package.json")).Raw["description"]);
    }

    [Theory]
    [InlineData("referenced")]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("invalid")]
    public async Task Offline_deletion_keeps_artifacts_without_a_readable_unreferenced_manifest(string state)
    {
        string profile;
        string retained;
        await using (var host = await StartAsync())
        {
            profile = host.Profile;
            Assert.Null(
                (await host.Session.ConfigurationOperations.InstallPackageAsync(
                    host.Toolchain,
                    new("IndependentPlugin", "1.0.0", Feed),
                    "retained-before-manifest-check",
                    true,
                    enabled: false)).Error);
            retained = host.Toolchain.Bundles["IndependentPlugin"];
        }

        var receipt = Path.Combine(Path.GetDirectoryName(retained)!, ".1.0.0.files.json");
        var assembly = await File.ReadAllBytesAsync(Path.Combine(retained, "IndependentPlugin.dll"));
        var receiptBytes = await File.ReadAllBytesAsync(receipt);
        var manifestPath = Path.Combine(profile, "package.json");
        if (state is "missing" or "unreadable")
            File.Delete(manifestPath);
        if (state == "unreadable")
            Directory.CreateDirectory(manifestPath);
        if (state == "invalid")
            await File.WriteAllTextAsync(manifestPath, "{");

        var failure = await Record.ExceptionAsync(() => DotnetPluginToolchain.DeleteRetainedArtifactAsync(
            profile,
            "IndependentPlugin",
            "1.0.0"));
        switch (state)
        {
            case "referenced":
                Assert.IsType<InvalidOperationException>(failure);
                Assert.Contains("still references", failure.Message, StringComparison.Ordinal);
                break;
            case "missing":
                Assert.IsType<FileNotFoundException>(failure);
                break;
            case "unreadable":
                Assert.IsType<UnauthorizedAccessException>(failure);
                break;
            case "invalid":
                Assert.IsAssignableFrom<System.Text.Json.JsonException>(failure);
                break;
        }

        Assert.Equal(assembly, await File.ReadAllBytesAsync(Path.Combine(retained, "IndependentPlugin.dll")));
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(receipt));
    }

    [Fact]
    public async Task Removal_admits_dependency_deletion_before_releasing_the_runtime_mapping()
    {
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        Assert.Null(
            (await owner.InstallPackageAsync(
                host.Toolchain,
                new("IndependentPlugin", "1.0.0", Feed),
                "before-removal-policy",
                true)).Error);
        var installedDirectory = host.Toolchain.Bundles["IndependentPlugin"];
        var admissions = 0;
        owner.AdmitProfileAsync = candidate =>
        {
            admissions++;
            if (JsonNode.Parse(candidate.ManifestJson)!["dependencies"]!["IndependentPlugin"] is null)
                throw new InvalidOperationException("dependency removal refused");
            return Task.CompletedTask;
        };
        var result = await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin");
        Assert.Equal(2, admissions);
        Assert.Contains("dependency removal refused", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(Directory.Exists(installedDirectory), System.Text.Json.JsonSerializer.Serialize(result));
        Assert.True(result.Installed);
        Assert.False(result.Selected);
        Assert.Equal("failed", result.Application);
        Assert.Contains(installedDirectory, result.Residuals!);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(host.Profile, "package.json")))!;
        Assert.Equal("1.0.0", manifest["dependencies"]!["IndependentPlugin"]!.GetValue<string>());
        Assert.Empty(manifest["dsh"]!["profile"]!["bundles"]!.AsArray());
    }

    [Theory]
    [InlineData("prepare", false)]
    [InlineData("admission", false)]
    [InlineData("publish", true)]
    [InlineData("publish-mapping", true)]
    [InlineData("published-content", true)]
    [InlineData("prepared-content", false)]
    public async Task Installation_rejects_unplanned_changes_without_overwriting_them(string boundary, bool published)
    {
        var mappings = new Dictionary<string, string>();
        await using var host = await StartAsync(installationBundles: mappings);
        var manifestPath = Path.Combine(host.Profile, "package.json");
        var before = await File.ReadAllTextAsync(manifestPath);
        string? edited = null;

        async Task Edit()
        {
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            manifest["description"] = "saved by another writer";
            edited = manifest.ToJsonString();
            await File.WriteAllTextAsync(manifestPath, edited);
        }

        var toolchain = new ObservedPackageToolchain(host.Toolchain);
        if (boundary == "prepare")
            toolchain.AfterPrepare = _ => Edit();
        if (boundary == "publish")
            toolchain.AfterPublish = _ => Edit();
        if (boundary == "published-content")
            toolchain.AfterPublish = package => File.WriteAllTextAsync(
                Path.Combine(package.PublicationDirectory!, "changed-after-approval.txt"),
                "unexpected");
        if (boundary == "publish-mapping")
            toolchain.AfterPublish = _ =>
            {
                mappings.Add("unexpected", Path.Combine(directory, "other"));
                return Task.CompletedTask;
            };
        host.Session.ConfigurationOperations.AdmitProfileAsync = async candidate =>
        {
            Assert.Contains("IndependentPlugin", candidate.ManifestJson, StringComparison.Ordinal);
            if (boundary == "admission")
                await Edit();
            if (boundary == "prepared-content")
                await File.WriteAllTextAsync(
                    Path.Combine(toolchain.Prepared!.Directory, "changed-after-approval.txt"),
                    "unexpected");
        };
        var result = await host.Session.ConfigurationOperations.InstallPackageAsync(
            toolchain,
            new("IndependentPlugin", "1.0.0", Feed),
            boundary,
            true);
        Assert.Equal("profile-conflict", result.Error);
        Assert.Equal("failed", result.Application);
        Assert.False(result.Installed);
        Assert.False(result.Selected);
        Assert.Equal(published ? 1 : 0, toolchain.Publications);
        Assert.Equal(edited ?? before, await File.ReadAllTextAsync(manifestPath));
        Assert.NotEmpty(result.Residuals!);
        Assert.All(result.Residuals!, path => Assert.True(Directory.Exists(path)));
    }

    [Fact]
    public async Task Publication_moves_and_registers_only_its_approved_candidate_and_metadata_save_checks_revision()
    {
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        string? admitted = null;
        owner.AdmitProfileAsync = candidate =>
        {
            admitted = candidate.ManifestJson;
            if (candidate.Package is { } package)
            {
                Assert.Equal("IndependentPlugin", package.Name);
                Assert.Equal("1.0.0", package.Version);
                Assert.True(File.Exists(Path.Combine(package.Directory, "package.json")));
                Assert.False(Directory.Exists(package.PublicationDirectory));
            }
            else
            {
                Assert.Contains("coordinated save", candidate.ManifestJson, StringComparison.Ordinal);
            }

            foreach (var layer in candidate.Composition.Layers)
                layer.Patches.Clear();
            return Task.CompletedTask;
        };
        var tools = new ObservedPackageToolchain(host.Toolchain)
        {
            AfterPrepare = package => File.WriteAllTextAsync(
                Path.Combine(package.Directory, "product-manifest.json"),
                "{\"product\":\"approved\"}")
        };
        var result = await owner.InstallPackageAsync(tools, new("IndependentPlugin", "1.0.0", Feed), "approved", true);
        Assert.True(result.Error is null, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.True(result.Installed);
        Assert.True(result.Selected);
        Assert.Equal("applied", result.Application);
        Assert.False(Directory.Exists(tools.Prepared!.Directory));
        Assert.True(Directory.Exists(tools.Prepared.PublicationDirectory));
        Assert.Equal(tools.Prepared.PublicationDirectory, host.Toolchain.Bundles["IndependentPlugin"]);
        Assert.Equal(
            "{\"product\":\"approved\"}",
            await File.ReadAllTextAsync(Path.Combine(tools.Prepared.PublicationDirectory!, "product-manifest.json")));
        Assert.Equal(admitted, await File.ReadAllTextAsync(Path.Combine(host.Profile, "package.json")));
        await host.Session.Context.RunAsync(ctx =>
        {
            var reference = ctx.Get<ConfigReference<int>>("installed-limit");
            Assert.NotNull(reference);
            Assert.Equal(1, reference.Value);
            return Task.CompletedTask;
        });
        var document = await owner.ReadProfileAsync();
        var edited = JsonNode.Parse(document.ManifestJson)!;
        edited["description"] = "coordinated save";
        var saved = await owner.SaveProfileMetadataAsync(edited.ToJsonString(), document.Revision);
        Assert.Null(saved.Error);
        var stale = await owner.SaveProfileMetadataAsync(document.ManifestJson, document.Revision);
        Assert.Equal("profile-conflict", stale.Error);
        Assert.Equal(
            "coordinated save",
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(host.Profile, "package.json")))!["description"]!
                .GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_save_waits_for_installation_then_rejects_its_old_revision(bool anotherOwner)
    {
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        var editor = anotherOwner
            ? new PluginConfigurationOperations(
                new ProfileLaunch(
                    await Profiles.LoadAsync(host.Profile, new Dictionary<string, string>(), host.Toolchain.Bundles),
                    Path.GetDirectoryName(host.Profile)!,
                    [],
                    new Dictionary<string, string>(),
                    host.Toolchain.Bundles),
                host.Session.Include)
            {
                RunExclusiveAsync = owner.RunExclusiveAsync
            }
            : owner;
        var document = await editor.ReadProfileAsync();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new ObservedPackageToolchain(host.Toolchain)
        {
            AfterPrepare = async _ =>
            {
                prepared.SetResult();
                await proceed.Task;
            }
        };
        var install = owner.InstallPackageAsync(tools, new("IndependentPlugin", "1.0.0", Feed), "waiting", true);
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(90));
        var edit = JsonNode.Parse(document.ManifestJson)!;
        edit["description"] = "pending draft";
        var save = editor.SaveProfileMetadataAsync(edit.ToJsonString(), document.Revision);
        Assert.False(save.IsCompleted);
        proceed.SetResult();
        Assert.Null((await install).Error);
        Assert.Equal("profile-conflict", (await save).Error);
        Assert.Contains(
            "IndependentPlugin",
            await File.ReadAllTextAsync(Path.Combine(host.Profile, "package.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removal_does_not_replace_its_baseline_after_the_tool_changes_other_inputs()
    {
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        Assert.Null(
            (await owner.InstallPackageAsync(
                host.Toolchain,
                new("IndependentPlugin", "1.0.0", Feed),
                "before-removal",
                true)).Error);
        var path = Path.Combine(host.Profile, "package.json");
        string? edited = null;
        var tools = new ObservedPackageToolchain(host.Toolchain)
        {
            AfterRemove = async _ =>
            {
                var value = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                value["description"] = "saved while removing";
                edited = value.ToJsonString();
                await File.WriteAllTextAsync(path, edited);
            }
        };
        var result = await owner.RemovePackageAsync(tools, "IndependentPlugin");
        Assert.Equal("profile-conflict", result.Error);
        Assert.True(result.Installed);
        Assert.False(result.Selected);
        Assert.Equal("failed", result.Application);
        Assert.Equal(edited, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Product_rejection_prevents_publication_and_repeated_selection_cannot_bypass_admission()
    {
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        owner.AdmitProfileAsync = _ => throw new InvalidOperationException("product refused candidate");
        var tools = new ObservedPackageToolchain(host.Toolchain);
        var rejected = await owner.InstallPackageAsync(
            tools,
            new("IndependentPlugin", "1.0.0", Feed),
            "rejected",
            true);
        Assert.False(rejected.Installed);
        Assert.False(rejected.Selected);
        Assert.Equal(0, tools.Publications);
        Assert.Contains("product refused", rejected.Diagnostic, StringComparison.Ordinal);
        owner.AdmitProfileAsync = null;
        var installed = await owner.InstallPackageAsync(
            host.Toolchain,
            new("IndependentPlugin", "1.0.0", Feed),
            "accepted",
            true);
        Assert.Null(installed.Error);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(host.Profile)!, "cordis.patch.yml"),
            "- id: independentplugin\n  config:\n    limit: 2\n");
        var calls = 0;
        owner.AdmitProfileAsync = _ =>
        {
            calls++;
            throw new InvalidOperationException("selection refused");
        };
        var selection = await owner.SetBundleEnabledAsync("IndependentPlugin", true);
        Assert.Equal(1, calls);
        Assert.Equal("failed", selection.Application);
        Assert.Contains("selection refused", selection.Diagnostic, StringComparison.Ordinal);
        await host.Session.Context.RunAsync(ctx =>
        {
            var reference = ctx.Get<ConfigReference<int>>("installed-limit");
            Assert.NotNull(reference);
            Assert.Equal(1, reference.Value);
            return Task.CompletedTask;
        });
    }

    private sealed class ObservedPackageToolchain(IProfilePackageToolchain inner) : IProfilePackageToolchain
    {
        public Func<PreparedPackage, Task>? AfterPrepare
        {
            get;
            set;
        }

        public Func<PreparedPackage, Task>? AfterPublish
        {
            get;
            set;
        }

        public Func<string, Task>? AfterRemove
        {
            get;
            init;
        }

        public PreparedPackage? Prepared
        {
            get;
            private set;
        }

        public int Publications
        {
            get;
            private set;
        }

        public IReadOnlyList<string> Sources => inner.Sources;
        public string ResolvePackageName(string name) => inner.ResolvePackageName(name);
        public IReadOnlyList<string> GetRetainedDirectories(string name) => inner.GetRetainedDirectories(name);

        public Task<IReadOnlyList<string>> VersionsAsync(
            string name,
            string source,
            CancellationToken cancellationToken = default) => inner.VersionsAsync(name, source, cancellationToken);

        public Task<PackageInspection> InspectAsync(
            PackageRequest request,
            CancellationToken cancellationToken = default) => inner.InspectAsync(request, cancellationToken);

        public async Task<PreparedPackage> PrepareAsync(
            PackageInspection inspection,
            bool buildApproved,
            Action<string> output,
            CancellationToken cancellationToken = default)
        {
            Prepared = await inner.PrepareAsync(inspection, buildApproved, output, cancellationToken);
            if (AfterPrepare is { } observe)
                await observe(Prepared);
            return Prepared;
        }

        public async Task PublishAsync(PreparedPackage package, CancellationToken cancellationToken = default)
        {
            await inner.PublishAsync(package, cancellationToken);
            Publications++;
            if (AfterPublish is { } observe)
                await observe(package);
        }

        public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
        {
            await inner.RemoveAsync(name, cancellationToken);
            if (AfterRemove is { } observe)
                await observe(name);
        }
    }
}
