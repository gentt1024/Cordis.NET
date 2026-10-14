using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
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
    public sealed record DecisionRequest(string Mode)
    {
        public Guid Id
        {
            get;
            init;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Omitted
        {
            get;
            init;
        }
    }

    public sealed record DecisionValue(string Value)
    {
        public Guid Id
        {
            get;
            init;
        }

        [AllowNull]
        public string Detail
        {
            get;
            set;
        } = "default";
    }

    /// <summary>A nullable nested collection exported before its required sibling.</summary>
    public sealed class LooseFirst
    {
        /// <summary>Framework identifier carried by the event.</summary>
        public Guid Id
        {
            get;
            set;
        }

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
