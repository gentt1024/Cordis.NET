using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cordis.AspNetCore;

/// <summary>The operation a host must authorize, with the inspected package hash for build execution.</summary>
public sealed record ManagementPermission(string Operation, string? Target = null, PackageInspection? Package = null);

/// <summary>HTTP/SSE bindings over one running profile. No configuration or plugin lifecycle state is copied into the transport.</summary>
/// <remarks>The host supplies authorization and settings visibility for every request. A lost installation response must
/// be resolved through wait/inventory; a null wait result is unknown, never permission to repeat the mutation.</remarks>
public sealed class CordisManagement(ProfileSession session, Func<HttpContext, ManagementPermission, Task<bool>> authorize,
    Func<string, SettingsPolicy> settingsPolicy, IProfilePackageToolchain? packages = null,
    Func<CancellationToken, Task<ClientModuleCatalog>>? clientModules = null)
{
    private readonly string generation = Guid.NewGuid().ToString("N");
    private event Action? ClientModulesChanged;
    private PluginConfigurationOperations Operations => session.ConfigurationOperations;

    /// <summary>Notify connected clients after the host has accepted a complete captured catalog.</summary>
    /// <remarks>The build/deployment owner must serialize capture and publication through its existing session queue.
    /// Failed builds keep the prior catalog and send no success notification.</remarks>
    public void NotifyClientModulesChanged() => ClientModulesChanged?.Invoke();

    /// <summary>Map the versioned management contract. Mutations require the generation returned by GET /state.</summary>
    /// <remarks>Reads may also supply If-Cordis-Generation to refuse results from a replacement host.</remarks>
    public void Map(IEndpointRouteBuilder endpoints, string prefix = "/cordis")
    {
        ArgumentNullException.ThrowIfNull(authorize);
        ArgumentNullException.ThrowIfNull(settingsPolicy);
        Get("/state", "read", async http => await WriteAsync(http, new EntryOptions
        {
            ["protocol"] = 1, ["generation"] = generation, ["restartRequired"] = session.RequiresRestart,
            ["selectedBundles"] = session.SelectedBundles.ToArray(), ["loadedBundles"] = session.LoadedBundles.ToArray(),
            ["skippedBundles"] = session.SkippedBundles.Select(row => new EntryOptions { ["name"] = row.Name, ["reason"] = row.Reason }).ToList(),
        }));
        Get("/plugins", "read", async http => await WriteAsync(http, (await Operations.ListPluginsAsync()).Select(row => new EntryOptions
        {
            ["entryId"] = row.EntryId, ["module"] = row.ModuleName, ["enabled"] = row.Enabled,
            ["readOnlyReason"] = row.ReadOnlyReason, ["state"] = row.State?.ToString(),
        }).ToList()));
        Get("/bundles", "read", async http => await WriteAsync(http, (await Operations.ListBundlesAsync()).Select(row => new EntryOptions
        {
            ["name"] = row.Name, ["version"] = row.Version, ["enabled"] = row.Enabled, ["installed"] = row.Installed,
            ["removable"] = row.Removable, ["readOnlyReason"] = row.ReadOnlyReason, ["error"] = row.Error,
        }).ToList()));
        Get("/sources", "read", http => WriteAsync(http, RequirePackages().Sources.ToArray()));
        Get("/versions", "inspect", async http => await WriteAsync(http, await RequirePackages().VersionsAsync(
            Query(http, "name"), Query(http, "source"), http.RequestAborted)));
        Get("/configuration", "configuration-read", async http =>
        {
            var snapshot = await Operations.ReadConfigurationAsync(Query(http, "entryId"), http.RequestAborted);
            await WriteAsync(http, new EntryOptions
            {
                ["entryId"] = snapshot.EntryId, ["revision"] = snapshot.Revision, ["raw"] = snapshot.Raw,
            });
        });
        Get("/configuration/schema", "configuration-read", async http => await WriteAsync(http, SchemaResult(
            await Operations.ReadConfigurationSchemasAsync(Query(http, "entryId"), http.RequestAborted))));
        Post("/configuration", "configuration-write", async (http, body) => await WriteAsync(http, EditResult(
            await Operations.MutateConfigurationAsync(Text(body, "entryId"), Edits(body), Text(body, "revision"),
                cancellationToken: http.RequestAborted))));
        Get("/settings", "settings-read", async http =>
        {
            var entryId = Query(http, "entryId");
            var settings = await Operations.ReadSettingsAsync(entryId, settingsPolicy(entryId), http.RequestAborted);
            await WriteAsync(http, new EntryOptions
            {
                ["entryId"] = entryId, ["revision"] = settings.Revision, ["diagnostics"] = settings.Diagnostics.ToArray(),
                ["secrets"] = settings.Secrets.Select(secret => new EntryOptions { ["path"] = secret.Path.ToArray(), ["set"] = secret.Set }).ToList(),
                ["fields"] = settings.Fields.Select(field => new EntryOptions
                { ["name"] = field.Name, ["kind"] = field.Kind, ["value"] = field.Value, ["overridden"] = field.Overridden }).ToList(),
            });
        });
        Get("/settings/schema", "settings-read", async http =>
        {
            var entryId = Query(http, "entryId");
            var schema = await Operations.ReadSettingsSchemasAsync(entryId, settingsPolicy(entryId), http.RequestAborted);
            await WriteAsync(http, SchemaResult(schema));
        });
        Post("/settings", "settings-write", async (http, body) =>
        {
            var entryId = Text(body, "entryId");
            var result = await Operations.MutateSettingsAsync(entryId, Edits(body), Text(body, "revision"), settingsPolicy(entryId), http.RequestAborted);
            await WriteAsync(http, EditResult(result));
        });
        Post("/enable", "manage", async (http, body) =>
        {
            var enabled = Boolean(body, "enabled");
            var result = Text(body, "kind") switch
            {
                "plugin" => await Operations.SetPluginEnabledAsync(Text(body, "target"), enabled, http.RequestAborted),
                "bundle" => await Operations.SetBundleEnabledAsync(Text(body, "target"), enabled, http.RequestAborted),
                _ => throw new FormatException("kind must be plugin or bundle."),
            };
            await WriteAsync(http, Change(result));
        });
        Post("/inspect", "inspect", async (http, body) =>
        {
            var inspection = await RequirePackages().InspectAsync(Request(body), http.RequestAborted);
            await WriteAsync(http, new EntryOptions { ["hash"] = inspection.ContentHash, ["description"] = inspection.Description,
                ["requiresBuildApproval"] = inspection.RequiresBuildApproval });
        });
        Post("/install", "install", async (http, body) =>
        {
            var request = Request(body);
            var inspection = await RequirePackages().InspectAsync(request, http.RequestAborted);
            if (inspection.ContentHash != Text(body, "inspectedHash")) throw new InvalidOperationException("The package changed after client inspection.");
            if (!await authorize(http, new("build", request.Name, inspection)))
            { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            // The connection owns neither the installation nor its cancellation. Explicit cancel and session shutdown do.
            var result = await Operations.InstallPackageAsync(RequirePackages(), request with { ExpectedHash = inspection.ContentHash },
                Text(body, "requestId"), buildApproved: true, enabled: body.GetValueOrDefault("enabled") is not false);
            await WriteAsync(http, PackageResult(result));
        });
        Get("/install/wait", "install", async http =>
        {
            var result = await Operations.WaitForInstallAsync(Query(http, "requestId"));
            await WriteAsync(http, result is null ? null : PackageResult(result));
        });
        Post("/install/cancel", "install", async (http, body) => await WriteAsync(http,
            new EntryOptions { ["status"] = await Operations.CancelInstallAsync(Text(body, "requestId")) }));
        Post("/remove", "manage", async (http, body) => await WriteAsync(http,
            PackageResult(await Operations.RemovePackageAsync(RequirePackages(), Text(body, "name"), http.RequestAborted))));
        Post("/compatibility", "compatibility-grant", async (http, body) => await WriteAsync(http, Change(
            await Operations.SetVersionExemptionAsync(Text(body, "packageVersion"), Text(body, "runtimeVersion"), Boolean(body, "enabled"),
                Boolean(body, "acceptRisk"), http.RequestAborted))));
        Get("/compatibility", "compatibility-read", async http =>
        {
            var grants = Operations.ReadVersionCompatibility();
            await WriteAsync(http, new EntryOptions
            {
                ["rewritable"] = grants.Rewritable, ["warnings"] = grants.Warnings.ToArray(),
                ["exemptions"] = grants.Exemptions.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.ToArray()),
            });
        });
        Get("/events", "read", EventsAsync);
        Get("/client/graph", "client-read", async http =>
        {
            var graph = (await RequireClientModules()(http.RequestAborted)).Graph;
            await WriteAsync(http, new EntryOptions
            {
                ["rev"] = graph.Revision,
                ["entries"] = graph.Entries.Select(row => new EntryOptions
                {
                    ["id"] = row.Id, ["url"] = row.Url, ["rev"] = row.Revision,
                    ["inject"] = row.Inject.ToArray(), ["external"] = row.External.ToArray(), ["immediately"] = row.Immediately,
                }).ToList(),
                ["batches"] = graph.Batches.Select(row => new EntryOptions
                { ["phase"] = row.Phase, ["url"] = row.Url, ["rev"] = row.Revision, ["entries"] = row.Entries.ToArray() }).ToList(),
            });
        });
        Get("/client/artifacts/{**package}", "client-read", async http =>
        {
            var name = Uri.UnescapeDataString(http.Request.RouteValues["package"] as string ?? "");
            var catalog = await RequireClientModules()(http.RequestAborted);
            var artifact = catalog.FindArtifact(name, Query(http, "rev"));
            if (artifact is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            http.Response.Headers.ETag = artifact.ETag;
            http.Response.Headers.CacheControl = "private, no-cache";
            if (http.Request.Headers.IfNoneMatch == artifact.ETag)
            {
                http.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
            http.Response.ContentType = "text/javascript; charset=utf-8";
            http.Response.ContentLength = artifact.Length;
            using var content = artifact.OpenRead();
            await content.CopyToAsync(http.Response.Body, http.RequestAborted);
        });

        void Get(string path, string permission, Func<HttpContext, Task> action)
            => endpoints.MapGet(prefix + path, (HttpContext http) => HandleAsync(http, permission, false, (context, _) => action(context)));
        void Post(string path, string permission, Func<HttpContext, EntryOptions, Task> action)
            => endpoints.MapPost(prefix + path, (HttpContext http) => HandleAsync(http, permission, true,
                (context, body) => action(context, body!)));
    }

    private async Task HandleAsync(HttpContext http, string operation, bool mutation, Func<HttpContext, EntryOptions?, Task> action)
    {
        try
        {
            var body = mutation ? await BodyAsync(http) : null;
            var target = new[] { "entryId", "target", "name", "requestId", "packageVersion" }
                .Select(key => body?.GetValueOrDefault(key) as string ?? http.Request.Query[key].FirstOrDefault()).FirstOrDefault(value => value is not null);
            if (!await authorize(http, new(operation, target)))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            // Inventory and captured client graphs are meaningful only while their
            // owning Context is live, even when reading them requires no plugin call.
            await session.Context.RunAsync(_ => Task.CompletedTask);
            var expectedGeneration = http.Request.Headers["If-Cordis-Generation"];
            if ((mutation || expectedGeneration.Count != 0) && expectedGeneration != generation)
            {
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await WriteAsync(http, new EntryOptions { ["error"] = "generation-changed", ["generation"] = generation });
                return;
            }
            await action(http, body);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (http.Response.HasStarted) throw;
            http.Response.StatusCode = error is ObjectDisposedException ? StatusCodes.Status410Gone : StatusCodes.Status400BadRequest;
            await WriteAsync(http, new EntryOptions { ["error"] = error.GetType().Name, ["diagnostic"] = error.Message });
        }
    }

    private async Task EventsAsync(HttpContext http)
    {
        var messages = Channel.CreateBounded<EntryOptions>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        EffectHandle? ownership = null;
        await session.Context.RunAsync(context =>
        {
            ownership = context.Effect(() => new EventStreamCleanup(messages.Writer), "management event connection");
            return Task.CompletedTask;
        });
        var sequence = 0L;
        void Send(string kind, object? value) => messages.Writer.TryWrite(new EntryOptions
        { ["generation"] = generation, ["sequence"] = Interlocked.Increment(ref sequence), ["kind"] = kind, ["value"] = value });
        void Changed(string reason) => Send("configuration", reason);
        void Refreshed() => Send("refresh", null);
        void ModulesChanged() => Send("client-modules", null);
        void Progress(PackageProgress progress) => Send("package", new EntryOptions
        { ["requestId"] = progress.RequestId, ["phase"] = progress.Phase, ["output"] = progress.Output });
        Operations.Changed += Changed;
        Operations.PackageProgressed += Progress;
        session.Refreshed += Refreshed;
        ClientModulesChanged += ModulesChanged;
        try
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            Send("connected", null);
            await foreach (var message in messages.Reader.ReadAllAsync(http.RequestAborted))
            {
                var json = ConfigurationFile.Write(message, true).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
                await http.Response.WriteAsync("data: " + json + "\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
        }
        finally
        {
            Operations.Changed -= Changed;
            Operations.PackageProgressed -= Progress;
            session.Refreshed -= Refreshed;
            ClientModulesChanged -= ModulesChanged;
            await ownership!.DisposeAsync();
        }
    }

    // Root disposal closes the stream without waiting for the HTTP client to read.
    // Request completion releases this same effect; no transport lifetime table is needed.
    private sealed class EventStreamCleanup(ChannelWriter<EntryOptions> messages) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            messages.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private IProfilePackageToolchain RequirePackages() => packages ?? throw new InvalidOperationException("This host has no dynamic package toolchain; static code changes require republishing.");
    private Func<CancellationToken, Task<ClientModuleCatalog>> RequireClientModules() => clientModules
        ?? throw new InvalidOperationException("This host has no client module catalog.");
    private static PackageRequest Request(EntryOptions body) => new(Text(body, "name"), Text(body, "version"), Text(body, "source"));
    private static string Query(HttpContext http, string key) => http.Request.Query[key].FirstOrDefault() ?? throw new FormatException("Missing query: " + key);
    private static string Text(EntryOptions body, string key) => body.GetValueOrDefault(key) as string ?? throw new FormatException("Missing string: " + key);
    private static bool Boolean(EntryOptions body, string key) => body.GetValueOrDefault(key) is bool value ? value : throw new FormatException("Missing boolean: " + key);
    private static EntryOptions Change(ConfigurationChange result) => new()
    { ["changed"] = result.Changed, ["application"] = result.Application, ["error"] = result.Error, ["diagnostic"] = result.Diagnostic };
    private static EntryOptions PackageResult(PackageChange result) => new()
    {
        ["requestId"] = result.RequestId, ["target"] = result.Target, ["stage"] = result.Stage, ["installed"] = result.Installed,
        ["selected"] = result.Selected, ["application"] = result.Application, ["error"] = result.Error, ["diagnostic"] = result.Diagnostic,
        ["residuals"] = result.Residuals?.ToArray(), ["toolExitCode"] = result.ToolExitCode,
    };
    private static EntryOptions EditResult(ConfigurationEditResult result) => new()
    {
        ["saved"] = result.Saved, ["applied"] = result.Applied, ["revision"] = result.Revision,
        ["error"] = result.Error, ["diagnostic"] = result.Diagnostic, ["recoveryErrors"] = result.RecoveryErrors?.ToArray(),
    };
    private static EntryOptions SchemaResult(ConfigurationSchemaView schema) => new()
    {
        ["revision"] = schema.Revision,
        ["schemastery"] = ConfigurationFile.Parse(schema.Schemastery.Document, true),
        ["jsonSchema"] = ConfigurationFile.Parse(schema.JsonSchema.Document, true),
        ["complete"] = schema.JsonSchema.Complete && schema.Schemastery.Complete,
        ["diagnostics"] = schema.Schemastery.Diagnostics.Concat(schema.JsonSchema.Diagnostics).Distinct().ToArray(),
    };
    private static ConfigurationPathOperation[] Edits(EntryOptions body)
    {
        if (body.GetValueOrDefault("operations") is not IEnumerable<object?> values) throw new FormatException("operations must be an array.");
        return values.Select(value =>
        {
            var edit = value as EntryOptions ?? throw new FormatException("Each operation must be an object.");
            if (edit.GetValueOrDefault("path") is not IEnumerable<object?> path) throw new FormatException("path must be an array.");
            var keys = path.Select(key => key as string ?? throw new FormatException("path segments must be strings.")).ToArray();
            return Text(edit, "op") switch
            {
                "set" when edit.ContainsKey("value") => (ConfigurationPathOperation)new ConfigurationSet(keys, edit["value"]),
                "unset" => new ConfigurationUnset(keys),
                _ => throw new FormatException("Supported configuration operations are set and unset."),
            };
        }).ToArray();
    }
    private static async Task<EntryOptions> BodyAsync(HttpContext http)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (await http.Request.Body.ReadAsync(buffer, http.RequestAborted) is var read && read > 0)
        {
            if (bytes.Length + read > 1024 * 1024) throw new FormatException("Management requests are limited to 1 MiB.");
            bytes.Write(buffer, 0, read);
        }
        return ConfigurationFile.Parse(Encoding.UTF8.GetString(bytes.ToArray()), true) as EntryOptions ?? throw new FormatException("The request must be an object.");
    }
    private static async Task WriteAsync(HttpContext http, object? data)
    {
        http.Response.ContentType = "application/json";
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.WriteAsync(ConfigurationFile.Write(data, true), http.RequestAborted);
    }
}
