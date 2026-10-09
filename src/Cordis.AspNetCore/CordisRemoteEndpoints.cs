using System.Text.Json;
using Cordis.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cordis.AspNetCore;

/// <summary>Native HTTP carrier for the generic Typert Gateway. The host retains endpoint authorization.</summary>
public static class CordisRemoteEndpoints
{
    /// <summary>Map unary JSON and downlink JSON-lines carriers. This is an explicit native transport adaptation.</summary>
    public static IEndpointConventionBuilder MapCordisRemote(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        TypertGateway gateway,
        Func<HttpContext, string, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(authorize);
        var group = endpoints.MapGroup(prefix);
        group.MapPost("/{remoteNamespace}/{method}", (RequestDelegate)(http => Handle(http, Endpoint(http), false)));
        group.MapPost(
            "/{remoteNamespace}/{method}/stream",
            (RequestDelegate)(http => Handle(http, Endpoint(http), true)));
        return group;

        static string Endpoint(HttpContext http) =>
            http.Request.RouteValues["remoteNamespace"] + "/" + http.Request.RouteValues["method"];

        async Task Handle(HttpContext http, string endpoint, bool stream)
        {
            if (!await authorize(http, endpoint))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            JsonElement arguments;
            try
            {
                using var document = await JsonDocument.ParseAsync(
                    http.Request.Body,
                    cancellationToken: http.RequestAborted);
                var envelope = document.RootElement;
                if (envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Count() != 1 ||
                    !envelope.TryGetProperty("args", out arguments))
                    throw new JsonException("Remote carrier requires exactly one args field.");
                arguments = arguments.Clone();
            }
            catch (JsonException)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            http.Response.ContentType = stream ? "application/x-ndjson" : "application/json";
            if (stream)
            {
                await foreach (var item in gateway.StreamAsync(endpoint, arguments, http.RequestAborted))
                {
                    await JsonSerializer.SerializeAsync(
                        http.Response.Body,
                        item,
                        TypertJson.Default.TypertRemoteResult,
                        http.RequestAborted);
                    await http.Response.WriteAsync("\n", http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                }
            }
            else
                await JsonSerializer.SerializeAsync(
                    http.Response.Body,
                    await gateway.InvokeAsync(endpoint, arguments, http.RequestAborted),
                    TypertJson.Default.TypertRemoteResult,
                    http.RequestAborted);
        }
    }
}
