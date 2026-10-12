using System.Text.Json.Serialization;
using Cordis.Composition;

namespace Cordis.Platform.Tests.EventAlpha
{
    [RemoteEvent("alpha/changed", typeof(EventDeclarationsJson))]
    public delegate void Changed(List<string?>? values, Dictionary<string, string?> labels, List<string> required);
}

namespace Cordis.Platform.Tests.EventBeta
{
    [RemoteEvent("beta/changed", typeof(EventDeclarationsJson))]
    public delegate void Changed(string value);
}

namespace Cordis.Platform.Tests
{
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(string))]
    public partial class EventDeclarationsJson : JsonSerializerContext;
}
