namespace Cordis.Composition;

/// <summary>Declare one selected Cordis event using its original name and source JSON metadata.</summary>
/// <remarks>A void delegate describes an emit. An asynchronous delegate with an agent subject,
/// request, same-result next callback and cancellation describes a scoped waterfall.</remarks>
[AttributeUsage(AttributeTargets.Delegate, Inherited = false)]
public sealed class RemoteEventAttribute(string name, Type jsonContext) : Attribute
{
    /// <summary>The original Cordis event name.</summary>
    public string Name
    {
        get;
    } = name;

    /// <summary>The source-generated JSON context used for event data.</summary>
    public Type JsonContext
    {
        get;
    } = jsonContext;
}

/// <summary>Host assembly projection of a scoped event subject to its Context and stable agent identity.</summary>
public sealed record TypertRemoteEventTarget(Context Context, string AgentId);
