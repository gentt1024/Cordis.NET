using Cordis.AspNetCore;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cordis.Platform.Tests;

internal static class ManagementTestHost
{
    internal static async Task<EffectHandle> MountAsync(
        WebApplication server,
        ProfileSession session,
        CordisManagement management,
        TypertRegistry? registry = null,
        Func<HttpContext, string, Task<bool>>? authorize = null)
    {
        registry ??= new(session.Context);
        var gateway = new TypertGateway(session.Context, registry);
        EffectHandle notifications = null!;
        await management.MountPluginManagerAsync(
            session.Context,
            registry,
            server.Services.GetRequiredService<IHttpContextAccessor>());
        await session.Context.RunAsync(context =>
        {
            notifications = gateway.RegisterRemoteEvents(
                context,
                TypertRemoteEvents.FromCordis(
                    context,
                    [
                        PluginManagerChangedEventTypert.Definition,
                        PluginInstallStateEventTypert.Definition,
                        PluginInstallLogEventTypert.Definition,
                    ]),
                new TypertRemoteEventHost(""));
            return Task.CompletedTask;
        });
        management.Map(server);
        TypertHostConnection remoteConnection = null!;
        await session.Context.RunAsync(owner =>
        {
            remoteConnection = new(owner, gateway);
            return Task.CompletedTask;
        });
        server.UseWebSockets();
        server.MapCordisRemote("/remote", remoteConnection, authorize ?? ((_, _) => Task.FromResult(true)));
        return notifications;
    }
}
