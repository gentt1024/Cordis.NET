using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>The standard Settings Remote over the ordinary Settings service.</summary>
[RemoteService("settingsController", typeof(SettingsJson), Namespace = "settings")]
public partial class SettingsController
{
    private readonly Context context;
    private readonly Func<string, CancellationToken, Task> openTextFile;

    /// <summary>Publish the controller independently of its optional Settings provider.</summary>
    public SettingsController(Context context, Func<string, CancellationToken, Task>? openTextFile = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
        this.openTextFile = openTextFile ?? SettingsDocumentOpener.OpenAsync;
        context.Provide("settingsController", this);
    }

    private Context Caller => TypertInvocation.Current?.Context ?? context;

    private ISettingsProvider Provider() => Caller.Get<ISettingsProvider>("settings", strict: false) ??
        throw new RemoteError(
            "gateway/internal",
            "settings service is absent: mount SettingsForms over the profile's ConfigurationOperations");

    /// <summary>Read redacted layered forms and numeric revisions from the current provider.</summary>
    [RemoteMethod("describe")]
    public Task<SettingsDescribeValue> DescribeAsync() => DescribeAsync(Provider());

    /// <summary>Prepare the provider-owned document and hand it to the host's native text opener.</summary>
    [RemoteMethod("openSettingsDocument")]
    public async Task<SettingsDocumentOpenValue> OpenSettingsDocumentAsync(
        CancellationToken cancellationToken = default)
    {
        var provider = Provider();
        if (cancellationToken.IsCancellationRequested)
            throw new RemoteError("gateway/cancelled", "settings document open was aborted");
        string path;
        try
        {
            path = await provider.PrepareDocumentAsync();
        }
        catch (Exception error)
        {
            throw cancellationToken.IsCancellationRequested
                ? new RemoteError(
                    "gateway/cancelled",
                    "settings document preparation was aborted",
                    innerException: error)
                : new RemoteError(
                    "gateway/internal",
                    "settings document preparation failed: " + error.Message,
                    innerException: error);
        }

        if (cancellationToken.IsCancellationRequested)
            throw new RemoteError("gateway/cancelled", "settings document open was aborted");
        try
        {
            await openTextFile(path, cancellationToken);
            return new(true);
        }
        catch (Exception error)
        {
            throw cancellationToken.IsCancellationRequested
                ? new RemoteError("gateway/cancelled", "settings document open was aborted", innerException: error)
                : new RemoteError("gateway/internal", "path open failed: " + error.Message, innerException: error);
        }
    }

    private async Task<SettingsDescribeValue> DescribeAsync(ISettingsProvider provider)
    {
        var snapshot = await provider.DescribeAsync();
        foreach (var diagnostic in snapshot.Diagnostics)
        {
            try
            {
                Caller.Logger.Warn(diagnostic);
            }
            catch
            {
                /* Diagnostics cannot replace the completed read. */
            }
        }

        return new(
            snapshot.Value.Writable,
            snapshot.Value.HasDocument,
            snapshot.Value.Namespaces.Select(Project).ToArray());
    }

    /// <summary>Merge one patch. Omitted expectedRevision skips only the prior form comparison.</summary>
    [RemoteMethod("update")]
    public Task<SettingsNamespaceView> UpdateAsync(
        string ns,
        JsonElement patch,
        [DisallowNull] double? expectedRevision = null!) =>
        WriteAsync(ns, provider => provider.UpdateAsync(ns, patch, expectedRevision));

    /// <summary>Reset live fields against inheritance and merge a section, retaining ordinary configuration.</summary>
    [RemoteMethod("replace")]
    public Task<SettingsNamespaceView> ReplaceAsync(
        string ns,
        JsonElement section,
        [DisallowNull] double? expectedRevision = null!) =>
        WriteAsync(ns, provider => provider.ReplaceAsync(ns, section, expectedRevision));

    /// <summary>Apply ordered form edits, then validate and commit the complete candidate once.</summary>
    [RemoteMethod("mutate")]
    public Task<SettingsNamespaceView> MutateAsync(
        string ns,
        IReadOnlyList<SettingsPathOp> ops,
        [DisallowNull] double? expectedRevision = null!) =>
        WriteAsync(ns, provider => provider.MutateAsync(ns, ops, expectedRevision));

    private async Task<SettingsNamespaceView> WriteAsync(string ns, Func<ISettingsProvider, Task> write)
    {
        if (string.IsNullOrEmpty(ns))
            throw new RemoteError("gateway/bad-request", "Settings namespace must be a non-empty string.");
        var provider = Provider();
        try
        {
            await write(provider);
        }
        catch (SettingsConflictException conflict)
        {
            throw new RemoteError(
                "settings/conflict",
                conflict.Message,
                Details(ns, conflict.Expected, conflict.Actual));
        }
        catch (Exception error)
        {
            throw new RemoteError("settings/rejected", error.Message, Details(ns));
        }

        var snapshot = await DescribeAsync(provider);
        return snapshot.Namespaces.SingleOrDefault(view => view.Ns == ns) ??
            throw new RemoteError("gateway/internal", "The committed settings namespace is no longer available.");
    }

    private static JsonElement Details(string ns, double? expected = null, double? actual = null)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("ns", ns);
            if (expected is { } prior)
                writer.WriteNumber("expected", prior);
            if (actual is { } current)
                writer.WriteNumber("actual", current);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }

    private static SettingsNamespaceView Project(SettingsNamespaceView view) => new(
        view.Ns,
        view.AutoGenerate,
        view.Schema.Clone(),
        view.Value.Clone(),
        view.Revision)
    {
        Applies = "live",
        Base = view.Base?.Clone(),
        User = view.User?.Clone(),
        Secrets = view.Secrets.Select(secret => new SettingsSecretView(secret.Path.ToArray(), secret.Set)).ToArray()
    };
}

/// <summary>Explicit static metadata for the single Settings wire contract.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SettingsDescribeValue))]
[JsonSerializable(typeof(SettingsDocumentOpenValue))]
[JsonSerializable(typeof(SettingsNamespaceView))]
[JsonSerializable(typeof(IReadOnlyList<SettingsPathOp>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double))]
public partial class SettingsJson : JsonSerializerContext;

/// <summary>Confirmation that the host opener accepted the settings document.</summary>
/// <param name="Opened">True after the opener accepts the path.</param>
public sealed record SettingsDocumentOpenValue(bool Opened);
