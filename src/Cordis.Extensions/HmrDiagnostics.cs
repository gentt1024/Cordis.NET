using System.Collections;

namespace Cordis.Extensions;

/// <summary>
/// Represents the hmr source location component.
/// </summary>
/// <param name="File">The file value.</param>
/// <param name="Line">The line value.</param>
/// <param name="Column">The column value.</param>
public sealed record HmrSourceLocation(string File, int Line, int Column);
/// <summary>
/// Represents the hmr build diagnostic component.
/// </summary>
/// <param name="Text">The text value.</param>
/// <param name="Location">The location value.</param>
public sealed record HmrBuildDiagnostic(string Text, HmrSourceLocation? Location = null);

/// <summary>A compiler adapter can retain its structured diagnostics through a failed module replacement.</summary>
public sealed class HmrBuildFailureException(IReadOnlyList<HmrBuildDiagnostic> diagnostics) : Exception("Module compilation failed")
{
    /// <summary>
    /// Gets the diagnostics value.
    /// </summary>
    public IReadOnlyList<HmrBuildDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Preserves unknown failures and formats compiler source locations without runtime reflection.</summary>
public static class HmrDiagnostics
{
    /// <summary>
    /// Reports the requested value.
    /// </summary>
    public static void Report(Context context, object? error) => Report(value => context.Logger.Warn(value), error);

    /// <summary>
    /// Reports the requested value.
    /// </summary>
    public static void Report(Action<object?> warn, object? error)
    {
        var diagnostics = Read(error);
        if (diagnostics is null) { warn(error); return; }
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Location is not { } location) { warn(diagnostic.Text); continue; }
            try
            {
                var source = File.ReadAllLines(location.File);
                var line = Math.Clamp(location.Line, 1, Math.Max(1, source.Length));
                var text = source.Length == 0 ? "" : source[line - 1];
                var prefix = $"> {line} | ";
                warn($"File: {location.File}:{location.Line}:{location.Column}\n{prefix}{text}\n{new string(' ', prefix.Length + Math.Max(0, location.Column - 1))}^ {diagnostic.Text}");
            }
            catch (Exception failure) { warn(failure); }
        }
    }

    private static IReadOnlyList<HmrBuildDiagnostic>? Read(object? error)
    {
        if (error is HmrBuildFailureException build) return build.Diagnostics;
        // Dictionaries are the explicit native counterpart of JavaScript diagnostic records.
        if (error is not IDictionary<string, object?> map || !map.TryGetValue("errors", out var raw) || raw is not IList rows) return null;
        var result = new List<HmrBuildDiagnostic>();
        foreach (var row in rows)
        {
            if (row is not IDictionary<string, object?> data || !data.TryGetValue("text", out var value) || value is not string text) return null;
            HmrSourceLocation? location = null;
            if (data.TryGetValue("location", out var site) && site is IDictionary<string, object?> fields
                && fields.TryGetValue("file", out var file) && file is string filename
                && fields.TryGetValue("line", out var line) && line is int lineNumber
                && fields.TryGetValue("column", out var column) && column is int columnNumber)
                location = new(filename, lineNumber, columnNumber);
            result.Add(new(text, location));
        }
        return result;
    }
}
