using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cordis.AspNetCore;

/// <summary>Expose explicitly selected service methods through ASP.NET Core without caching service instances.</summary>
public static class CordisServiceEndpoints
{
    /// <summary>Resolve the current service in the caller's Cordis context for each authorized request.</summary>
    /// <remarks>The host owns the context and authorization policy. Explicit serialization metadata keeps this endpoint
    /// usable in static Native AOT hosts. Request cancellation is passed to the service; it is not an automatic rollback.</remarks>
    public static IEndpointConventionBuilder MapCordisService<TService, TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Context caller,
        string serviceName,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResult> responseType,
        Func<HttpContext, Task<bool>> authorize,
        Func<TService, TRequest, CancellationToken, Task<TResult>> invoke) where TService : class
    {
        ArgumentNullException.ThrowIfNull(authorize);
        return endpoints.MapPost(
            pattern,
            async (HttpContext http) =>
            {
                if (!await authorize(http))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                TRequest? request;
                try
                {
                    request = await JsonSerializer.DeserializeAsync(
                        http.Request.Body,
                        requestType,
                        http.RequestAborted);
                }
                catch (JsonException)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                if (request is null)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                TResult response = default!;
                var available = false;
                try
                {
                    await caller.RunAsync(async context =>
                    {
                        var service = context.Get<TService>(serviceName, strict: false);
                        if (service is null)
                            return;
                        available = true;
                        response = await invoke(service, request, http.RequestAborted);
                    });
                }
                catch (ObjectDisposedException)
                {
                    http.Response.StatusCode = StatusCodes.Status410Gone;
                    return;
                }

                if (!available)
                {
                    http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }

                http.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(http.Response.Body, response, responseType, http.RequestAborted);
            });
    }
}
