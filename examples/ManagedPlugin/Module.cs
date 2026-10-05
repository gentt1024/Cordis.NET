using Cordis;
using Cordis.Clr;
using Cordis.Composition;

namespace Example.ManagedPlugin;

public sealed class Module : IClrPluginModule
{
    private sealed record Settings(int Limit, string Label, string Credential);

    public IPlugin CreatePlugin() => new Plugin<Settings>
    {
        Configuration = ConfigObject<Settings>.Create(Validate)
            .Field("limit", ConfigDescriptor.Number().WithMetadata(new() { Min = 1, Max = 100, Step = 1,
                Description = "Maximum concurrent work items." }).Default(1).Volatile(), value => value.Limit)
            .Field("label", ConfigDescriptor.String().Default("worker"), value => value.Label)
            .Field("credential", ConfigDescriptor.String().WithMetadata(new() { Role = "secret" }).Default("").Volatile(), value => value.Credential)
            .Build(),
        Apply = (context, _) => context.Provide("application-limit", context.Fiber.GetConfigReference<int>("limit")),
    };

    private static ConfigResult<Settings> Validate(object? raw)
    {
        if (raw is not IReadOnlyDictionary<string, object?> values) return ConfigResult<Settings>.Failure("An object is required.");
        var limit = values.GetValueOrDefault("limit", 1);
        if (limit is not (byte or short or int or long or float or double or decimal)) return ConfigResult<Settings>.Failure("limit must be a number.");
        var number = Convert.ToDouble(limit, System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(number) || number < 1 || number > 100 || Math.Truncate(number) != number)
            return ConfigResult<Settings>.Failure("limit must be an integer from 1 through 100.");
        if (values.GetValueOrDefault("label", "worker") is not string label
            || values.GetValueOrDefault("credential", "") is not string credential)
            return ConfigResult<Settings>.Failure("label and credential must be strings.");
        return ConfigResult<Settings>.Success(new((int)number, label, credential));
    }
}
