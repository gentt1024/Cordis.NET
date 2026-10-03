using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cordis;

public sealed partial class ConfigDescriptor
{
    /// <summary>Serialize the declaration graph, retaining shared nodes, recursive edges, and mode/default/volatile metadata.</summary>
    /// <remarks>Unmaterialized lazy builders cannot be serialized. Delegates and validators are never serialized.</remarks>
    public string Serialize()
    {
        var nodes = new List<ConfigDescriptor>();
        var ids = new Dictionary<ConfigDescriptor, int>(ReferenceEqualityComparer.Instance);
        int Id(ConfigDescriptor node)
        {
            if (ids.TryGetValue(node, out var id)) return id;
            ids.Add(node, id = nodes.Count); nodes.Add(node); return id;
        }
        Id(this);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteStartArray("nodes");
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                if (node._selectBranch is not null) throw new InvalidOperationException("A union branch selector cannot be serialized as a data-only description.");
                if (node.Kind == "lazy" && node.Inner is null) throw new InvalidOperationException("Resolve a lazy configuration before serializing its description.");
                writer.WriteStartObject(); writer.WriteString("kind", node.Kind);
                writer.WriteBoolean("optional", node.IsOptional); writer.WriteBoolean("volatile", node.IsVolatile);
                writer.WriteBoolean("hasDefault", node.HasDefault);
                if (node.HasDefault) { writer.WritePropertyName("default"); WriteValue(writer, node.DefaultValue); }
                if (node.Inner is not null) writer.WriteNumber("inner", Id(node.Inner));
                if (node.Key is not null) writer.WriteNumber("key", Id(node.Key));
                writer.WriteStartArray("children"); foreach (var child in node.Children) writer.WriteNumberValue(Id(child)); writer.WriteEndArray();
                writer.WriteStartObject("properties");
                foreach (var (name, child) in node.Properties) writer.WriteNumber(name, Id(child));
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reconstruct a data-only declaration graph without validators, reflection, or lazy callback execution.</summary>
    public static ConfigDescriptor Deserialize(string serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new ArgumentException("Unsupported configuration description version.", nameof(serialized));
        var records = root.GetProperty("nodes").EnumerateArray().ToArray();
        if (records.Length == 0) throw new ArgumentException("A configuration description must contain a root.", nameof(serialized));
        var nodes = new ConfigDescriptor[records.Length];
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index]; var kind = record.GetProperty("kind").GetString();
            if (kind is not ("any" or "number" or "string" or "boolean" or "object" or "array" or "lazy" or "dict" or "tuple" or "union" or "intersect" or "transform" or "getter"))
                throw new ArgumentException("Unknown configuration description kind.", nameof(serialized));
            var hasDefault = record.GetProperty("hasDefault").GetBoolean();
            nodes[index] = new(kind, optional: record.GetProperty("optional").GetBoolean(), volatileValue: record.GetProperty("volatile").GetBoolean(),
                hasDefault: hasDefault, defaultValue: hasDefault ? ConfigSnapshots.Create(ReadValue(record.GetProperty("default"))) : null);
        }
        ConfigDescriptor Node(JsonElement id)
        {
            var value = id.GetInt32();
            return value >= 0 && value < nodes.Length ? nodes[value] : throw new ArgumentException("Invalid configuration description edge.", nameof(serialized));
        }
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index]; var properties = new Dictionary<string, ConfigDescriptor>(StringComparer.Ordinal);
            foreach (var member in record.GetProperty("properties").EnumerateObject()) properties.Add(member.Name, Node(member.Value));
            nodes[index].Properties = new ReadOnlyDictionary<string, ConfigDescriptor>(properties);
            if (record.TryGetProperty("inner", out var inner)) nodes[index].Inner = Node(inner);
            if (record.TryGetProperty("key", out var key)) nodes[index].Key = Node(key);
            nodes[index].Children = System.Array.AsReadOnly(record.GetProperty("children").EnumerateArray().Select(Node).ToArray());
            if (nodes[index].Kind is "array" or "lazy" or "dict" or "transform" or "getter" && nodes[index].Inner is null) throw new ArgumentException("Missing configuration description inner edge.", nameof(serialized));
        }
        return nodes[0];
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        writer.WriteStartObject();
        if (value is null) writer.WriteString("type", "null");
        else if (ReferenceEquals(value, Undefined.Value)) writer.WriteString("type", "undefined");
        else if (value is string text) { writer.WriteString("type", "string"); writer.WriteString("value", text); }
        else if (value is char character) { writer.WriteString("type", "char"); writer.WriteString("value", character.ToString()); }
        else if (value is bool boolean) { writer.WriteString("type", "bool"); writer.WriteBoolean("value", boolean); }
        else if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            writer.WriteString("type", "object"); writer.WriteStartObject("value");
            foreach (var (name, child) in dictionary) { writer.WritePropertyName(name); WriteValue(writer, child); }
            writer.WriteEndObject();
        }
        else if (value is IReadOnlyList<object?> list)
        {
            writer.WriteString("type", "array"); writer.WriteStartArray("value");
            foreach (var child in list) WriteValue(writer, child);
            writer.WriteEndArray();
        }
        else
        {
            var (type, number) = value switch
            {
                byte n => ("byte", n.ToString(CultureInfo.InvariantCulture)), sbyte n => ("sbyte", n.ToString(CultureInfo.InvariantCulture)),
                short n => ("short", n.ToString(CultureInfo.InvariantCulture)), ushort n => ("ushort", n.ToString(CultureInfo.InvariantCulture)),
                int n => ("int", n.ToString(CultureInfo.InvariantCulture)), uint n => ("uint", n.ToString(CultureInfo.InvariantCulture)),
                long n => ("long", n.ToString(CultureInfo.InvariantCulture)), ulong n => ("ulong", n.ToString(CultureInfo.InvariantCulture)),
                float n => ("float", n.ToString("R", CultureInfo.InvariantCulture)), double n => ("double", n.ToString("R", CultureInfo.InvariantCulture)),
                decimal n => ("decimal", n.ToString(CultureInfo.InvariantCulture)),
                _ => throw new InvalidOperationException("Unsupported configuration default value.")
            };
            writer.WriteString("type", type); writer.WriteString("value", number);
        }
        writer.WriteEndObject();
    }

    private static object? ReadValue(JsonElement encoded)
    {
        var type = encoded.GetProperty("type").GetString();
        if (type == "null") return null;
        if (type == "undefined") return Undefined.Value;
        var value = encoded.GetProperty("value"); var culture = CultureInfo.InvariantCulture;
        return type switch
        {
            "string" => value.GetString(), "char" => value.GetString() is { Length: 1 } text ? text[0] : throw new ArgumentException("Invalid character default."),
            "bool" => value.GetBoolean(),
            "object" => value.EnumerateObject().ToDictionary(member => member.Name, member => ReadValue(member.Value), StringComparer.Ordinal),
            "array" => value.EnumerateArray().Select(ReadValue).ToArray(),
            "byte" => byte.Parse(value.GetString()!, culture), "sbyte" => sbyte.Parse(value.GetString()!, culture),
            "short" => short.Parse(value.GetString()!, culture), "ushort" => ushort.Parse(value.GetString()!, culture),
            "int" => int.Parse(value.GetString()!, culture), "uint" => uint.Parse(value.GetString()!, culture),
            "long" => long.Parse(value.GetString()!, culture), "ulong" => ulong.Parse(value.GetString()!, culture),
            "float" => float.Parse(value.GetString()!, culture), "double" => double.Parse(value.GetString()!, culture), "decimal" => decimal.Parse(value.GetString()!, culture),
            _ => throw new ArgumentException("Unknown configuration default type.")
        };
    }
}
