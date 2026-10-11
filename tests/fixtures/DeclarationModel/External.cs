namespace IndependentDeclarations.External;

public interface IExternalMarker;

public sealed class Envelope<T>
{
    public T? Value
    {
        get;
        init;
    }
}
