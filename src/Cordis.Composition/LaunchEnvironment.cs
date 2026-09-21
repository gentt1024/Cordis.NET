using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Cordis.Composition;

/// <summary>
/// Represents the environment value component.
/// </summary>
/// <param name="Value">The value value.</param>
/// <param name="Source">The source value.</param>
/// <param name="Path">The path value.</param>
public sealed record EnvironmentValue(string Value, string Source, string? Path = null);
/// <summary>
/// Represents the environment layer component.
/// </summary>
/// <param name="Source">The source value.</param>
/// <param name="Values">The values value.</param>
/// <param name="Path">The path value.</param>
public sealed record EnvironmentLayer(string Source, IReadOnlyDictionary<string, string> Values, string? Path = null);
/// <summary>
/// Represents the launch environment snapshot component.
/// </summary>
/// <param name="layers">The layers value.</param>
public sealed class LaunchEnvironmentSnapshot(IReadOnlyList<EnvironmentLayer> layers)
{
    private readonly EnvironmentLayer[] _layers = layers.Select(layer => layer with
    { Values = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(layer.Values, StringComparer.Ordinal)) }).ToArray();

    /// <summary>
    /// Gets the requested value.
    /// </summary>
    public EnvironmentValue? Get(string name) => GetFrom(name, ["process", "project-env", "user-env"]);
    /// <summary>
    /// Gets from.
    /// </summary>
    public EnvironmentValue? GetFrom(string name, IReadOnlyCollection<string> sources)
    {
        foreach (var layer in _layers)
            if (sources.Contains(layer.Source) && layer.Values.TryGetValue(name, out var value)) return new(value, layer.Source, layer.Path);
        return null;
    }
}

/// <summary>Optional DSH bootstrap policy. It never changes Core service or plugin semantics.</summary>
public static partial class LaunchEnvironment
{
    /// <summary>
    /// Loads the requested value.
    /// </summary>
    public static void Load(IDictionary<string, string> environment, Action<string>? warn = null, string diagnosticName = "cordis") =>
        Load(Environment.CurrentDirectory, environment, warn, diagnosticName);

    private static readonly HashSet<string> BootstrapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USERPROFILE", "SHELL", "NODE_OPTIONS", "NODE_PATH", "NODE_EXTRA_CA_CERTS",
        "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "BASH_ENV", "ENV", "SHELLOPTS", "BASHOPTS",
        "PERL5OPT", "PERL5LIB", "PYTHONSTARTUP", "PYTHONPATH", "RUBYOPT", "RUBYLIB", "JAVA_TOOL_OPTIONS",
        "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "PYTHONHOME", "GIT_SSH", "GIT_SSH_COMMAND", "GIT_EXTERNAL_DIFF",
        "GIT_PAGER", "GIT_EDITOR", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM",
        "GIT_CONFIG_COUNT", "EDITOR", "VISUAL", "PAGER", "BROWSER", "DEEPSEEK_BASE_URL", "DEEPSEEK_SEARCH_BASE_URL",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE", "NODE_TLS_REJECT_UNAUTHORIZED"
    };
    private static readonly string[] BootstrapPrefixes = ["DSH_", "XDG_", "DYLD_", "BASH_FUNC_"];
    private static readonly HashSet<string> HomeProxyNames = new(["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves config path.
    /// </summary>
    public static string ResolveConfigPath(string path, string? snapshotMode = null, string? currentDirectory = null)
    {
        var absolute = System.IO.Path.GetFullPath(path, currentDirectory ?? Environment.CurrentDirectory);
        return snapshotMode != "replay" ? absolute : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(absolute)!,
            ConfigBasename().Replace(System.IO.Path.GetFileName(absolute), "cordis.snapshot.yml"));
    }
    [GeneratedRegex("cordis\\.ya?ml$")]
    private static partial Regex ConfigBasename();

    /// <summary>Read and validate every layer before materializing any new value into the supplied environment.</summary>
    public static LaunchEnvironmentSnapshot LoadLayered(string home, string currentDirectory, IDictionary<string, string> environment,
        Action<string>? warn = null, string diagnosticName = "cordis")
    {
        home = System.IO.Path.GetFullPath(home); currentDirectory = System.IO.Path.GetFullPath(currentDirectory);
        var inherited = new Dictionary<string, string>(environment, StringComparer.Ordinal);
        var project = ReadLayer(currentDirectory, "project-env", home, warn, diagnosticName);
        var user = home == currentDirectory ? null : ReadLayer(home, "user-env", home, warn, diagnosticName);
        var layers = new List<EnvironmentLayer> { new("process", inherited) };
        foreach (var layer in new[] { project, user })
        {
            if (layer is null) continue;
            layers.Add(layer);
            foreach (var (name, value) in layer.Values) if (!environment.ContainsKey(name)) environment[name] = value;
        }
        return new(layers);
    }

    /// <summary>
    /// Loads the requested value.
    /// </summary>
    public static void Load(string directory, IDictionary<string, string> environment, Action<string>? warn = null, string diagnosticName = "cordis")
    {
        var text = ReadFile(directory, warn, diagnosticName);
        if (text is null) return;
        foreach (var (name, value) in Parse(text)) if (!environment.ContainsKey(name)) environment[name] = value;
    }

    private static EnvironmentLayer? ReadLayer(string directory, string source, string home, Action<string>? warn, string name)
    {
        var text = ReadFile(directory, warn, name);
        if (text is null) return null;
        var values = Parse(text);
        foreach (var key in values.Keys)
        {
            if (!BootstrapNames.Contains(key) && !BootstrapPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
            if (directory == home && HomeProxyNames.Contains(key)) continue;
            var remedy = HomeProxyNames.Contains(key)
                ? $"export {key}, or put it in {System.IO.Path.Combine(home, ".env")}, which does not travel with a repository"
                : $"export {key} instead of putting it in a .env file";
            throw new InvalidOperationException($"{name}: {System.IO.Path.Combine(directory, ".env")} sets \"{key}\", which only the launching environment may set; {remedy}");
        }
        return new(source, values, System.IO.Path.Combine(directory, ".env"));
    }

    private static string? ReadFile(string directory, Action<string>? warn, string name)
    {
        try { return File.ReadAllText(System.IO.Path.Combine(directory, ".env")); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { (warn ?? Console.Error.Write)($"{name}: failed to load .env: {error.Message.ReplaceLineEndings(" ")}\n"); return null; }
    }

    /// <summary>Data-only dotenv parser: export prefix, comments, quotes and multiline quoted values.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int cursor = 0;
        while (cursor < text.Length)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length) break;
            if (text[cursor] == '#') { SkipLine(); continue; }
            if (text.AsSpan(cursor).StartsWith("export ", StringComparison.Ordinal)) cursor += 7;
            int start = cursor;
            while (cursor < text.Length && text[cursor] is not ('=' or '\r' or '\n')) cursor++;
            if (cursor == text.Length || text[cursor] != '=') { SkipLine(); continue; }
            var key = text[start..cursor].Trim(); cursor++;
            while (cursor < text.Length && text[cursor] is ' ' or '\t') cursor++;
            string value;
            if (cursor < text.Length && text[cursor] is '\'' or '"' or '`')
            {
                char quote = text[cursor++]; var builder = new StringBuilder();
                while (cursor < text.Length && text[cursor] != quote)
                {
                    if (quote == '"' && text[cursor] == '\\' && cursor + 1 < text.Length && text[cursor + 1] is 'n' or 'r')
                    { builder.Append(text[cursor + 1] == 'n' ? '\n' : '\r'); cursor += 2; }
                    else builder.Append(text[cursor++]);
                }
                value = builder.ToString(); if (cursor < text.Length) cursor++; SkipLine();
            }
            else
            {
                start = cursor; while (cursor < text.Length && text[cursor] is not ('\r' or '\n' or '#')) cursor++;
                value = text[start..cursor].Trim(); SkipLine();
            }
            if (key.Length > 0) values[key] = value;
        }
        return values;
        void SkipLine() { while (cursor < text.Length && text[cursor] != '\n') cursor++; if (cursor < text.Length) cursor++; }
    }
}
