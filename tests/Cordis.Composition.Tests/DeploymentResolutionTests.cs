using System.Text.Json;
using Cordis;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class DeploymentResolutionTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cordis-deployment-" + Guid.NewGuid().ToString("N"));
        public Dictionary<(string Anchor, string Name), string> Edges { get; } = [];
        public string Profiles => Path.Combine(Root, "profiles");
        public string Active => Path.Combine(Profiles, "active");
        public string Installation => Path.Combine(Root, "install", "package.json");

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Package("install", "app");
            Package("profiles/active", "profile");
        }

        public string Package(string relative, string name, string version = "1.0.0", string[]? dependencies = null, string[]? peers = null)
        {
            var directory = Path.GetFullPath(Path.Combine(Root, relative));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "package.json"), JsonSerializer.Serialize(new Dictionary<string, object?> { ["name"] = name, ["version"] = version, ["dependencies"] = (dependencies ?? []).ToDictionary(n => n, _ => "*"), ["peerDependencies"] = (peers ?? []).ToDictionary(n => n, _ => "*") }));
            return directory;
        }

        public string? Resolve(string anchor, string name) => Edges.GetValueOrDefault((Path.GetFullPath(anchor), name));
        public void Edge(string from, string name, string to) => Edges[(Path.Combine(from, "package.json"), name)] = to;
        public Profile Profile(params string[] bundles) => new("active", Active, bundles.Select(path => new Bundle(Path.GetFileName(path), path, Path.Combine(path, "cordis.patch.yml"), [])).ToArray(), new(Path.Combine(Active, "cordis.patch.yml"), []));
        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public void ComputesImmutableGraphWithoutMaterialization()
    {
        using var f = new Fixture();
        var app = f.Package("install", "app", dependencies: ["lib"]);
        var lib = f.Package("lib", "lib");
        f.Edge(app, "lib", lib);
        var generation = DeploymentGeneration.Create(f.Installation, f.Profiles, f.Profile(), f.Resolve);
        var entry = Assert.Single(generation.Entries, e => e.Name == "lib");
        Assert.Equal(lib, entry.Directory);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal(f.Installation, entry.Declarer);
        Assert.Equal(DeploymentPackageScope.Installation, entry.Scope);
        Assert.False(Directory.Exists(Path.Combine(f.Profiles, "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(f.Active, ".dsh-module-fallback")));
        Assert.Throws<NotSupportedException>(() => ((IList<DeploymentEntry>)generation.Entries).Add(entry));
        var installationOnly = DeploymentGeneration.Create(f.Installation, f.Profiles, null, f.Resolve);
        Assert.Null(installationOnly.ProfileDirectory);
        Assert.Empty(installationOnly.LocalPackageNames);
        Assert.Equal(lib, new DeploymentPackageResolver(installationOnly).PackageDirectory("lib", new Uri(Path.Combine(f.Profiles, "entry.cs"))));
    }

    [Fact]
    public void EarlierRootCompletesBeforeLaterRootsAndIncludesPeers()
    {
        using var f = new Fixture();
        var app = f.Package("install", "app", dependencies: ["bridge", "missing"]);
        var bridge = f.Package("bridge", "bridge", dependencies: ["choice"], peers: ["peer"]);
        var choice = f.Package("first-choice", "choice");
        var peer = f.Package("peer", "peer", "3.0.0");
        f.Edge(app, "bridge", bridge);
        f.Edge(bridge, "choice", choice);
        f.Edge(bridge, "peer", peer);
        var bundle = f.Package("bundle", "bundle", dependencies: ["choice", "bundle-bridge"]);
        var otherChoice = f.Package("other-choice", "choice", "2.0.0");
        var bundleBridge = f.Package("bundle-bridge", "bundle-bridge", dependencies: ["bundle-choice"]);
        var first = f.Package("bundle-first", "bundle-choice");
        f.Edge(bundle, "choice", otherChoice);
        f.Edge(bundle, "bundle-bridge", bundleBridge);
        f.Edge(bundleBridge, "bundle-choice", first);
        var later = f.Package("later", "later", dependencies: ["bundle-choice"]);
        var second = f.Package("bundle-second", "bundle-choice", "2.0.0");
        f.Edge(later, "bundle-choice", second);
        var generation = DeploymentGeneration.Create(f.Installation, f.Profiles, f.Profile(bundle, later), f.Resolve);
        Assert.Equal(choice, generation.Entries.Single(e => e.Name == "choice").Directory);
        Assert.Equal(peer, generation.Entries.Single(e => e.Name == "peer").Directory);
        Assert.Equal(first, generation.Entries.Single(e => e.Name == "bundle-choice").Directory);
        Assert.Equal(DeploymentPackageScope.Profile, generation.Entries.Single(e => e.Name == "bundle-choice").Scope);
        Assert.DoesNotContain(generation.Entries, e => e.Name is "bundle" or "later" or "missing");
    }

    [Fact]
    public void ProfileFallbackIsRestrictedToActiveProfileAndLocalPackagesWin()
    {
        using var f = new Fixture();
        var installed = f.Package("installed", "lib");
        var bundled = f.Package("bundled", "bundle-lib");
        var local = f.Package("local", "lib", "2.0.0");
        var generation = new DeploymentGeneration(f.Profiles, f.Active, [new("lib", installed, "1", f.Installation, DeploymentPackageScope.Installation), new("bundle-lib", bundled, "1", f.Installation, DeploymentPackageScope.Profile)], new Dictionary<string, string> { { "lib", local } });
        var resolver = new DeploymentPackageResolver(generation);
        var active = new Uri(Path.Combine(f.Active, "entry.cs"));
        var other = new Uri(Path.Combine(f.Profiles, "other", "entry.cs"));
        Assert.Equal(local, resolver.PackageDirectory("lib", active));
        Assert.Equal(installed, resolver.PackageDirectory("lib", other));
        Assert.Equal(bundled, resolver.PackageDirectory("bundle-lib", active));
        Assert.Null(resolver.PackageDirectory("bundle-lib", other));
    }

    [Fact]
    public void AdditiveReplacementInvalidatesMissAndMetadataCacheAtomically()
    {
        using var f = new Fixture();
        var original = f.Package("original", "lib");
        var added = f.Package("added", "added-lib", "2.0.0");
        var first = new DeploymentGeneration(f.Profiles, f.Active, [new("lib", original, "1.0.0", f.Installation, DeploymentPackageScope.Installation)]);
        var resolver = new DeploymentPackageResolver(first);
        var parent = new Uri(Path.Combine(f.Active, "entry.cs"));
        Assert.Null(resolver.PackageOf("added-lib", parent));
        var metadata = resolver.PackageOf("lib/private", parent);
        Assert.Same(metadata, resolver.PackageOf("lib", parent));
        var next = new DeploymentGeneration(f.Profiles, f.Active, first.Entries.Append(new("added-lib", added, "2.0.0", f.Installation, DeploymentPackageScope.Installation)));
        resolver.Replace(next);
        Assert.Equal(added, resolver.PackageDirectory("added-lib", parent));
        Assert.Equal("added-lib", resolver.PackageOf("added-lib", parent)!.Name);
        Assert.Equal("2.0.0", resolver.PackageOf("added-lib", parent)!.Version);
        Assert.NotSame(metadata, resolver.PackageOf("lib", parent));
        Assert.Same(next, resolver.Generation);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("version")]
    [InlineData("declarer")]
    [InlineData("scope")]
    [InlineData("remove")]
    [InlineData("profile")]
    [InlineData("local-override")]
    public void ChangedMappingRequiresRestartWithoutPublishing(string change)
    {
        using var f = new Fixture();
        var directory = f.Package("lib", "lib");
        var original = new DeploymentEntry("lib", directory, "1", f.Installation, DeploymentPackageScope.Installation);
        var first = new DeploymentGeneration(f.Profiles, f.Active, [original]);
        var resolver = new DeploymentPackageResolver(first);
        var altered = change switch
        {
            "directory" => original with
            {
                Directory = f.Root
            },
            "version" => original with
            {
                Version = "2"
            },
            "declarer" => original with
            {
                Declarer = Path.Combine(f.Root, "other.json")
            },
            "scope" => original with
            {
                Scope = DeploymentPackageScope.Profile
            },
            _ => original
        };
        var next = new DeploymentGeneration(change == "profile" ? Path.Combine(f.Root, "different") : f.Profiles, f.Active, change == "remove" ? [] : [altered], change == "local-override" ? new Dictionary<string, string> { { "lib", directory } } : null);
        Assert.Throws<DeploymentRestartRequiredException>(() => resolver.Replace(next));
        Assert.Same(first, resolver.Generation);
        Assert.Equal(directory, resolver.PackageDirectory("lib", new Uri(Path.Combine(f.Active, "entry.cs"))));
    }

    [Fact]
    public void NewLocalPackageCanBeAddedButNotRemoved()
    {
        using var f = new Fixture();
        var first = new DeploymentGeneration(f.Profiles, f.Active, []);
        var resolver = new DeploymentPackageResolver(first);
        var local = f.Package("new-local", "new-local");
        var next = new DeploymentGeneration(f.Profiles, f.Active, [], new Dictionary<string, string> { { "new-local", local } });
        resolver.Replace(next);
        resolver.Replace(next);
        Assert.Throws<DeploymentRestartRequiredException>(() => resolver.Replace(first));
        Assert.Same(next, resolver.Generation);
    }

    [Fact]
    public void MalformedSuccessorLeavesPublishedGenerationAndWritesNothing()
    {
        using var f = new Fixture();
        var first = DeploymentGeneration.Create(f.Installation, f.Profiles, f.Profile(), f.Resolve);
        var resolver = new DeploymentPackageResolver(first);
        File.WriteAllText(Path.Combine(f.Active, "package.json"), "{");
        Assert.ThrowsAny<JsonException>(() => DeploymentGeneration.Create(f.Installation, f.Profiles, f.Profile(), f.Resolve));
        Assert.Same(first, resolver.Generation);
        Assert.False(Directory.Exists(Path.Combine(f.Profiles, "node_modules")));
    }

    [Fact]
    public void MetadataUsesSelectedPackageWithoutRequiringPrivateExport()
    {
        using var f = new Fixture();
        var directory = f.Package("metadata", "metadata-lib", "2.0.0");
        var resolver = new DeploymentPackageResolver(new(f.Profiles, f.Active, [new("metadata-lib", directory, "2.0.0", f.Installation, DeploymentPackageScope.Installation)]));
        var parent = new Uri(Path.Combine(f.Active, "entry.cs"));
        var package = resolver.PackageOf("metadata-lib/private", parent)!;
        Assert.Equal("metadata-lib", package.Name);
        Assert.Equal("2.0.0", package.Version);
        Assert.Equal(directory, package.Directory);
        Assert.Equal(Path.Combine(directory, "package.json"), package.ManifestPath);
        Assert.Null(resolver.PackageOf("node:fs", parent));
        Assert.Null(resolver.PackageOf("./local.cs", parent));
        Assert.Null(resolver.PackageOf("@scope", parent));
        Assert.Null(resolver.PackageOf("missing", parent));
        File.WriteAllText(package.ManifestPath, "{");
        Assert.Same(package, resolver.PackageOf("metadata-lib", parent));
        resolver.Replace(resolver.Generation);
        Assert.ThrowsAny<JsonException>(() => resolver.PackageOf("metadata-lib", parent));
    }

    [Fact]
    public void OutsideAndAncestorLookupsDelegateWithoutRevivingStaleFallback()
    {
        using var f = new Fixture();
        var outside = f.Package("outside", "outside-lib");
        var above = f.Package("above", "ancestor-lib");
        var stale = f.Package("profiles/node_modules/stale", "stale");
        var resolver = new DeploymentPackageResolver(new(f.Profiles, f.Active, []), native: (name, _) => name == "outside-lib" ? outside : null, ancestor: (name, _) => name == "ancestor-lib" ? above : null);
        Assert.Equal(outside, resolver.PackageDirectory("outside-lib", new Uri(Path.Combine(f.Root, "external", "entry.cs"))));
        var scoped = new Uri(Path.Combine(f.Active, "entry.cs"));
        Assert.Equal(above, resolver.PackageDirectory("ancestor-lib", scoped));
        Assert.Null(resolver.PackageDirectory("stale", scoped));
        Assert.True(Directory.Exists(stale));
        var withAncestor = new DeploymentPackageResolver(resolver.Generation, ancestor: (name, _) => name == "stale" ? above : null);
        Assert.Equal(above, withAncestor.PackageDirectory("stale", scoped));
        Assert.True(Directory.Exists(stale));
    }

    [Fact]
    public async Task ModuleAndMetadataResolutionSharePackageSelection()
    {
        using var f = new Fixture();
        var directory = f.Package("module", "plugin-package");
        var packages = new DeploymentPackageResolver(new(f.Profiles, f.Active, [new("plugin-package", directory, "1.0.0", f.Installation, DeploymentPackageScope.Installation)]));
        var expected = new Plugin<object?>
        {
            Apply = (_, _) =>
            {
            }
        };
        var modules = new StaticModuleResolver().Register("deployed:plugin", expected);
        var resolver = new DeploymentModuleResolver(packages, modules).Register(directory, ".", "deployed:plugin");
        var parent = new Uri(Path.Combine(f.Active, "entry.cs"));
        Assert.Same(expected, await resolver.ResolveAsync("plugin-package", parent));
        Assert.Equal(directory, packages.PackageOf("plugin-package/private", parent)!.Directory);
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await resolver.ResolveAsync("plugin-package/private", parent));
    }

    [Fact]
    public void NativeMetadataCachesAndUsesFallbackName()
    {
        using var f = new Fixture();
        var directory = f.Package("native", "ignored");
        File.WriteAllText(Path.Combine(directory, "package.json"), "{\"name\":4}");
        var resolver = DeploymentPackageResolver.Native((name, _) => name == "@scope/metadata" ? directory : null);
        var parent = new Uri(Path.Combine(f.Root, "entry.cs"));
        var first = resolver.PackageOf("@scope/metadata/private", parent)!;
        Assert.Equal("@scope/metadata", first.Name);
        Assert.Null(first.Version);
        Assert.Equal(directory, first.Directory);
        Assert.Same(first, resolver.PackageOf("@scope/metadata", parent));
        Assert.Null(resolver.PackageOf("missing", parent));
        Assert.Throws<InvalidOperationException>(() => resolver.Replace(new(f.Profiles, f.Active, [])));
    }

    [Fact]
    public void MissingSelectedManifestDoesNotFallThrough()
    {
        using var f = new Fixture();
        var missing = Path.Combine(f.Root, "missing");
        var fallback = f.Package("fallback", "lib");
        var resolver = new DeploymentPackageResolver(new(f.Profiles, f.Active, [new("lib", missing, "1", f.Installation, DeploymentPackageScope.Installation)]), ancestor: (_, _) => fallback);
        var parent = new Uri(Path.Combine(f.Active, "entry.cs"));
        Assert.Equal(missing, resolver.PackageDirectory("lib", parent));
        Assert.Null(resolver.PackageOf("lib", parent));
    }

    [Fact]
    public void CanonicalAliasDoesNotRequireRestart()
    {
        using var f = new Fixture();
        var directory = f.Package("lib", "lib");
        var alias = Path.Combine(f.Root, "alias");
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(f.Root, "junction.ps1");
            File.WriteAllText(script, "param([string]$Link,[string]$Target)\nNew-Item -ItemType Junction -Path $Link -Target $Target | Out-Null");
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                script,
                alias,
                directory
            }

            )
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
            Directory.CreateSymbolicLink(alias, directory);
        var first = new DeploymentGeneration(f.Profiles, f.Active, [new("lib", directory, "1", Path.Combine(directory, "package.json"), DeploymentPackageScope.Installation)]);
        var resolver = new DeploymentPackageResolver(first);
        var next = new DeploymentGeneration(f.Profiles, f.Active, [new("lib", alias, "1", Path.Combine(alias, "package.json"), DeploymentPackageScope.Installation)]);
        try
        {
            resolver.Replace(next);
            Assert.Same(next, resolver.Generation);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Fact]
    public void ApplicationOwnedProfileOutsideSharedDirectoryStillRoutes()
    {
        using var f = new Fixture();
        var directory = f.Package("lib", "lib");
        var owned = Path.Combine(f.Root, "application", "owned-profile");
        var resolver = new DeploymentPackageResolver(new(f.Profiles, owned, [new("lib", directory, "1", f.Installation, DeploymentPackageScope.Profile)]));
        Assert.Equal(directory, resolver.PackageDirectory("lib", new Uri(Path.Combine(owned, "entry.cs"))));
        Assert.Null(resolver.PackageDirectory("lib", new Uri(Path.Combine(f.Active, "entry.cs"))));
    }

    [Fact]
    public void ObservesLateLocalPackageAfterMissAndKeepsGenerationBeforeAncestor()
    {
        using var f = new Fixture();
        var selected = f.Package("selected", "lib");
        var higher = f.Package("higher", "lib", "2");
        string? late = null;
        var resolver = new DeploymentPackageResolver(new(f.Profiles, f.Active, [new("lib", selected, "1", f.Installation, DeploymentPackageScope.Installation)]), local: (name, _) => name == "late" ? late : null, ancestor: (name, _) => name == "lib" ? higher : null);
        var parent = new Uri(Path.Combine(f.Active, "entry.cs"));
        Assert.Null(resolver.PackageDirectory("late", parent));
        Assert.Null(resolver.PackageDirectory("legacy-missing/subpath", parent));
        late = f.Package("late", "late");
        Assert.Equal(late, resolver.PackageDirectory("late", parent));
        Assert.Equal(late, resolver.PackageDirectory("late/subpath", parent));
        Assert.Equal(selected, resolver.PackageDirectory("lib", parent));
    }

    [Theory]
    [InlineData("different")]
    [InlineData("only-generation")]
    [InlineData("only-deployment")]
    [InlineData("same")]
    [InlineData("neither")]
    public void VerificationComparesBothMetadataBackends(string mode)
    {
        using var f = new Fixture();
        var chosen = f.Package("chosen", "lib");
        var another = f.Package("another", "lib");
        var generation = new DeploymentGeneration(f.Profiles, f.Active, mode is "only-deployment" or "neither" ? [] : [new("lib", chosen, "1", f.Installation, DeploymentPackageScope.Installation)]);
        var resolver = new DeploymentPackageResolver(generation, native: (_, _) => mode switch
        {
            "different" => another,
            "same" or "only-deployment" => chosen,
            _ => null
        }, behavior: DeploymentResolutionBehavior.Verify);
        var parent = new Uri(Path.Combine(f.Active, "entry.cs"));
        if (mode == "same")
            Assert.Equal(chosen, resolver.PackageOf("lib", parent)!.Directory);
        else if (mode == "neither")
            Assert.Null(resolver.PackageOf("lib", parent));
        else
            Assert.Throws<DeploymentResolutionMismatchException>(() => resolver.PackageOf("lib", parent));
    }

    [Fact]
    public void DeployedLocalSelectionOwnsMalformedMetadataFailure()
    {
        using var f = new Fixture();
        var local = f.Package("local", "lib");
        var fallback = f.Package("fallback", "lib");
        File.WriteAllText(Path.Combine(local, "package.json"), "{");
        var resolver = new DeploymentPackageResolver(new(f.Profiles, f.Active, [new("lib", fallback, "1", f.Installation, DeploymentPackageScope.Installation)], new Dictionary<string, string> { { "lib", local } }));
        Assert.ThrowsAny<JsonException>(() => resolver.PackageOf("lib", new Uri(Path.Combine(f.Active, "entry.cs"))));
    }

    [Fact]
    public void MetadataBoundaryInputsAndRepeatedOutsideLookups()
    {
        using var f = new Fixture();
        var selected = f.Package("selected", "resolution-lib");
        var outside = f.Package("outside", "outside-lib");
        var scoped = f.Package("scoped", "@scope/outside");
        var ancestor = f.Package("ancestor", "ancestor-lib");
        var resolver = new DeploymentPackageResolver(new(f.Profiles, f.Active, [new("resolution-lib", selected, "1.0.0", f.Installation, DeploymentPackageScope.Installation)]), native: (name, _) => name switch
        {
            "outside-lib" => outside,
            "@scope/outside" => scoped,
            _ => null
        }, ancestor: (name, _) => name == "ancestor-lib" ? ancestor : null);
        var parent = new Uri(Path.Combine(f.Active, "entry.cs"));
        foreach (var request in new[]
        {
            "",
            "./local.js",
            "/absolute.js",
            "\\\\server\\share",
            "#internal",
            "@scope",
            "node:fs"
        }

        )
            Assert.Null(resolver.PackageDirectory(request, parent));
        Assert.Equal(selected, resolver.PackageDirectory("resolution-lib/private", parent));
        var outsideParent = new Uri(Path.Combine(f.Root, "elsewhere", "entry.cs"));
        for (var repeat = 0; repeat < 2; repeat++)
        {
            Assert.Equal(outside, resolver.PackageDirectory("outside-lib", outsideParent));
            Assert.Equal(ancestor, resolver.PackageDirectory("ancestor-lib", parent));
        }

        Assert.Equal(scoped, resolver.PackageDirectory("@scope/outside", outsideParent));
        Assert.Equal(scoped, resolver.PackageDirectory("@scope/outside/private", outsideParent));
        Assert.Null(resolver.PackageDirectory("missing", new Uri(parent, "%ZZ")));
        var verified = new DeploymentPackageResolver(resolver.Generation, native: (name, _) => name == "resolution-lib" ? selected : null, behavior: DeploymentResolutionBehavior.Verify);
        Assert.Equal(selected, verified.PackageDirectory("resolution-lib", parent));
        Assert.Null(verified.PackageDirectory("missing-metadata", parent));
        Assert.Null(verified.PackageDirectory("node:fs", parent));
        Assert.Null(verified.PackageDirectory("missing-metadata", new Uri(parent, "%ZZ")));
    }

    [Fact]
    public void LocalPackageNamesAreDeclaredExistingAndImmutable()
    {
        using var f = new Fixture();
        f.Package("profiles/active", "profile", dependencies: ["resolution-lib", "missing"], peers: ["linked-local"]);
        var local = f.Package("local", "resolution-lib");
        var linked = f.Package("linked", "linked-local");
        var notDeclared = f.Package("undeclared", "undeclared");
        var locals = new Dictionary<string, string>
        {
            ["resolution-lib"] = local,
            ["linked-local"] = linked,
            ["missing"] = Path.Combine(f.Root, "missing"),
            ["undeclared"] = notDeclared
        };
        var generation = DeploymentGeneration.Create(f.Installation, f.Profiles, f.Profile(), f.Resolve, locals);
        Assert.Equal(new[] { "resolution-lib", "linked-local" }, generation.LocalPackageNames);
        locals.Clear();
        Assert.Equal(2, generation.LocalPackages.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)generation.LocalPackageNames).Add("other"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)generation.LocalPackages).Add("other", f.Root));
    }

    [Fact]
    public async Task SelectedLocalModuleFailureNeverFallsBackToGeneration()
    {
        using var f = new Fixture();
        var local = f.Package("local", "plugin");
        var fallback = f.Package("fallback", "plugin");
        var packages = new DeploymentPackageResolver(new(f.Profiles, f.Active, [new("plugin", fallback, "1", f.Installation, DeploymentPackageScope.Installation)], new Dictionary<string, string> { ["plugin"] = local }));
        var modules = new StaticModuleResolver().Register("fallback-plugin", new Plugin<object?>
        {
            Apply = (_, _) =>
        {
        }
        });
        var resolver = new DeploymentModuleResolver(packages, modules).Register(fallback, ".", "fallback-plugin");
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await resolver.ResolveAsync("plugin", new Uri(Path.Combine(f.Active, "entry.cs"))));
        Assert.Equal(local, packages.PackageDirectory("plugin", new Uri(Path.Combine(f.Active, "entry.cs"))));
    }

    [Fact]
    public async Task BundleMetadataResolutionDoesNotRequireJavaScriptManifestExport()
    {
        using var f = new Fixture();
        var bundle = f.Package("sealed-bundle", "sealed-bundle");
        var manifest = PackageManifest.Read(Path.Combine(bundle, "package.json"));
        manifest.Raw["exports"] = new EntryOptions
        {
            ["."] = "./plugin.js"
        };
        manifest.Raw["dsh"] = new EntryOptions
        {
            ["bundle"] = new EntryOptions
            {
                ["patch"] = "patch.yml"
            }
        };
        manifest.Write(Path.Combine(bundle, "package.json"));
        File.WriteAllText(Path.Combine(bundle, "patch.yml"), "[]\n");
        ProfileMaintenance.WriteBundles(f.Active, PackageManifest.Read(Path.Combine(f.Active, "package.json")), ["sealed-bundle"]);
        var profile = await Profiles.LoadAsync(f.Active, new Dictionary<string, string> { ["sealed-bundle"] = bundle });
        Assert.Equal(bundle, Assert.Single(profile.Bundles).Directory);
        Assert.Equal(Path.Combine(bundle, "patch.yml"), profile.Bundles[0].PatchPath);
    }

    [Fact]
    public void ExplicitBundleRootsStillTraverseWhenEarlierNestedPackageHasSameName()
    {
        using var f = new Fixture();
        var a = f.Package("bundle-a", "bundle-a", dependencies: ["bundle-b"]);
        var nested = f.Package("nested-b", "bundle-b", dependencies: ["nested-only"]);
        var nestedOnly = f.Package("nested-only", "nested-only");
        var b = f.Package("bundle-b", "bundle-b", dependencies: ["explicit-only"]);
        var explicitOnly = f.Package("explicit-only", "explicit-only");
        f.Edge(a, "bundle-b", nested);
        f.Edge(nested, "nested-only", nestedOnly);
        f.Edge(b, "explicit-only", explicitOnly);
        var generation = DeploymentGeneration.Create(f.Installation, f.Profiles, f.Profile(a, b), f.Resolve);
        Assert.Equal(nestedOnly, generation.Entries.Single(row => row.Name == "nested-only").Directory);
        Assert.Equal(explicitOnly, generation.Entries.Single(row => row.Name == "explicit-only").Directory);
        Assert.DoesNotContain(generation.Entries, row => row.Name is "bundle-a" or "bundle-b");
    }

    [Fact]
    public void SeparateInstallationsAndProfilesDoNotShareRoutingOrWriteFiles()
    {
        using var a = new Fixture();
        using var b = new Fixture();
        var installedA = a.Package("commander", "commander", "1");
        var installedB = b.Package("commander", "commander", "2");
        var bundleA = a.Package("bundle-only", "bundle-only");
        var bundleB = b.Package("bundle-only", "bundle-only");
        var owned = a.Package("local", "pnpm-owned");
        var sentinel = Path.Combine(owned, "sentinel");
        File.WriteAllText(sentinel, "profile-installed");
        var resolverA = new DeploymentPackageResolver(new(a.Profiles, a.Active, [new("commander", installedA, "1", a.Installation, DeploymentPackageScope.Installation), new("bundle-only", bundleA, "1", a.Installation, DeploymentPackageScope.Profile)], new Dictionary<string, string> { ["pnpm-owned"] = owned }));
        var resolverB = new DeploymentPackageResolver(new(b.Profiles, b.Active, [new("commander", installedB, "2", b.Installation, DeploymentPackageScope.Installation), new("bundle-only", bundleB, "1", b.Installation, DeploymentPackageScope.Profile)]));
        var parentA = new Uri(Path.Combine(a.Active, "entry.cs"));
        var parentB = new Uri(Path.Combine(b.Active, "entry.cs"));
        Assert.Equal(installedA, resolverA.PackageDirectory("commander", parentA));
        Assert.Equal(installedB, resolverB.PackageDirectory("commander", parentB));
        Assert.Equal(owned, resolverA.PackageDirectory("pnpm-owned", parentA));
        Assert.Equal("profile-installed", File.ReadAllText(sentinel));
        Assert.Equal(bundleA, resolverA.PackageDirectory("bundle-only", parentA));
        Assert.Equal(bundleB, resolverB.PackageDirectory("bundle-only", parentB));
        Assert.Null(resolverA.PackageDirectory("bundle-only", parentB));
        Assert.Null(resolverB.PackageDirectory("bundle-only", parentA));
        Assert.False(Directory.Exists(Path.Combine(a.Profiles, "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(b.Profiles, "node_modules")));
        Assert.Equal(installedA, resolverA.PackageDirectory("commander", parentA));
    }

    [Fact]
    public void CanonicalizedProfileUsesAncestorFromMatchingParentTree()
    {
        using var f = new Fixture();
        var carrier = Path.Combine(f.Root, "carrier");
        var realProfiles = Path.Combine(carrier, "profiles");
        var realActive = Path.Combine(realProfiles, "active");
        Directory.CreateDirectory(realActive);
        var alias = Path.Combine(f.Root, "home", "profiles");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        CreateDirectoryAlias(alias, realProfiles);
        var expected = f.Package("carrier/lib", "stale-only", "3");
        var incorrect = f.Package("home/lib", "stale-only", "4");
        try
        {
            var resolver = new DeploymentPackageResolver(new(alias, Path.Combine(alias, "active"), []), ancestor: (_, parent) => Path.GetDirectoryName(parent.LocalPath) == carrier ? expected : incorrect);
            Assert.Equal(expected, resolver.PackageDirectory("stale-only", new Uri(Path.Combine(realActive, "entry.cs"))));
            Assert.Equal(incorrect, resolver.PackageDirectory("stale-only", new Uri(Path.Combine(alias, "active", "entry.cs"))));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    private static void CreateDirectoryAlias(string alias, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }

        var script = Path.Combine(Path.GetDirectoryName(alias)!, "junction.ps1");
        File.WriteAllText(script, "param([string]$Link,[string]$Target)\nNew-Item -ItemType Junction -Path $Link -Target $Target | Out-Null");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            script,
            alias,
            target
        }

        )
            start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
