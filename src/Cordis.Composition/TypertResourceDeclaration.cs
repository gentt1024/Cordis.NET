namespace Cordis.Composition;

/// <summary>Declare the wire field containing a resource identity.</summary>
public sealed record TypertResourceDeclaration(string Field);

/// <summary>A resource kind and its declared identity field.</summary>
public sealed record TypertResourceEntry(string Kind, string Field);
