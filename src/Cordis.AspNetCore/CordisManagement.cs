using System.Text;
using System.Text.Json;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cordis.AspNetCore;

/// <summary>The operation a host must authorize, with the inspected package hash for build execution.</summary>
/// <remarks>Target is the endpoint's parsed resource: an entry, bundle/package, active request ID, artifact package,
/// or canonical package/version grant. Inventory, graph and event-stream requests have no individual target.
/// Unrelated body/query fields never select the resource. Build authorization is separate from installation authority.</remarks>
public sealed record ManagementPermission(string Operation, string? Target = null, PackageInspection? Package = null);

/// <summary>Native host state, raw configuration and artifact bindings over one running profile.</summary>
/// <remarks>The host supplies authorization for every request. A lost installation response must
/// be resolved through wait/inventory; a null wait result is unknown, never permission to repeat the mutation.</remarks>
public sealed class CordisManagement(
    ProfileSession session,
    Func<HttpContext, ManagementPermission, Task<bool>> authorize,
    IProfilePackageToolchain? packages = null,
    Func<CancellationToken, Task<ClientModuleCatalog>>? clientModules = null)
{
    private readonly string generation = Guid.NewGuid().ToString("N");
    private PluginConfigurationOperations Operations => session.ConfigurationOperations;

    /// <summary>Notify connected clients after the host has accepted a complete captured catalog.</summary>
    /// <remarks>The build/deployment owner must serialize capture and publication through its existing session queue.
    /// Failed builds keep the prior catalog and send no success notification.</remarks>
    public Task NotifyClientModulesChangedAsync() => session.Context.RunAsync(context =>
    {
        context.Emit("client-modules/changed");
        return Task.CompletedTask;
    });

    /// <summary>Mount the standard generated PluginManager with this transport's host policy and generation fence.</summary>
    /// <remarks>The request accessor supplies identity only during the physical HTTP call. The manager still
    /// authorizes the parsed operation target and inspected build separately, using the same operations and toolchain.</remarks>
    public Task MountPluginManagerAsync(
        Context owner,
        TypertRegistry registry,
        IHttpContextAccessor requests,
        string package = "Cordis.NET.PluginManager")
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(requests);
        if (!ReferenceEquals(owner.Root, session.Context.Root))
            throw new ArgumentException("The manager must share the profile's Cordis root.", nameof(owner));
        return owner.RunAsync(context =>
        {
            new PluginManager(
                context,
                Operations,
                (_, permission) => AuthorizeRemoteAsync(requests.HttpContext, permission),
                packages);
            registry.Register(context, PluginManagerTypert.Contribution(package));
            return Task.CompletedTask;
        });
    }

    private async Task<bool> AuthorizeRemoteAsync(HttpContext? http, PluginManagementPermission permission)
    {
        if (http is null || !await authorize(http, new(permission.Operation, permission.Target, permission.Package)))
            return false;
        var expected = http.Request.Headers["If-Cordis-Generation"];
        if ((permission.Mutation || expected.Count != 0) && expected != generation)
            throw new RemoteError(
                "plugin-manager/generation-changed",
                "Read current host state before starting another management request.");
        return true;
    }

    /// <summary>Map host state, raw configuration and client artifacts. Raw mutations require the generation returned by GET /state.</summary>
    /// <remarks>Reads may also supply If-Cordis-Generation to refuse results from a replacement host.</remarks>
    public void Map(IEndpointRouteBuilder endpoints, string prefix = "/cordis")
    {
        ArgumentNullException.ThrowIfNull(authorize);
        Get(
            "/state",
            "read",
            async http => await WriteAsync(
                http,
                new EntryOptions
                {
                    ["protocol"] = 1,
                    ["generation"] = generation,
                    ["restartRequired"] = session.RequiresRestart,
                    ["selectedBundles"] = session.SelectedBundles.ToArray(),
                    ["loadedBundles"] = session.LoadedBundles.ToArray(),
                    ["skippedBundles"] = session
                        .SkippedBundles.Select(row => new EntryOptions
                        {
                            ["name"] = row.Name,
                            ["reason"] = row.Reason
                        })
                        .ToList(),
                }));
        GetTarget(
            "/configuration",
            "configuration-read",
            http => Query(http, "entryId"),
            async (http, target) =>
            {
                var snapshot = await Operations.ReadConfigurationAsync(target, http.RequestAborted);
                await WriteAsync(
                    http,
                    new EntryOptions
                    {
                        ["entryId"] = snapshot.EntryId,
                        ["revision"] = snapshot.Revision,
                        ["raw"] = snapshot.Raw,
                    });
            });
        GetTarget(
            "/configuration/schema",
            "configuration-read",
            http => Query(http, "entryId"),
            async (http, target) => await WriteAsync(
                http,
                SchemaResult(await Operations.ReadConfigurationSchemasAsync(target, http.RequestAborted))));
        PostTarget(
            "/configuration",
            "configuration-write",
            "entryId",
            async (http, body, target) => await WriteAsync(
                http,
                EditResult(
                    await Operations.MutateConfigurationAsync(
                        target,
                        Edits(body),
                        Text(body, "revision"),
                        cancellationToken: http.RequestAborted))));
        Get(
            "/client/graph",
            "client-read",
            async http =>
            {
                var graph = (await RequireClientModules()(http.RequestAborted)).Graph;
                var wire = new EntryOptions
                {
                    ["rev"] = graph.Revision,
                    ["entries"] = graph
                        .Entries.Select(row => new EntryOptions
                        {
                            ["id"] = row.Id,
                            ["url"] = row.Url,
                            ["rev"] = row.Revision,
                            ["inject"] = row.Inject.ToArray(),
                            ["external"] = row.External.ToArray(),
                            ["immediately"] = row.Immediately,
                        })
                        .ToList(),
                    ["batches"] = graph
                        .Batches.Select(row => new EntryOptions
                        {
                            ["phase"] = row.Phase,
                            ["url"] = row.Url,
                            ["rev"] = row.Revision,
                            ["entries"] = row.Entries.ToArray()
                        })
                        .ToList(),
                };
                if (graph.Release is { } release)
                {
                    var identity = new EntryOptions
                    {
                        ["version"] = release.Version
                    };
                    if (release.Commit is not null)
                        identity["commit"] = release.Commit;
                    wire["release"] = identity;
                }

                await WriteAsync(http, wire);
            });
        GetTarget(
            "/client/artifacts/{**package}",
            "client-read",
            http => Uri.UnescapeDataString(http.Request.RouteValues["package"] as string ?? ""),
            async (http, name) =>
            {
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

        void Get(string path, string permission, Func<HttpContext, Task> action) =>
            endpoints.MapGet(
                prefix + path,
                (HttpContext http) => HandleAsync(
                    http,
                    permission,
                    false,
                    (context, _) => (null, () => action(context))));

        void GetTarget(
            string path,
            string permission,
            Func<HttpContext, string> identity,
            Func<HttpContext, string, Task> action) =>
            endpoints.MapGet(
                prefix + path,
                (HttpContext http) => HandleAsync(
                    http,
                    permission,
                    false,
                    (context, _) =>
                    {
                        var target = identity(context);
                        return (target, () => action(context, target));
                    }));

        void PostTarget(
            string path,
            string permission,
            string identityField,
            Func<HttpContext, EntryOptions, string, Task> action) =>
            PostResolved(path, permission, body => Text(body, identityField), action);

        void PostResolved(
            string path,
            string permission,
            Func<EntryOptions, string> identity,
            Func<HttpContext, EntryOptions, string, Task> action) =>
            endpoints.MapPost(
                prefix + path,
                (HttpContext http) => HandleAsync(
                    http,
                    permission,
                    true,
                    (context, body) =>
                    {
                        var target = identity(body!);
                        return (target, () => action(context, body!, target));
                    }));
    }

    private async Task HandleAsync(
        HttpContext http,
        string operation,
        bool mutation,
        Func<HttpContext, EntryOptions?, (string? Target, Func<Task> Execute)> prepare)
    {
        try
        {
            var body = mutation ? await BodyAsync(http) : null;
            // Only the endpoint selects its identity. The execution closure captures
            // that same parsed value; unrelated request fields cannot redirect authorization.
            var request = prepare(http, body);
            if (!await authorize(http, new(operation, request.Target)))
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
                await WriteAsync(
                    http,
                    new EntryOptions
                    {
                        ["error"] = "generation-changed",
                        ["generation"] = generation
                    });
                return;
            }

            await request.Execute();
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException
                                          or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (http.Response.HasStarted)
                throw;
            http.Response.StatusCode = error is ObjectDisposedException
                ? StatusCodes.Status410Gone
                : StatusCodes.Status400BadRequest;
            await WriteAsync(
                http,
                new EntryOptions
                {
                    ["error"] = error.GetType().Name,
                    ["diagnostic"] = error.Message
                });
        }
    }

    private Func<CancellationToken, Task<ClientModuleCatalog>> RequireClientModules() =>
        clientModules ?? throw new InvalidOperationException("This host has no client module catalog.");

    private static string Query(HttpContext http, string key) =>
        http.Request.Query[key].FirstOrDefault() ?? throw new FormatException("Missing query: " + key);

    private static string Text(EntryOptions body, string key) =>
        body.GetValueOrDefault(key) as string ?? throw new FormatException("Missing string: " + key);

    private static EntryOptions EditResult(ConfigurationEditResult result) =>
        new()
        {
            ["saved"] = result.Saved,
            ["applied"] = result.Applied,
            ["revision"] = result.Revision,
            ["error"] = result.Error,
            ["diagnostic"] = result.Diagnostic,
            ["recoveryErrors"] = result.RecoveryErrors?.ToArray(),
        };

    private static EntryOptions SchemaResult(ConfigurationSchemaView schema) =>
        new()
        {
            ["revision"] = schema.Revision,
            ["schemastery"] = ConfigurationFile.Parse(schema.Schemastery.Document, true),
            ["jsonSchema"] = ConfigurationFile.Parse(schema.JsonSchema.Document, true),
            ["complete"] = schema.JsonSchema.Complete && schema.Schemastery.Complete,
            ["diagnostics"] = schema.Schemastery.Diagnostics.Concat(schema.JsonSchema.Diagnostics).Distinct().ToArray(),
        };

    private static ConfigurationPathOperation[] Edits(EntryOptions body)
    {
        if (body.GetValueOrDefault("operations") is not IEnumerable<object?> values)
            throw new FormatException("operations must be an array.");
        return values
            .Select(value =>
            {
                var edit = value as EntryOptions ?? throw new FormatException("Each operation must be an object.");
                if (edit.GetValueOrDefault("path") is not IEnumerable<object?> path)
                    throw new FormatException("path must be an array.");
                var keys = path
                    .Select(key => key as string ?? throw new FormatException("path segments must be strings."))
                    .ToArray();
                return Text(edit, "op") switch
                {
                    "set" when edit.ContainsKey("value") => (ConfigurationPathOperation)new ConfigurationSet(
                        keys,
                        edit["value"]),
                    "unset" => new ConfigurationUnset(keys),
                    _ => throw new FormatException("Supported configuration operations are set and unset."),
                };
            })
            .ToArray();
    }

    private static async Task<EntryOptions> BodyAsync(HttpContext http)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (await http.Request.Body.ReadAsync(buffer, http.RequestAborted) is var read && read > 0)
        {
            if (bytes.Length + read > 1024 * 1024)
                throw new FormatException("Management requests are limited to 1 MiB.");
            bytes.Write(buffer, 0, read);
        }

        return ConfigurationFile.Parse(Encoding.UTF8.GetString(bytes.ToArray()), true) as EntryOptions ??
            throw new FormatException("The request must be an object.");
    }

    private static async Task WriteAsync(HttpContext http, object? data)
    {
        http.Response.ContentType = "application/json";
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.WriteAsync(ConfigurationFile.Write(data, true), http.RequestAborted);
    }
}
