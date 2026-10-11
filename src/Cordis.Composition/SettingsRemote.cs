using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>An optional ordinary service supplying a selected native settings describe snapshot.</summary>
public interface ISettingsDescribeProvider
{
    /// <summary>Read the provider's current namespaces and retain native projection diagnostics.</summary>
    Task<SettingsDescribeSnapshot> DescribeAsync();
}

/// <summary>Project one existing profile's host-selected live settings without adding a lifecycle.</summary>
public sealed class ProfileSettingsDescribeProvider : ISettingsDescribeProvider
{
    private readonly PluginConfigurationOperations operations;
    private readonly IReadOnlyList<SettingsNamespaceSelection> selections;
    private readonly bool writable;
    private readonly bool hasDocument;

    /// <summary>Copy namespace selection and retain explicit host deployment facts.</summary>
    public ProfileSettingsDescribeProvider(
        PluginConfigurationOperations operations,
        IEnumerable<SettingsNamespaceSelection> selections,
        bool writable,
        bool hasDocument)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations;
        this.selections = PluginConfigurationOperations.SnapshotSettingsSelections(selections);
        this.writable = writable;
        this.hasDocument = hasDocument;
    }

    /// <summary>Read all selected namespaces through one existing profile transaction.</summary>
    public Task<SettingsDescribeSnapshot> DescribeAsync() =>
        operations.DescribeSettingsAsync(selections, writable, hasDocument);
}

/// <summary>Optional Remote projection over the ordinary settings provider.</summary>
[RemoteService("settingsController", typeof(SettingsJson), Namespace = "settings")]
public partial class SettingsController
{
    private readonly Context context;

    /// <summary>Publish the controller independently of its optional settings provider.</summary>
    public SettingsController(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
        context.Provide("settingsController", this);
    }

    /// <summary>Read the current optional provider and return only the fixed describe fields.</summary>
    [RemoteMethod("describe")]
    public async Task<SettingsDescribeValue> DescribeAsync()
    {
        var caller = TypertInvocation.Current?.Context ?? context;
        var provider = caller.Get<ISettingsDescribeProvider>("settings", strict: false) ??
            throw new RemoteError(
                "gateway/internal",
                "settings service is absent: provide ISettingsDescribeProvider as 'settings' in the profile composition");
        var snapshot = await provider.DescribeAsync();
        var value = new SettingsDescribeValue(
            snapshot.Value.Writable,
            snapshot.Value.HasDocument,
            Array.AsReadOnly(
                snapshot
                    .Value.Namespaces.Select(view => new SettingsNamespaceView(
                        view.Ns,
                        view.AutoGenerate,
                        view.Schema.Clone(),
                        view.Value.Clone(),
                        view.Revision)
                    {
                        Applies = "live",
                        Base = view.Base?.Clone(),
                        User = view.User?.Clone(),
                        Secrets = Array.AsReadOnly(
                            view
                                .Secrets.Select(secret =>
                                    new SettingsSecretView(Array.AsReadOnly(secret.Path.ToArray()), secret.Set))
                                .ToArray())
                    })
                    .ToArray()));
        foreach (var diagnostic in snapshot.Diagnostics)
        {
            try
            {
                caller.Logger.Warn(diagnostic);
            }
            catch
            {
                // A diagnostic observer cannot reject an already completed settings read.
            }
        }

        return value;
    }
}

/// <summary>Explicit static metadata for the selected settings wire contract.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SettingsDescribeValue))]
public partial class SettingsJson : JsonSerializerContext;
