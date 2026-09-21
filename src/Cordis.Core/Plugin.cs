namespace Cordis;
/// <summary>A reusable plugin definition. The same definition can own several fibers.</summary>
/// <typeparam name = "T">The plugin's validated configuration type.</typeparam>
public sealed class Plugin<T> : IPlugin
{
    /// <summary>The diagnostic name; it is not a service name or a Loader entry id.</summary>
    public string? Name
    {
        get; init;
    }
    /// <summary>Services required by the complete plugin. Array form supplies no intercept configuration.</summary>
    public IReadOnlyList<string> Inject { get; init; } = [];
    /// <summary>Optional synchronous validation/conversion, performed before each activation.</summary>
    public Func<object?, ConfigResult<T>>? Config
    {
        get; init;
    }
    /// <summary>Synchronous apply body. Specify exactly one of Apply, ApplyAsync and ApplyEffect.</summary>
    public Action<Context, T>? Apply
    {
        get; init;
    }
    /// <summary>Asynchronous apply body. The fiber remains Loading until the returned Task settles.</summary>
    public Func<Context, T, Task>? ApplyAsync
    {
        get; init;
    }
    /// <summary>
    /// Gets the inject config value.
    /// </summary>
    public IReadOnlyDictionary<string, object?> InjectConfig { get; init; } = new Dictionary<string, object?>();
    /// <summary>
    /// Gets the apply effect value.
    /// </summary>
    public Func<Context, T, IAsyncDisposable?>? ApplyEffect
    {
        get; init;
    }

    object IPlugin.Identity => (object?)Apply ?? (object?)ApplyAsync ?? ApplyEffect!;

    IReadOnlyDictionary<string, object?> IPlugin.Dependencies => Inject
        .Distinct(StringComparer.Ordinal)
        .Select(name => KeyValuePair.Create(name, (object?)null))
        .Concat(InjectConfig)
        .GroupBy(pair => pair.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

    object? IPlugin.ResolveConfig(object? raw) => Config is null ? (T)raw! : Config(raw).GetValue();
    Task IPlugin.ApplyAsync(Context ctx, object? config) => Capture().Apply(ctx, config);
    internal PluginDefinition Capture()
    {
        if ((Apply is null ? 0 : 1) + (ApplyAsync is null ? 0 : 1) + (ApplyEffect is null ? 0 : 1) != 1)
            throw new ArgumentException("Specify exactly one of Apply, ApplyAsync and ApplyEffect.");
        ArgumentNullException.ThrowIfNull(Inject);
        string[] inject = Inject.ToArray();
        foreach (string name in inject)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Action<Context, T>? sync = Apply;
        Func<Context, T, Task>? async = ApplyAsync;
        Func<object?, ConfigResult<T>>? validate = Config;
        return new PluginDefinition((object?)sync ?? (object?)async ?? ApplyEffect!, Name, ((IPlugin)this).Dependencies, raw => validate is null ? (T)raw! : validate(raw).GetValue(), (ctx, value) =>
        {
            if (sync is not null)
            {
                sync(ctx, (T)value!);
                return Task.CompletedTask;
            }

            if (ApplyEffect is not null)
            {
                ctx.Effect(() => ApplyEffect(ctx, (T)value!)!);
                return Task.CompletedTask;
            }

            return async!(ctx, (T)value!) ?? throw new InvalidOperationException("ApplyAsync returned a null Task.");
        });
    }
}

internal sealed record PluginDefinition(object Identity, string? Name, IReadOnlyDictionary<string, object?> Inject, Func<object?, object?> ResolveConfig, Func<Context, object?, Task> Apply);
/// <summary>A small value/issues adapter; it does not impose a JSON or DataAnnotations schema.</summary>
public sealed class ConfigResult<T>
{
    private readonly T? _value;
    private readonly string[] _issues;
    private ConfigResult(T? value, string[] issues) => (_value, _issues) = (value, issues);
    /// <summary>
    /// Performs the success operation.
    /// </summary>
    public static ConfigResult<T> Success(T value) => new(value, []);
    /// <summary>
    /// Performs the failure operation.
    /// </summary>
    public static ConfigResult<T> Failure(params string[] issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Length == 0 || issues.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one nonblank issue is required.", nameof(issues));
        return new(default, issues.ToArray());
    }

    internal T GetValue() => _issues.Length == 0 ? _value! : throw new ConfigurationValidationException((IEnumerable<string>)_issues);
}

/// <summary>A rejected configuration. The validator determines the rules, not the serializer.</summary>
public sealed class ConfigurationValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationValidationException"/> type.
    /// </summary>
    public ConfigurationValidationException(IEnumerable<string> issues) : this((issues ?? throw new ArgumentNullException(nameof(issues))).ToArray())
    {
    }

    private ConfigurationValidationException(string[] issues) : base("Invalid configuration: " + string.Join("; ", issues)) => Issues = Array.AsReadOnly(issues);
    /// <summary>
    /// Gets the issues value.
    /// </summary>
    public IReadOnlyList<string> Issues
    {
        get;
    }
}

/// <summary>Numeric values match the frozen DSH FiberState values.</summary>
public enum FiberState
{
    /// <summary>
    /// Gets the pending value.
    /// </summary>
    Pending = 0,
    /// <summary>
    /// Gets the loading value.
    /// </summary>
    Loading = 1,
    /// <summary>
    /// Gets the active value.
    /// </summary>
    Active = 2,
    /// <summary>
    /// Gets the failed value.
    /// </summary>
    Failed = 3,
    /// <summary>
    /// Gets the disposed value.
    /// </summary>
    Disposed = 4,
    /// <summary>
    /// Gets the unloading value.
    /// </summary>
    Unloading = 5,
}

/// <summary>A Cordis contract violation with a stable machine-readable code.</summary>
public sealed class CordisException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>
    /// Gets the code value.
    /// </summary>
    public string Code { get; } = code;
}

/// <summary>Explicit plugin entry contract shared by static and collectible CLR modules.</summary>
public interface IPlugin
{
    /// <summary>
    /// Gets the identity value.
    /// </summary>
    object Identity
    {
        get;
    }

    /// <summary>
    /// Gets the name value.
    /// </summary>
    string? Name
    {
        get;
    }

    /// <summary>
    /// Gets the dependencies value.
    /// </summary>
    IReadOnlyDictionary<string, object?> Dependencies
    {
        get;
    }

    /// <summary>
    /// Resolves config.
    /// </summary>
    object? ResolveConfig(object? configuration);
    /// <summary>
    /// Applies async.
    /// </summary>
    Task ApplyAsync(Context context, object? configuration);
}
