using System.Text.Json.Serialization;
using Cordis.Composition;

namespace Cordis.Platform.Tests.EventAlpha
{
    [RemoteEvent("alpha/changed", typeof(EventDeclarationsJson))]
    public delegate void Changed(List<string?>? values, Dictionary<string, string?> labels, List<string> required);

    [RemoteEvent("alpha/nested", typeof(EventDeclarationsJson))]
    public delegate void Nested(LooseFirst loose, StrictFirst strict);
}

namespace Cordis.Platform.Tests.EventBeta
{
    [RemoteEvent("beta/changed", typeof(EventDeclarationsJson))]
    public delegate void Changed(string value);
}

namespace Cordis.Platform.Tests
{
    /// <summary>A nullable nested collection exported before its required sibling.</summary>
    public sealed class LooseFirst
    {
        /// <summary>Nested nullable elements.</summary>
        [JsonPropertyOrder(0)]
        public List<List<string?>> Loose
        {
            get;
            set;
        } = [];

        /// <summary>Nested required elements.</summary>
        [JsonPropertyOrder(1)]
        public List<List<string>> Strict
        {
            get;
            set;
        } = [];
    }

    /// <summary>A required nested collection exported before its nullable sibling.</summary>
    public sealed class StrictFirst
    {
        /// <summary>Nested nullable elements.</summary>
        [JsonPropertyOrder(1)]
        public List<List<string?>> Loose
        {
            get;
            set;
        } = [];

        /// <summary>Nested required elements.</summary>
        [JsonPropertyOrder(0)]
        public List<List<string>> Strict
        {
            get;
            set;
        } = [];
    }

    [JsonSerializable(typeof(LooseFirst))]
    [JsonSerializable(typeof(StrictFirst))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(string))]
    public partial class EventDeclarationsJson : JsonSerializerContext;
}
