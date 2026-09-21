using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cordis.Hosting;

/// <summary>
/// Represents the cordis host options component.
/// </summary>
public sealed class CordisHostOptions
{
    internal List<Action<Context, IServiceProvider>> BorrowedServices { get; } = [];
    internal List<Func<Context, IServiceProvider, CancellationToken, Task>> Startup { get; } = [];

    /// <summary>The DI container owns the service. Cordis only publishes and unpublishes it.</summary>
    public CordisHostOptions Borrow<T>(string name) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        BorrowedServices.Add((context, services) => context.Provide(name, services.GetRequiredService<T>()));
        return this;
    }

    /// <summary>
    /// Configures the requested value.
    /// </summary>
    public CordisHostOptions Configure(Func<Context, IServiceProvider, CancellationToken, Task> startup)
    {
        Startup.Add(startup ?? throw new ArgumentNullException(nameof(startup)));
        return this;
    }
}

/// <summary>
/// Represents the cordis host extensions component.
/// </summary>
public static class CordisHostExtensions
{
    /// <summary>Add one Cordis root and its thin Generic Host lifecycle adapter.</summary>
    public static IServiceCollection AddCordis(this IServiceCollection services, Action<CordisHostOptions>? configure = null)
    {
        var options = new CordisHostOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton<Context>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CordisHostedService>());
        return services;
    }
}

internal sealed class CordisHostedService(Context context, IServiceProvider services, CordisHostOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.RunAsync(async ctx =>
            {
                foreach (var publish in options.BorrowedServices) publish(ctx, services);
                foreach (var start in options.Startup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await start(ctx, services, cancellationToken);
                }
            });
        }
        catch { await context.DisposeAsync(); throw; }
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await context.DisposeAsync();
}
