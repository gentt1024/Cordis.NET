using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Composition;

public sealed partial class TypertConnection
{
    private static async Task<JsonDocument> ReadResponseAsync(HttpContent content, CancellationToken token)
    {
        if (!string.Equals(content.Headers.ContentType?.MediaType, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        }
        var boundary = content.Headers.ContentType!.Parameters.SingleOrDefault(parameter =>
            string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        if (string.IsNullOrEmpty(boundary) || boundary.Contains('\r') || boundary.Contains('\n'))
            throw new JsonException("Invalid Remote multipart boundary.");
        var fields = ReadParts(await content.ReadAsByteArrayAsync(token).ConfigureAwait(false), boundary);
        if (!fields.Remove("metadata", out var metadata) || metadata.File)
            throw new JsonException("Missing Remote multipart metadata.");
        var envelope = JsonNode.Parse(metadata.Bytes) as JsonObject ?? throw new JsonException("Invalid Remote multipart metadata.");
        if (envelope["result"] is not JsonObject result || result["ok"]?.GetValue<bool>() != true ||
            envelope["attachments"] is not JsonArray { Count: > 0 } attachments)
            throw new JsonException("Invalid Remote multipart result.");
        foreach (var entry in attachments)
        {
            if (entry is not JsonObject attachment || attachment["codec"]?.GetValue<string>() != "bytes" ||
                attachment["part"]?.GetValue<string>() is not { } part || attachment["path"] is not JsonArray path ||
                !fields.Remove(part, out var data) || !data.File)
                throw new JsonException("Invalid Remote binary attachment.");
            JsonNode parent = result;
            JsonNode key = JsonValue.Create("value")!;
            foreach (var segment in path)
            {
                parent = Get(parent, key) ?? throw new JsonException("Invalid Remote binary path.");
                key = segment ?? throw new JsonException("Invalid Remote binary path.");
            }
            if (Get(parent, key) is not null)
                throw new JsonException("Invalid Remote binary placeholder.");
            // STJ's byte[] representation is internal to the codec. The wire has
            // only null placeholders and MIME bytes; it never sends this string.
            var restored = JsonValue.Create(Convert.ToBase64String(data.Bytes));
            if (parent is JsonArray array)
                array[Index(key, array.Count)] = restored;
            else
                ((JsonObject)parent)[key.GetValue<string>()] = restored;
        }
        if (fields.Count != 0)
            throw new JsonException("Unexpected Remote multipart fields.");
        return JsonDocument.Parse(envelope.ToJsonString());
    }

    private static JsonNode? Get(JsonNode parent, JsonNode key)
    {
        if (parent is JsonArray array)
            return array[Index(key, array.Count)];
        if (parent is JsonObject obj && key is JsonValue value && value.TryGetValue<string>(out var name) && obj.TryGetPropertyValue(name, out var child))
            return child;
        throw new JsonException("Invalid Remote binary path.");
    }

    private static int Index(JsonNode key, int count) => key is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0 && index < count
        ? index : throw new JsonException("Invalid Remote binary array index.");

    private static Dictionary<string, (byte[] Bytes, bool File)> ReadParts(byte[] bytes, string boundary)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var separator = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        ReadOnlySpan<byte> remaining = bytes;
        if (!remaining.StartsWith(delimiter))
            throw new JsonException("Invalid Remote multipart body.");
        remaining = remaining[delimiter.Length..];
        var fields = new Dictionary<string, (byte[], bool)>(StringComparer.Ordinal);
        while (!remaining.StartsWith("--"u8))
        {
            if (!remaining.StartsWith("\r\n"u8))
                throw new JsonException("Invalid Remote multipart delimiter.");
            remaining = remaining[2..];
            var headerEnd = remaining.IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0)
                throw new JsonException("Invalid Remote multipart headers.");
            ContentDispositionHeaderValue? disposition = null;
            foreach (var header in Encoding.UTF8.GetString(remaining[..headerEnd]).Split("\r\n", StringSplitOptions.None))
            {
                var colon = header.IndexOf(':');
                if (colon < 1)
                    throw new JsonException("Invalid Remote multipart header.");
                if (string.Equals(header[..colon], "Content-Disposition", StringComparison.OrdinalIgnoreCase))
                {
                    if (disposition is not null || !ContentDispositionHeaderValue.TryParse(header[(colon + 1)..].Trim(), out disposition))
                        throw new JsonException("Invalid Remote multipart disposition.");
                }
            }
            if (disposition?.DispositionType != "form-data" || disposition.Name?.Trim('"') is not { } name)
                throw new JsonException("Invalid Remote multipart field.");
            remaining = remaining[(headerEnd + 4)..];
            var end = 0;
            while (true)
            {
                var next = remaining[end..].IndexOf(separator);
                if (next < 0)
                    throw new JsonException("Incomplete Remote multipart field.");
                end += next;
                var tail = remaining[(end + separator.Length)..];
                if (tail.StartsWith("--"u8) || tail.StartsWith("\r\n"u8))
                    break;
                end += separator.Length;
            }
            if (!fields.TryAdd(name, (remaining[..end].ToArray(), disposition.FileName is not null || disposition.FileNameStar is not null)))
                throw new JsonException("Duplicate Remote multipart field.");
            remaining = remaining[(end + separator.Length)..];
        }
        remaining = remaining[2..];
        if (!remaining.IsEmpty && !remaining.SequenceEqual("\r\n"u8))
            throw new JsonException("Invalid Remote multipart closing delimiter.");
        return fields;
    }
}
