using Cordis.AspNetCore;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cordis.Platform.Tests;

internal static class ManagementTestHost
{
    internal static async Task MountAsync(
        WebApplication server,
        ProfileSession session,
        CordisManagement management,
        TypertRegistry? registry = null,
        Func<HttpContext, string, Task<bool>>? authorize = null)
    {
        registry ??= new(session.Context);
        var gateway = new TypertGateway(session.Context, registry);
        await management.MountPluginManagerAsync(
            session.Context,
            registry,
            server.Services.GetRequiredService<IHttpContextAccessor>());
        await session.Context.RunAsync(context =>
        {
            new RemoteNotifications(context, gateway);
            registry.Register(context, RemoteNotificationsTypert.Contribution("Cordis.Tests.Notifications"));
            gateway.RegisterRemoteEvents(
                context,
                TypertRemoteEvents.FromCordis(
                    context,
                    [
                        new(
                            "plugin-manager/changed",
                            [TypertCodec.Create(PluginManagerJson.Default.PluginManagerChanged)]),
                        new(
                            "plugin-manager/install-state",
                            [TypertCodec.Create(PluginManagerJson.Default.PackageProgress)]),
                        new(
                            "plugin-manager/install-log",
                            [TypertCodec.Create(PluginManagerJson.Default.PackageProgress)]),
                    ]));
            return Task.CompletedTask;
        });
        management.Map(server);
        server.MapCordisRemote("/remote", gateway, authorize ?? ((_, _) => Task.FromResult(true)));
    }
}
