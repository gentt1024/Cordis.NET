namespace Cordis.Composition;

/// <summary>Read resource identities from decoded parameters, with lookup parameters still represented as JSON wire identities.</summary>
/// <remarks>Null names no resource of this kind; an empty list is invalid. Readers run before Context or lookup resolution.</remarks>
public delegate IReadOnlyList<string>? TypertResourceReader(
    IReadOnlyList<object?> arguments,
    CancellationToken cancellationToken);

/// <summary>Optional Host metadata for the resource identities touched by exported Remote methods.</summary>
public interface ITypertRemoteResourceSource
{
    /// <summary>Readers keyed first by exported method name, then by resource kind. The Gateway reads the root service.</summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypertResourceReader>> ResourceReaders
    {
        get;
    }
}
