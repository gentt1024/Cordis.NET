using Cordis;
namespace Cordis.Composition;

/// <summary>
/// Represents the include options component.
/// </summary>
/// <param name="Path">The path value.</param>
/// <param name="Initial">The initial value.</param>
/// <param name="Patches">The patches value.</param>
/// <param name="EnableLogs">The enable logs value.</param>
public sealed record IncludeOptions(string Path, List<EntryOptions>? Initial = null, List<EntryOptions>? Patches = null, bool EnableLogs = false)
{
    /// <summary>
    /// Performs the from operation.
    /// </summary>
    public static IncludeOptions From(object? raw)
    {
        if (raw is IncludeOptions options) return options;
        if (raw is not IDictionary<string, object?> map || map.GetValueOrDefault("path") is not string path) throw new FormatException("Include config requires a path.");
        return new(path, map.TryGetValue("initial", out var initial) && initial is not null ? Data.Entries(initial) : null, map.TryGetValue("patches", out var patches) && patches is not null ? Data.Entries(patches) : null, map.GetValueOrDefault("enableLogs") is true);
    }
}

/// <summary>File-backed tree with last-good parse caching and serialized durable writes.</summary>
public sealed class Include : EntryTree
{
    private string? content;
    private List<EntryOptions>? parsed;
    private List<EntryOptions>? pendingWrite;
    private Task writeQueue = Task.CompletedTask;
    private readonly object writeGate = new();
    private CancellationTokenSource? debounce;
    private bool stopping;
    /// <summary>
    /// Gets the options value.
    /// </summary>
    public IncludeOptions Options { get; private set; }
    /// <summary>
    /// Gets the filename value.
    /// </summary>
    public string Filename { get; }
    /// <summary>Injectable filesystem rename boundary for hosts and fault tests.</summary>
    public Func<string, string, Task> ReplaceFileAsync { get; set; } = (source, destination) => { File.Move(source, destination, true); return Task.CompletedTask; };
    /// <summary>
    /// Initializes a new instance of the <see cref="Include"/> type.
    /// </summary>
    public Include(Context context, Loader loader, IncludeOptions options, Uri baseUri, Entry? owner = null)
        : base(context, loader, new Uri(new Uri(baseUri, options.Path), "."), owner)
    {
        Options = options; Filename = new Uri(baseUri, options.Path).LocalPath; EnableLogs = options.EnableLogs;
        if (Path.GetExtension(Filename) is not (".yml" or ".yaml" or ".json")) throw new NotSupportedException($"Extension '{Path.GetExtension(Filename)}' is not supported.");
    }
    private async Task<bool> ReadAsync(bool force = false)
    {
        var incoming = await File.ReadAllTextAsync(Filename);
        if (!force && content == incoming) return false;
        var data = ConfigurationFile.ParseEntries(incoming, Path.GetExtension(Filename) == ".json");
        content = incoming; parsed = data; return true;
    }
    private List<EntryOptions> Patched() => EntryPatches.Apply(parsed!, Options.Patches, message => Loader.Report(new InvalidOperationException(message)));
    /// <summary>
    /// Starts async.
    /// </summary>
    public async Task StartAsync()
    {
        try { await ReadAsync(); }
        catch (FileNotFoundException) when (Options.Initial is not null) { await WriteFileAsync(Options.Initial); await ReadAsync(true); }
        await Root.UpdateAsync(Patched());
    }
    /// <summary>
    /// Refreshes async.
    /// </summary>
    public async Task RefreshAsync()
    {
        try { if (await ReadAsync()) await Root.UpdateAsync(Patched()); }
        catch (Exception error) { Loader.Report(new InvalidOperationException($"Config reload at {Filename} failed; keeping the running tree.", error)); }
    }
    /// <summary>
    /// Updates options async.
    /// </summary>
    public async Task UpdateOptionsAsync(IncludeOptions options)
    {
        if (Options.Path != options.Path) throw new InvalidOperationException("A changed include path requires a new Include instance.");
        Options = options;
        try { await Root.UpdateAsync(Patched()); } catch (Exception error) { Loader.Report(error); }
    }
    private async Task WriteFileAsync(List<EntryOptions> data)
    {
        var text = ConfigurationFile.Write(data, Path.GetExtension(Filename) == ".json");
        content = text;
        await File.WriteAllTextAsync(Filename + ".tmp", text);
        for (var attempt = 0; ; attempt++)
        {
            try { await ReplaceFileAsync(Filename + ".tmp", Filename); return; }
            catch (Exception error) when (attempt < 10 && Retryable(error)) { await Task.Delay((attempt + 1) * 50); }
        }
    }
    private static bool Retryable(Exception error) => error is UnauthorizedAccessException || error is IOException io && (io.HResult & 0xffff) is 5 or 16 or 32 or 33;
    /// <summary>
    /// Performs the write operation.
    /// </summary>
    public override void Write()
    {
        lock (writeGate)
        {
            pendingWrite = Root.Data; debounce?.Cancel(); debounce?.Dispose(); debounce = new CancellationTokenSource();
            if (!stopping) _ = DebouncedWriteAsync(debounce.Token);
        }
    }
    private async Task DebouncedWriteAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(1, cancellationToken); await FlushWriteAsync(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { Loader.Report(error); }
    }
    /// <summary>
    /// Performs the flush write async operation.
    /// </summary>
    public Task FlushWriteAsync()
    {
        lock (writeGate)
        {
            debounce?.Cancel();
            if (pendingWrite is null) return writeQueue;
            var data = pendingWrite; pendingWrite = null; var prior = writeQueue;
            writeQueue = RunAsync(); return writeQueue;
            async Task RunAsync() { try { await prior; } catch { /* A new write may recover a previous failure. */ } await WriteFileAsync(data); }
        }
    }
    /// <summary>
    /// Stops async.
    /// </summary>
    public async Task StopAsync()
    {
        stopping = true;
        try { await FlushWriteAsync(); }
        finally { await Root.StopAsync(); await FlushWriteAsync(); debounce?.Dispose(); }
    }
}

internal static class MappingExtensions
{
    internal static object? GetValueOrDefault(this IDictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) ? value : null;
}
