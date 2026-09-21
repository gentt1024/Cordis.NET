namespace Cordis.Composition;

/// <summary>A root-owned callable matching the DSH configuration path helper.</summary>
public delegate string DshHomePath(params string[] segments);

/// <summary>Immutable DSH home snapshot. Construction reads the launching environment once; calls never read process state.</summary>
public sealed class DshHomePaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DshHomePaths"/> type.
    /// </summary>
    public DshHomePaths(string? configured = null, IReadOnlyDictionary<string, string>? environment = null,
        string? userHome = null, string? currentDirectory = null)
    {
        userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        currentDirectory ??= Environment.CurrentDirectory;
        var inherited = environment is null ? Environment.GetEnvironmentVariable("DSH_HOME") : environment.GetValueOrDefault("DSH_HOME");
        var selected = configured ?? (!string.IsNullOrWhiteSpace(inherited) ? inherited : Path.Combine(userHome, ".dsh"));
        if (selected == "~") selected = userHome;
        else if (selected.StartsWith("~/", StringComparison.Ordinal) || selected.StartsWith("~\\", StringComparison.Ordinal)) selected = Path.Join(userHome, selected[2..]);
        Home = Path.GetFullPath(selected, currentDirectory);
    }
    /// <summary>
    /// Gets the home value.
    /// </summary>
    public string Home { get; }
    /// <summary>
    /// Performs the path of operation.
    /// </summary>
    public string PathOf(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var segment in segments) ArgumentNullException.ThrowIfNull(segment);
        return Path.GetFullPath(Path.Join(new[] { Home }.Concat(segments).ToArray()));
    }
}
