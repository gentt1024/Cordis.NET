namespace Cordis;

/// <summary>A receiver's filter for ordinary Cordis event listeners.</summary>
/// <remarks>Listeners registered with Global bypass receiver filtering.</remarks>
public interface IEventFilter
{
    /// <summary>Determine whether the listener's owning Context receives this dispatch.</summary>
    bool Filter(Context listener);
}
