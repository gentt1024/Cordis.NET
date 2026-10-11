using System.Text.Json;
using Cordis.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cordis.AspNetCore;

/// <summary>Standard HTTP unary and text WebSocket mux for the Typert Gateway. The host retains endpoint authorization.</summary>
public static class CordisRemoteEndpoints
{
    /// <summary>Map standard unary and mux endpoints. Register ASP.NET WebSocket middleware before these routes.</summary>
    public static IEndpointConventionBuilder MapCordisRemote(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        TypertHostConnection connection,
        Func<HttpContext, string, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(authorize);
        var group = endpoints.MapGroup(prefix);
        group.MapPost("/{remoteNamespace}/{method}", (RequestDelegate)(http => HandleUnary(http, Endpoint(http))));
        group.MapGet("/remote.mux", (RequestDelegate)(async http =>
        {
            if (!await authorize(http, "remote.mux"))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await using var mux = new CordisRemoteMux(http, connection, authorize);
            await mux.RunAsync();
        }));
        return group;

        static string Endpoint(HttpContext http) =>
            http.Request.RouteValues["remoteNamespace"] + "/" + http.Request.RouteValues["method"];

        async Task HandleUnary(HttpContext http, string endpoint)
        {
            if (!await authorize(http, endpoint))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!string.Equals(http.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            }
            catch (JsonException)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                var objectRoot = root.ValueKind == JsonValueKind.Object;
                var rpcId = objectRoot && root.TryGetProperty("rpcId", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()! : "invalid-request";
                if (!objectRoot ||
                    !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "client-request" ||
                    !root.TryGetProperty("rpcId", out id) || id.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                {
                    await Write(http, rpcId, BadRequest("invalid client-request message"));
                    return;
                }

                if (method.GetString() != endpoint)
                {
                    await Write(http, rpcId, BadRequest("method does not match endpoint " + endpoint));
                    return;
                }

                var arguments = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("args", out var args) ? args : default;
                await Write(http, rpcId, await connection.InvokeAsync(endpoint, arguments, http.RequestAborted));
            }
        }

        static TypertRemoteResult BadRequest(string message)
        {
            using var details = JsonDocument.Parse("{\"issues\":[]}");
            return new(false, null, new("gateway/bad-request", message, details.RootElement.Clone()));
        }

        static async Task Write(HttpContext http, string rpcId, TypertRemoteResult result)
        {
            http.Response.ContentType = "application/json";
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "server-response");
                writer.WriteString("rpcId", rpcId);
                writer.WriteStartObject("result");
                writer.WriteBoolean("ok", result.Ok);
                if (result.Ok && result.Value is { } value)
                {
                    writer.WritePropertyName("value");
                    value.WriteTo(writer);
                }
                if (!result.Ok)
                {
                    writer.WriteStartObject("error");
                    writer.WriteString("code", result.Error!.Code);
                    writer.WriteString("message", result.Error.Message);
                    writer.WritePropertyName("details");
                    if (result.Error.Details is { ValueKind: JsonValueKind.Object } details)
                        details.WriteTo(writer);
                    else
                    {
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                if (result.Ok && result.Attachments.Count != 0)
                {
                    writer.WriteStartArray("attachments");
                    for (var index = 0;index < result.Attachments.Count;index++)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("codec", "bytes");
                        writer.WriteString("part", "bytes-" + index);
                        writer.WriteStartArray("path");
                        foreach (var segment in result.Attachments[index].Path)
                            segment.WriteTo(writer);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            if (!result.Ok || result.Attachments.Count == 0)
            {
                await http.Response.Body.WriteAsync(buffer.ToArray(), http.RequestAborted);
                return;
            }
            using var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(System.Text.Encoding.UTF8.GetString(buffer.ToArray())), "\"metadata\"");
            for (var index = 0;index < result.Attachments.Count;index++)
            {
                var bytes = new ReadOnlyMemoryContent(result.Attachments[index].Bytes);
                bytes.Headers.ContentType = new("application/octet-stream");
                bytes.Headers.ContentDisposition = new("form-data")
                {
                    Name = "\"bytes-" + index + "\"",
                    FileName = "\"blob\""
                };
                multipart.Add(bytes);
            }
            http.Response.ContentType = multipart.Headers.ContentType!.ToString();
            await multipart.CopyToAsync(http.Response.Body, http.RequestAborted);
        }

    }
}
