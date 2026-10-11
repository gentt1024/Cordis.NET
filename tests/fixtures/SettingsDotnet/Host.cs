using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.AspNetCore;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length != 2)
    throw new ArgumentException("Expected workDirectory and deployed bundleDirectory.");
var work = Path.GetFullPath(args[0]);
var bundle = Path.GetFullPath(args[1]);
Directory.CreateDirectory(work);
var profileDirectory = Path.Combine(work, "profile");
var home = Directory.CreateDirectory(Path.Combine(work, "home")).FullName;
var manifest = PackageManifest.Read(Path.Combine(bundle, "package.json"));
var package = manifest.Raw.GetValueOrDefault("name") as string ??
    throw new FormatException("The deployed bundle requires its package name.");
var moduleRequest = "nuget:" + package.ToLowerInvariant();
await using var resolver = new ClrModuleResolver(Path.Combine(work, "shadows"));
using (var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "cordis.plugin.json"))))
{
    var root = metadata.RootElement;
    var assembly = root.GetProperty("assembly").GetString()!;
    resolver.Register(moduleRequest, new(bundle, assembly, root.GetProperty("entryType").GetString()!));
    foreach (var export in root.GetProperty("exports").EnumerateObject())
    {
        if (!export.Name.StartsWith("./", StringComparison.Ordinal))
            throw new FormatException("The fixture requires explicit relative CLR export names.");
        resolver.Register(moduleRequest + export.Name[1..], new(bundle, assembly, export.Value.GetString()!));
    }
}

var bundles = new Dictionary<string, string>(StringComparer.Ordinal)
{
    [package] = bundle
};
Profiles.Initialize(profileDirectory, [package]);
var profile = await Profiles.LoadAsync(profileDirectory, bundles);
if (profile.SkippedBundles.Count != 0)
    throw new InvalidOperationException(string.Join("; ", profile.SkippedBundles.Select(skipped => skipped.Reason)));
var launch = new ProfileLaunch(profile, home, [], bundles);
var configuration = Path.Combine(profileDirectory, "cordis.yml");
await File.WriteAllTextAsync(configuration, "[]\n");
TypertRegistry registry = null!;
await using var session = await ProfileSession.StartAsync(
    configuration,
    launch,
    resolver,
    required: new HashSet<string>(["root:one"], StringComparer.Ordinal),
    prepare: context =>
    {
        registry = new TypertRegistry(context);
        context.Provide("typert", registry);
        return Task.CompletedTask;
    },
    diagnostic: _ => Console.Error.WriteLine("Profile lifecycle reported an error."));
await session.Context.RunAsync(_ =>
{
    if (session.Loader.Resolve("root:one").Fiber?.State != FiberState.Active ||
        session.Loader.Resolve("root:two").Fiber?.State != FiberState.Active)
        throw new InvalidOperationException("Both independent configuration entries must be active.");
    return Task.CompletedTask;
});

var policy = new SettingsPolicy(
    ["label", "rootSecret", "nested", "hidden", "genuineNull", "defaulted"],
    ["hidden"]);
var controls = new FixtureControls(session, resolver, registry, policy, moduleRequest, profile.UserLayer.Source);
await controls.InitializeAsync();
var gateway = new TypertGateway(session.Context, registry);
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseKestrel();
await using var app = builder.Build();
app.Urls.Add("http://127.0.0.1:0");
app.MapCordisRemote("/remote", gateway, (_, _) => Task.FromResult(true));
app.MapPost("/control/{command}", (string command) => controls.ExecuteAsync(command));
app.MapPost(
    "/control/stop",
    () =>
    {
        app.Lifetime.StopApplication();
        return Results.Ok();
    });
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
Console.WriteLine("READY " + address);
Console.Out.Flush();
await app.WaitForShutdownAsync();

internal sealed class FixtureControls(
    ProfileSession session,
    ClrModuleResolver resolver,
    TypertRegistry registry,
    SettingsPolicy policy,
    string moduleRequest,
    string patchPath)
{
    private readonly SemaphoreSlim mutations = new(1);
    private Fiber? providerOwner;
    private Fiber? formsOwner;
    private SettingsForms nativeProvider = null!;
    private Fiber? definitionOwner;
    private Fiber? loggerOwner;
    private TypertLoader artifacts = null!;
    private HeldProvider? held;
    private string providerMode = "missing";
    private int providerGeneration;
    private int definitionGeneration;
    private string[] diagnostics = [];

    internal async Task InitializeAsync()
    {
        await MountFormsAsync();
        await ProvideAsync(new ObservedProvider(nativeProvider, RecordDiagnostics), "live");
        await RegisterDefinitionsAsync();
    }

    internal async Task<IResult> ExecuteAsync(string command)
    {
        if (command == "wait-held")
        {
            var selected = held;
            if (selected is null)
                return Results.Json(
                    new FixtureFailure("No held provider is installed."),
                    jsonTypeInfo: FixtureJson.Default.FixtureFailure,
                    statusCode: StatusCodes.Status409Conflict);
            await selected.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Status(command);
        }

        if (command == "release")
        {
            held?.Release.TrySetResult();
            return Status(command);
        }

        await mutations.WaitAsync();
        try
        {
            switch (command)
            {
                case "withdraw":
                    await WithdrawProviderAsync();
                    break;
                case "reprovide":
                    await ProvideAsync(new ObservedProvider(nativeProvider, RecordDiagnostics), "live");
                    break;
                case "fail":
                    await ProvideAsync(new FailingProvider(nativeProvider), "failing");
                    break;
                case "hold":
                    if (held is { } previous && !previous.Release.Task.IsCompleted)
                        return Results.Json(
                            new FixtureFailure("Release the previous held provider first."),
                            jsonTypeInfo: FixtureJson.Default.FixtureFailure,
                            statusCode: StatusCodes.Status409Conflict);
                    held = new HeldProvider(new ObservedProvider(nativeProvider, RecordDiagnostics));
                    await ProvideAsync(held, "held");
                    break;
                case "suspend":
                    await artifacts.SuspendAsync([moduleRequest]);
                    break;
                case "resume":
                    await artifacts.ResumeAsync([moduleRequest]);
                    definitionGeneration++;
                    break;
                case "definition-withdraw":
                    if (definitionOwner is not null)
                        await definitionOwner.DisposeAsync();
                    definitionOwner = null;
                    break;
                case "reregister":
                    await RegisterDefinitionsAsync();
                    break;
                case "logger-fail":
                    await session.Context.RunAsync(async context =>
                    {
                        if (loggerOwner is not null)
                            await loggerOwner.DisposeAsync();
                        loggerOwner = context.Plugin(
                            new Plugin<object?>
                            {
                                Apply = (owner, _) => owner.Logger.Exporter(
                                    new DelegateLogExporter(
                                        _ => throw new InvalidOperationException(
                                            "Fixture log observer refused the diagnostic."),
                                        (int)Cordis.LogLevel.Debug))
                            });
                        await loggerOwner.WaitAsync();
                    });
                    break;
                case "logger-restore":
                    if (loggerOwner is not null)
                        await loggerOwner.DisposeAsync();
                    loggerOwner = null;
                    break;
                case "diagnostics":
                    var snapshot = await nativeProvider.DescribeAsync();
                    RecordDiagnostics(snapshot.Diagnostics);
                    return Results.Json(
                        new FixtureDiagnostics(Volatile.Read(ref diagnostics)),
                        jsonTypeInfo: FixtureJson.Default.FixtureDiagnostics);
                case "source":
                    return Results.Json(
                        new FixtureSource(await File.ReadAllTextAsync(patchPath)),
                        jsonTypeInfo: FixtureJson.Default.FixtureSource);
                case "settings-restart":
                    await WithdrawProviderAsync();
                    await MountFormsAsync();
                    await ProvideAsync(new ObservedProvider(nativeProvider, RecordDiagnostics), "live");
                    break;
                case "readonly":
                    await WithdrawProviderAsync();
                    await MountFormsAsync(writable: false);
                    await ProvideAsync(new ObservedProvider(nativeProvider, RecordDiagnostics), "readonly");
                    break;
                case "two-disable":
                case "two-enable":
                    var changed = await session.ConfigurationOperations.SetPluginEnabledAsync(
                        "root:two",
                        command == "two-enable");
                    if (!changed.Changed || changed.Application != "applied" || changed.Error is not null)
                        throw new InvalidOperationException(
                            changed.Diagnostic ?? changed.Error ?? "Entry enablement did not complete.");
                    if (command == "two-enable")
                        await session.Context.RunAsync(_ =>
                        {
                            nativeProvider.Configure(false, session.Loader.Resolve("root:two").Fiber!);
                            return Task.CompletedTask;
                        });
                    break;
                case "status":
                    break;
                default:
                    return Results.Json(
                        new FixtureFailure("Unknown fixture command."),
                        jsonTypeInfo: FixtureJson.Default.FixtureFailure,
                        statusCode: StatusCodes.Status404NotFound);
            }

            return Status(command);
        }
        finally
        {
            mutations.Release();
        }
    }

    private IResult Status(string command) => Results.Json(
        new FixtureStatus(
            command,
            providerMode,
            providerGeneration,
            definitionGeneration,
            held?.Entered.Task.IsCompleted ?? false,
            Volatile.Read(ref diagnostics)),
        jsonTypeInfo: FixtureJson.Default.FixtureStatus);

    private async Task MountFormsAsync(bool writable = true)
    {
        if (formsOwner is not null)
            await formsOwner.DisposeAsync();
        await session.Context.RunAsync(async context =>
        {
            formsOwner = context.Plugin(
                new Plugin<object?>
                {
                    Apply = (owner, _) =>
                    {
                        nativeProvider = new SettingsForms(
                            owner.Isolate("settings"),
                            session.ConfigurationOperations,
                            _ => policy,
                            writable: writable);
                        nativeProvider.Configure(false, session.Loader.Resolve("root:two").Fiber!);
                    }
                });
            await formsOwner.WaitAsync();
        });
    }

    private async Task ProvideAsync(ISettingsProvider provider, string mode)
    {
        await WithdrawProviderAsync();
        await session.Context.RunAsync(async context =>
        {
            providerOwner = context.Plugin(
                new Plugin<object?>
                {
                    Apply = (owner, _) => owner.Provide("settings", provider)
                });
            await providerOwner.WaitAsync();
        });
        providerMode = mode;
        providerGeneration++;
    }

    private async Task WithdrawProviderAsync()
    {
        if (providerOwner is not null)
            await providerOwner.DisposeAsync();
        providerOwner = null;
        providerMode = "missing";
    }

    private async Task RegisterDefinitionsAsync()
    {
        if (definitionOwner is not null)
            await definitionOwner.DisposeAsync();
        await session.Context.RunAsync(async context =>
        {
            definitionOwner = context.Plugin(
                new Plugin<object?>
                {
                    ApplyAsync = async (owner, _) =>
                        artifacts = await TypertLoader.StartAsync(owner, session.Loader, registry, resolver)
                });
            await definitionOwner.WaitAsync();
        });
        definitionGeneration++;
    }

    private void RecordDiagnostics(IReadOnlyList<string> reasons)
    {
        Volatile.Write(ref diagnostics, reasons.ToArray());
        foreach (var reason in reasons)
            Console.Error.WriteLine("Settings projection: " + reason);
    }
}

internal abstract class ForwardingProvider(ISettingsProvider inner) : ISettingsProvider
{
    protected ISettingsProvider Inner
    {
        get;
    } = inner;

    public virtual Task<SettingsDescribeSnapshot> DescribeAsync() => Inner.DescribeAsync();

    public Task UpdateAsync(
        string ns,
        JsonElement patch,
        [System.Diagnostics.CodeAnalysis.DisallowNull]
        double? expectedRevision = null!) =>
        Inner.UpdateAsync(ns, patch, expectedRevision!);

    public Task ReplaceAsync(
        string ns,
        JsonElement section,
        [System.Diagnostics.CodeAnalysis.DisallowNull]
        double? expectedRevision = null!) =>
        Inner.ReplaceAsync(ns, section, expectedRevision!);

    public Task MutateAsync(
        string ns,
        IReadOnlyList<SettingsPathOp> ops,
        [System.Diagnostics.CodeAnalysis.DisallowNull]
        double? expectedRevision = null!) =>
        Inner.MutateAsync(ns, ops, expectedRevision!);
}

internal sealed class ObservedProvider(ISettingsProvider inner, Action<IReadOnlyList<string>> report)
    : ForwardingProvider(inner)
{
    public override async Task<SettingsDescribeSnapshot> DescribeAsync()
    {
        var snapshot = await Inner.DescribeAsync();
        report(snapshot.Diagnostics);
        return snapshot;
    }
}

internal sealed class FailingProvider(ISettingsProvider inner) : ForwardingProvider(inner)
{
    public override Task<SettingsDescribeSnapshot> DescribeAsync()
    {
        using var details = JsonDocument.Parse("""{"ns":"one","stage":"provider"}""");
        return Task.FromException<SettingsDescribeSnapshot>(
            new RemoteError(
                "fixture/refused",
                "The fixture settings provider refused this read.",
                details.RootElement));
    }
}

internal sealed class HeldProvider(ISettingsProvider inner) : ForwardingProvider(inner)
{
    internal TaskCompletionSource Entered
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task<SettingsDescribeSnapshot> DescribeAsync()
    {
        var snapshot = await Inner.DescribeAsync();
        Entered.TrySetResult();
        await Release.Task;
        return snapshot;
    }
}

internal sealed record FixtureStatus(
    string Command,
    string ProviderMode,
    int ProviderGeneration,
    int DefinitionGeneration,
    bool Held,
    IReadOnlyList<string> Diagnostics);

internal sealed record FixtureFailure(string Reason);

internal sealed record FixtureDiagnostics(IReadOnlyList<string> Diagnostics);

internal sealed record FixtureSource(string Document);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FixtureStatus))]
[JsonSerializable(typeof(FixtureFailure))]
[JsonSerializable(typeof(FixtureDiagnostics))]
[JsonSerializable(typeof(FixtureSource))]
internal partial class FixtureJson : JsonSerializerContext;
