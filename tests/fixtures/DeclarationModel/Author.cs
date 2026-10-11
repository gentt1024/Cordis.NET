using Cordis;
using IndependentDeclarations.External;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace IndependentDeclarations;

public sealed class Config
{
    public string Label
    {
        get;
        init;
    } = "catalog";

    public bool Enabled
    {
        get;
        set;
    } = true;
}

public sealed class PluginAuthor
{
    public ConfigSchema<Config> Schema
    {
        get;
    } = new(
        raw => ConfigResult<Config>.Success(raw as Config ?? new Config()),
        ConfigDescriptor.Object(("label", ConfigDescriptor.String())));

    public Plugin<Config> Plugin => new()
    {
        Name = "ordinary-catalog",
        Configuration = Schema,
        Apply = (_, _) =>
        {
        }
    };
}

public sealed class CatalogService : Service<Config>
{
    public CatalogService(Context context, Config config) : base(context, "ordinary-catalog", config)
    {
    }

    private CatalogService(CatalogService provider, Context caller) : base(provider, caller)
    {
    }

    public Page<Config> Snapshot() => new()
    {
        Name = State.Label,
        Item = State
    };

    public Page<Config>.Cursor OpenCursor() => new();

    protected override Service CreateView(Context caller) => new CatalogService(this, caller);
}

public interface IPage<out T> where T : class
{
    T? Item
    {
        get;
    }
}

public delegate void PageChanged<T>(Page<T> page, string? reason) where T : class, new();

/// <summary>An ordinary page with no remote serialization contract.</summary>
public sealed class Page<T> : IPage<T> where T : class, new()
{
    public T? Item
    {
        get;
        init;
    }

    public required string Name
    {
        get;
        init;
    }

    public string? Note
    {
        get;
        init;
    }

    public string Label
    {
        get;
        init;
    } = "untitled";

    public int Limit
    {
        get;
        set;
    } = 7;

    [return: MaybeNull]
    public string ReadLabel() => Label;

    private string? accessorLabel;

    public string? AccessorLabel
    {
        [DebuggerStepThrough]
        get
        {
            return accessorLabel;
        }
        [DebuggerNonUserCode]
        [param: DisallowNull]
        set => accessorLabel = value;
    }

    public T? Find(int index) => index >= 0 ? Item : null;

    public T? Find(string key, int limit = 7) => key.Length <= limit ? Item : null;

    public Task<Page<T>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this);
    }

    public TResult Convert<TResult>(T source) where TResult : class, IExternalMarker, new() => new();

    public Envelope<Page<T>> Export() => new()
    {
        Value = this
    };

    public event PageChanged<T>? Changed;

    public void Notify(string? reason) => Changed?.Invoke(this, reason);

    public sealed class Cursor
    {
        public int Offset
        {
            get;
            init;
        }

        public T? Current
        {
            get;
            init;
        }
    }
}

public static class PageExtensions
{
    public static int Count(this Page<Config> page, scoped ReadOnlySpan<byte> data) => page.Limit + data.Length;
}

public sealed class Unselected
{
    public string Value
    {
        get;
        set;
    } = "outside the selected graph";
}

internal sealed class Hidden;

internal sealed class HiddenContainer
{
    public sealed class Nested;
}
