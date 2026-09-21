namespace Cordis.Fixtures;

public interface IVersionedService
{
    string Version { get; }
    string Resource { get; }
    string Dependency { get; }
}
