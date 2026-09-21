using System.Globalization;
using System.Text.RegularExpressions;
using System.Collections;
using System.Text;
using System.Text.Json;

namespace Cordis;

/// <summary>
/// Represents the log level component.
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// Gets the error value.
    /// </summary>
    Error = 0,
    /// <summary>
    /// Gets the info value.
    /// </summary>
    Info = 1,
    /// <summary>
    /// Gets the warn value.
    /// </summary>
    Warn = 2,
    /// <summary>
    /// Gets the debug value.
    /// </summary>
    Debug = 3
}

/// <summary>
/// Represents the log message component.
/// </summary>
/// <param name="Sequence">The sequence value.</param>
/// <param name="Timestamp">The timestamp value.</param>
/// <param name="Name">The name value.</param>
/// <param name="Level">The level value.</param>
/// <param name="Arguments">The arguments value.</param>
/// <param name="Fiber">The fiber value.</param>
public sealed record LogMessage(long Sequence, DateTimeOffset Timestamp, string Name, LogLevel Level, object?[] Arguments, WeakReference<Fiber> Fiber);
/// <summary>
/// Represents the i log exporter component.
/// </summary>
public interface ILogExporter
{
    /// <summary>
    /// Gets the level value.
    /// </summary>
    int Level => (int)LogLevel.Info;

    /// <summary>
    /// Gets the levels value.
    /// </summary>
    IReadOnlyDictionary<string, int>? Levels => null;

    /// <summary>
    /// Performs the export operation.
    /// </summary>
    void Export(LogMessage message);
}

/// <summary>
/// Represents the delegate log exporter component.
/// </summary>
/// <param name="export">The export value.</param>
/// <param name="level">The level value.</param>
public sealed class DelegateLogExporter(Action<LogMessage> export, int level = 1) : ILogExporter
{
    /// <summary>
    /// Gets the level value.
    /// </summary>
    public int Level => level;

    /// <summary>
    /// Performs the export operation.
    /// </summary>
    public void Export(LogMessage message) => export(message);
}

/// <summary>
/// Represents the logger service service.
/// </summary>
/// <param name="context">The context value.</param>
public sealed class LoggerService(Context context)
{
    /// <summary>
    /// Gets the buffer value.
    /// </summary>
    public IReadOnlyList<LogMessage> Buffer => context._runtime.LogBuffer;
    /// <summary>
    /// Gets the buffer size value.
    /// </summary>
    public int BufferSize
    {
        get => context._runtime.BufferSize; set => context._runtime.BufferSize = value;
    }

    /// <summary>Register a host-owned observer. Dispose it explicitly after the teardown it observes.</summary>
    public LogSubscription Subscribe(ILogExporter exporter)
    {
        context.VerifyAccess();
        long id = ++context._runtime.ExporterCounter;
        context._runtime.Exporters.Add(id, exporter);
        return new LogSubscription(() => context._runtime.Execution.RunAsync(() =>
        {
            context._runtime.Exporters.Remove(id);
            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Performs the exporter operation.
    /// </summary>
    public EffectHandle Exporter(ILogExporter exporter) => context.Effect(() =>
    {
        long id = ++context._runtime.ExporterCounter;
        context._runtime.Exporters.Add(id, exporter);
        return (Action)(() => context._runtime.Exporters.Remove(id));
    }, "ctx.logger.exporter()");
    /// <summary>
    /// Creates the requested value.
    /// </summary>
    public Logger Create(string? name = null)
    {
        int? level = null;
        string? interceptedName = null;
        foreach (var config in context.Intercepts("logger"))
        {
            if (config is IReadOnlyDictionary<string, object?> map)
            {
                if (map.TryGetValue("name", out var configured))
                    interceptedName = configured as string;
                if (map.TryGetValue("level", out var configuredLevel))
                    level = Convert.ToInt32(configuredLevel, CultureInfo.InvariantCulture);
            }
        }

        return new Logger(context, name ?? interceptedName ?? Regex.Replace((context.ShadowProvider ?? context).Fiber.Name, "([a-z0-9])([A-Z])", "$1-$2").ToLowerInvariant(), level);
    }

    /// <summary>
    /// Performs the error operation.
    /// </summary>
    public void Error(params object?[] args) => Create().Error(args);
    /// <summary>
    /// Performs the info operation.
    /// </summary>
    public void Info(params object?[] args) => Create().Info(args);
    /// <summary>
    /// Performs the warn operation.
    /// </summary>
    public void Warn(params object?[] args) => Create().Warn(args);
    /// <summary>
    /// Performs the debug operation.
    /// </summary>
    public void Debug(params object?[] args) => Create().Debug(args);
}

/// <summary>
/// Represents the logger component.
/// </summary>
/// <param name="context">The context value.</param>
/// <param name="name">The name value.</param>
/// <param name="level">The level value.</param>
public sealed class Logger(Context context, string name, int? level = null)
{
    /// <summary>
    /// Gets the name value.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Performs the error operation.
    /// </summary>
    public void Error(params object?[] args) => Write(LogLevel.Error, args);
    /// <summary>
    /// Performs the info operation.
    /// </summary>
    public void Info(params object?[] args) => Write(LogLevel.Info, args);
    /// <summary>
    /// Performs the warn operation.
    /// </summary>
    public void Warn(params object?[] args) => Write(LogLevel.Warn, args);
    /// <summary>
    /// Performs the debug operation.
    /// </summary>
    public void Debug(params object?[] args) => Write(LogLevel.Debug, args);
    /// <summary>
    /// Performs the write operation.
    /// </summary>
    public void Write(LogLevel severity, params object?[] args)
    {
        context.VerifyAccess();
        if (args.Length == 1 && args[0] is AggregateException aggregate)
        {
            foreach (var error in aggregate.InnerExceptions)
                Write(severity, error);
            return;
        }

        if (args.Length == 1 && args[0] is Exception { InnerException: { } inner })
            Write(severity, inner);
        var runtime = context._runtime;
        var message = new LogMessage(++runtime.MessageCounter, DateTimeOffset.UtcNow, Name, severity, args, new((context.ShadowProvider ?? context).Fiber));
        if ((level ?? 1) >= (int)severity)
        {
            runtime.LogBuffer.Add(message);
            if (runtime.LogBuffer.Count > runtime.BufferSize)
                runtime.LogBuffer.RemoveAt(0);
        }

        foreach (var exporter in runtime.Exporters.Values.ToArray())
        {
            int threshold = exporter.Levels is { } levels && levels.TryGetValue(Name, out var namedLevel) ? namedLevel : exporter.Levels is { } defaults && defaults.TryGetValue("default", out var defaultLevel) ? defaultLevel : level ?? exporter.Level;
            if (threshold >= (int)severity)
                exporter.Export(message);
        }
    }

    /// <summary>
    /// Performs the format operation.
    /// </summary>
    public static string Format(LogMessage message, int maxLength = 10240, IReadOnlyDictionary<char, Func<object?, string>>? formatters = null)
    {
        var args = new Queue<object?>(message.Arguments);
        object? first = args.Count == 0 ? "" : args.Dequeue();
        string format = first switch
        {
            string text => text,
            Exception error => error.ToString(),
            _ => FormatData(first)
        };
        format = Regex.Replace(format, "%([a-zA-Z%])", match =>
        {
            char token = match.Value[1];
            if (token == '%')
                return "%";
            if (token is not ('s' or 'd' or 'i' or 'f' or 'o' or 'O' or 'c' or 'C') && formatters?.ContainsKey(token) != true)
                return match.Value;
            var value = args.Count == 0 ? Undefined.Value : args.Dequeue();
            if (formatters?.TryGetValue(token, out var custom) == true)
                return custom(value);
            return token switch
            {
                'c' => "",
                'd' or 'i' => Math.Truncate(Number(value)).ToString(CultureInfo.InvariantCulture),
                'f' => Number(value).ToString(CultureInfo.InvariantCulture),
                'o' or 'O' => FormatData(value),
                _ => value?.ToString() ?? "null",
            };
        });
        foreach (var value in args)
            format += " " + (value is not null && value is not string && value is not ValueType ? FormatData(value) : value?.ToString() ?? "null");
        return string.Join('\n', format.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Length <= maxLength ? line : line[..maxLength] + "..."));
    }

    private static double Number(object? value) => value switch
    {
        null => 0,
        Undefined => double.NaN,
        bool flag => flag ? 1 : 0,
        string text => text.Trim().Length == 0 ? 0 : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN,
        IConvertible convertible => ConvertNumber(convertible),
        _ => double.NaN,
    };
    private static double ConvertNumber(IConvertible value)
    {
        try
        {
            return value.ToDouble(CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
        {
            return double.NaN;
        }
    }

    /// <summary>Formats the portable data domain without inspecting arbitrary CLR properties.</summary>
    public static string FormatData(object? value)
    {
        if (value is Undefined)
            return "undefined";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, value, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, object? value, HashSet<object> seen)
    {
        switch (value)
        {
            case null or Undefined:
                writer.WriteNullValue();
                return;
            case JsonElement json:
                json.WriteTo(writer);
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool flag:
                writer.WriteBooleanValue(flag);
                return;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                writer.WriteRawValue(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                return;
            case double number:
                if (double.IsFinite(number))
                    writer.WriteNumberValue(number);
                else
                    writer.WriteNullValue();
                return;
            case float number:
                if (float.IsFinite(number))
                    writer.WriteNumberValue(number);
                else
                    writer.WriteNullValue();
                return;
        }

        if (!seen.Add(value))
            throw new InvalidOperationException("Cannot format circular data.");
        try
        {
            if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                writer.WriteStartObject();
                foreach (var pair in pairs)
                {
                    if (pair.Value is Undefined)
                        continue;
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value, seen);
                }

                writer.WriteEndObject();
            }
            else if (value is IDictionary dictionary)
            {
                writer.WriteStartObject();
                foreach (DictionaryEntry pair in dictionary)
                {
                    if (pair.Value is Undefined)
                        continue;
                    writer.WritePropertyName(Convert.ToString(pair.Key, CultureInfo.InvariantCulture)!);
                    Write(writer, pair.Value, seen);
                }

                writer.WriteEndObject();
            }
            else if (value is IEnumerable list)
            {
                writer.WriteStartArray();
                foreach (var item in list)
                    Write(writer, item, seen);
                writer.WriteEndArray();
            }
            else
                writer.WriteStringValue(value.ToString());
        }
        finally
        {
            seen.Remove(value);
        }
    }
}

/// <summary>A host-owned logger subscription independent of any fiber teardown snapshot.</summary>
public sealed class LogSubscription : IDisposable, IAsyncDisposable
{
    private readonly Func<Task> remove;
    internal LogSubscription(Func<Task> remove) => this.remove = remove;
    /// <summary>Queues removal in the execution domain; use DisposeAsync to join it from another thread.</summary>
    public void Dispose() => _ = remove();
    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public ValueTask DisposeAsync() => new(remove());
}
