using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>Static serialization metadata for the native Remote envelopes.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TypertRemoteResult))]
[JsonSerializable(typeof(string))]
public partial class TypertJson : JsonSerializerContext;
