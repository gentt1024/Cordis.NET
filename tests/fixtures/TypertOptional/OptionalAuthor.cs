using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis.Composition;

namespace OptionalAuthor;

[RemoteService("optional", typeof(OptionalJson), Namespace = "optional")]
public sealed partial class OptionalService
{
    public int Calls
    {
        get;
        private set;
    }

    [RemoteMethod("edit")]
    public Task<string> Edit(JsonElement candidate, [DisallowNull] double? expectedRevision = null!)
    {
        Calls++;
        return Task.FromResult(
            candidate.GetProperty("value").GetString() + ":" +
            (expectedRevision?.ToString(CultureInfo.InvariantCulture) ?? "absent"));
    }

    [RemoteMethod("defaults")]
    public Task<string> Defaults(
        int limit = 7,
        string? text = null,
        bool enabled = false,
        float ratio = 1.25f,
        decimal amount = 0.1m)
    {
        Calls++;
        return Task.FromResult(
            limit.ToString(CultureInfo.InvariantCulture) + ":" +
            (text ?? "null") + ":" + enabled.ToString(CultureInfo.InvariantCulture) + ":" +
            ratio.ToString(CultureInfo.InvariantCulture) + ":" + amount.ToString(CultureInfo.InvariantCulture));
    }

    [RemoteMethod("nullable")]
    public Task<double?> Nullable(double? revision = null)
    {
        Calls++;
        return Task.FromResult(revision);
    }

    [RemoteMethod("required")]
    public Task<double> Required(double revision)
    {
        Calls++;
        return Task.FromResult(revision);
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
public partial class OptionalJson : JsonSerializerContext;
