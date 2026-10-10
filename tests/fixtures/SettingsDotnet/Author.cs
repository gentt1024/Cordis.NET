using System.Text.Json.Serialization;
using Cordis;
using Cordis.Clr;
using Cordis.Composition;

namespace IndependentSettings;

public sealed class First : IClrPluginModule, IClrTypertModule
{
    public IPlugin CreatePlugin() => Entries.Create(controller: true);

    public TypertContribution CreateTypertContribution() =>
        SettingsRemoteTypert.Contribution("IndependentSettings.Provider");
}

public sealed class Second : IClrPluginModule
{
    public IPlugin CreatePlugin() => Entries.Create(controller: false);
}

[RemoteService("settingsController", typeof(AuthorSettingsJson), Namespace = "settings")]
public sealed partial class SettingsRemote(Context context) : SettingsController(context)
{
    [RemoteMethod("describe")]
    public new Task<SettingsDescribeValue> DescribeAsync() => base.DescribeAsync();
}

public sealed record NestedOptions(string Visible = "nested-visible", string Secret = "nested-default-secret");

public sealed record EntryConfig(
    string Label = "default-label",
    string RootSecret = "root-default-secret",
    NestedOptions? Nested = null,
    string Hidden = "hidden-value",
    string? GenuineNull = null,
    int Defaulted = 7);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SettingsDescribeValue))]
[JsonSerializable(typeof(EntryConfig))]
public partial class AuthorSettingsJson : JsonSerializerContext;

internal static class Entries
{
    internal static IPlugin Create(bool controller)
    {
        var bind = ConfigBinding.FromJsonTypeInfo(AuthorSettingsJson.Default.EntryConfig);
        var schema = ConfigObject<EntryConfig>
            .Create(bind)
            .Field("label", ConfigDescriptor.String().Default("default-label").Volatile(), options => options.Label)
            .Field("rootSecret", Secret().Default("root-default-secret").Volatile(), options => options.RootSecret)
            .Field(
                "nested",
                ConfigDescriptor
                    .Object(
                        ("visible", ConfigDescriptor.String().Default("nested-visible")),
                        ("secret", Secret().Default("nested-default-secret")))
                    .Default(NestedData(new NestedOptions()))
                    .Volatile(),
                options => NestedData(options.Nested ?? new NestedOptions()))
            .Field(
                "hidden",
                ConfigDescriptor
                    .String()
                    .Default("hidden-value")
                    .WithAnnotations(
                        new Dictionary<string, object?>
                        {
                            ["hidden"] = true
                        })
                    .Volatile(),
                options => options.Hidden)
            .Field("genuineNull", ConfigDescriptor.Any().Default(null).Volatile(), options => options.GenuineNull)
            .Field("defaulted", ConfigDescriptor.Number().Default(7).Volatile(), options => options.Defaulted)
            .Build();
        return new Plugin<EntryConfig>
        {
            Name = controller ? "independent-settings-first" : "independent-settings-second",
            Configuration = schema,
            Apply = (context, _) =>
            {
                if (controller)
                    new SettingsRemote(context);
            }
        };
    }

    private static ConfigDescriptor Secret() => ConfigDescriptor
        .String()
        .WithAnnotations(
            new Dictionary<string, object?>
            {
                ["role"] = "secret"
            });

    private static IReadOnlyDictionary<string, object?> NestedData(NestedOptions nested) =>
        new Dictionary<string, object?>
        {
            ["visible"] = nested.Visible,
            ["secret"] = nested.Secret
        };
}
