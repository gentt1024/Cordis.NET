using System.Net.Http.Headers;
using System.Text.Json;

namespace Cordis.Composition;

/// <summary>A shared Remote Connection. The application retains ownership of its HttpClient.</summary>
public sealed partial class TypertConnection : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly Uri remoteBase;
    private readonly TypertCallLifetime lifetime = new();

    /// <summary>Create a Connection at an absolute channel URL, normally the Host's /api channel.</summary>
    public TypertConnection(HttpClient borrowedClient, Uri remoteBase)
    {
        ArgumentNullException.ThrowIfNull(borrowedClient);
        ArgumentNullException.ThrowIfNull(remoteBase);
        if (!remoteBase.IsAbsoluteUri)
            throw new ArgumentException("Remote base must be absolute.", nameof(remoteBase));
        http = borrowedClient;
        this.remoteBase = remoteBase;
    }

    internal bool Active => lifetime.Active;

    internal async Task<TypertRemoteResult> CallAsync(string endpoint, JsonElement arguments, CancellationToken token)
    {
        using var call = lifetime.Enter() ?? throw new InvalidOperationException("The Remote Connection is closed.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, call.Token);
        var rpcId = Guid.NewGuid().ToString();
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "client-request");
            writer.WriteString("rpcId", rpcId);
            writer.WriteString("method", endpoint);
            writer.WriteStartObject("payload");
            writer.WritePropertyName("args");
            arguments.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var url = new Uri(remoteBase.AbsoluteUri.TrimEnd('/') + "/" + endpoint);
        using var response = await http.PostAsync(url, content, linked.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Remote transport failure: HTTP " + (int)response.StatusCode, null, response.StatusCode);
        using var document = await ReadResponseAsync(response.Content, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        var envelope = document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "server-response" ||
            !envelope.TryGetProperty("rpcId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != rpcId ||
            !envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Invalid or uncorrelated Remote response.");
        if (ok.GetBoolean())
            return new(true, result.TryGetProperty("value", out var value) ? value.Clone() : null, null);
        if (!result.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String ||
            !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
            !error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
            throw new JsonException("Invalid Remote failure.");
        return new(false, null, new(code.GetString()!, message.GetString()!, details.Clone()));
    }

    /// <summary>Cancel and drain this Connection's calls without disposing its borrowed HttpClient.</summary>
    public ValueTask DisposeAsync() => lifetime.DisposeAsync();
}
