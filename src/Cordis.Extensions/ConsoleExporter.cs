using System.Globalization;

namespace Cordis.Extensions;

/// <summary>
/// Represents the console options component.
/// </summary>
public sealed record ConsoleOptions
{
    /// <summary>
    /// Gets the colors value.
    /// </summary>
    public int Colors { get; init; }
    /// <summary>
    /// Gets the max length value.
    /// </summary>
    public int MaxLength { get; init; } = 10240;
    /// <summary>
    /// Gets the levels value.
    /// </summary>
    public IReadOnlyDictionary<string, int>? Levels { get; init; }
    /// <summary>
    /// Gets the show diff value.
    /// </summary>
    public bool ShowDiff { get; init; }
    /// <summary>
    /// Gets the show time value.
    /// </summary>
    public string ShowTime { get; init; } = "yyyy-MM-dd HH:mm:ss ";
    /// <summary>
    /// Gets the label width value.
    /// </summary>
    public int LabelWidth { get; init; }
    /// <summary>
    /// Gets the label margin value.
    /// </summary>
    public int LabelMargin { get; init; } = 1;
    /// <summary>
    /// Gets the label right aligned value.
    /// </summary>
    public bool LabelRightAligned { get; init; }
}

/// <summary>Console exporter; ownership belongs to its context, not the supplied writer.</summary>
public sealed class ConsoleExporter : ILogExporter, IAsyncDisposable
{
    private readonly ConsoleOptions _options;
    private readonly TextWriter _writer;
    private readonly EffectHandle _registration;
    private DateTimeOffset _timestamp;
    /// <summary>
    /// Gets the levels value.
    /// </summary>
    public IReadOnlyDictionary<string, int>? Levels => _options.Levels;
    /// <summary>
    /// Gets the formatters value.
    /// </summary>
    public Dictionary<char, Func<object?, string>> Formatters { get; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleExporter"/> type.
    /// </summary>
    public ConsoleExporter(Context context, ConsoleOptions? options = null, TextWriter? writer = null)
    {
        _options = options ?? new(); _writer = writer ?? Console.Out;
        _timestamp = DateTimeOffset.UtcNow;
        _registration = context.Logger.Exporter(this);
    }

    /// <summary>
    /// Performs the export operation.
    /// </summary>
    public void Export(LogMessage message) => _writer.WriteLine(Render(message));
    /// <summary>
    /// Performs the render operation.
    /// </summary>
    public string Render(LogMessage message)
    {
        var space = new string(' ', Math.Max(0, _options.LabelMargin));
        var prefix = $"[{message.Level.ToString()[0]}]";
        var indent = 3 + space.Length;
        var time = _options.ShowTime.Length == 0 ? "" : message.Timestamp.ToLocalTime().ToString(_options.ShowTime, CultureInfo.InvariantCulture);
        indent += time.Length;
        var label = _options.LabelRightAligned ? message.Name.PadLeft(_options.LabelWidth) : message.Name.PadRight(_options.LabelWidth);
        var coloredLabel = Color(message.Name, label, bold: true);
        var output = _options.LabelRightAligned ? time + coloredLabel + space + prefix + space : time + prefix + space + coloredLabel + space;
        if (_options.LabelRightAligned) indent += _options.LabelWidth + space.Length;
        var formatters = new Dictionary<char, Func<object?, string>>(Formatters);
        formatters.TryAdd('C', value => Color(message.Name, value?.ToString() ?? "null"));
        output += Logger.Format(message, _options.MaxLength, formatters).Replace("\n", "\n" + new string(' ', indent), StringComparison.Ordinal);
        if (_options.ShowDiff) output += Color(message.Name, " +" + Duration((message.Timestamp - _timestamp).TotalMilliseconds));
        _timestamp = message.Timestamp;
        return output;
    }

    private static readonly int[] Palette16 = [6, 2, 3, 4, 5, 1];
    private static readonly int[] Palette256 =
    [
        20, 21, 26, 27, 32, 33, 38, 39, 40, 41, 42, 43, 44, 45, 56, 57, 62,
        63, 68, 69, 74, 75, 76, 77, 78, 79, 80, 81, 92, 93, 98, 99, 112, 113,
        129, 134, 135, 148, 149, 160, 161, 162, 163, 164, 165, 166, 167, 168,
        169, 170, 171, 172, 173, 178, 179, 184, 185, 196, 197, 198, 199, 200,
        201, 202, 203, 204, 205, 206, 207, 208, 209, 214, 215, 220, 221,
    ];
    private string Color(string name, string value, bool bold = false)
    {
        if (_options.Colors == 0) return value;
        int hash = 0;
        foreach (char c in name) hash = unchecked(hash * 7 + c + 13);
        var colors = _options.Colors >= 2 ? Palette256 : Palette16;
        var color = colors[(int)(Math.Abs((long)hash) % colors.Length)];
        var code = color < 8 ? color.ToString(CultureInfo.InvariantCulture) : "8;5;" + color;
        return $"\u001b[3{code}{(_options.Colors >= 2 && bold ? ";1" : "")}m{value}\u001b[0m";
    }

    private static string Duration(double milliseconds)
    {
        var absolute = Math.Abs(milliseconds);
        foreach (var (threshold, divisor, unit) in new (double, double, string)[]
        { (84600000, 86400000, "d"), (3570000, 3600000, "h"), (59500, 60000, "m"), (1000, 1000, "s") })
            if (absolute >= threshold) return Math.Floor(milliseconds / divisor + .5).ToString(CultureInfo.InvariantCulture) + unit;
        return milliseconds.ToString("0.###", CultureInfo.InvariantCulture) + "ms";
    }
    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public ValueTask DisposeAsync() => _registration.DisposeAsync();
}
