namespace Cordis.Typert.Generator.Model;

internal sealed record NativeDiagnostic(string Category, string Code, string Message, string? Symbol = null);

internal sealed record NativeLocation(string Document, int Line, int Column);

internal sealed record NativeCompilation(
    string AssemblyName,
    string LanguageVersion,
    string Nullable,
    string[] Defines,
    bool AllowUnsafe,
    bool CheckOverflow,
    string OutputKind,
    string Platform,
    string Optimization);

internal sealed record NativeConstant(string Kind, string Type, string Json);

internal sealed record NativeNamedConstant(string Name, NativeConstant Value);

internal sealed record NativeAttribute(string Name, NativeConstant[] Arguments, NativeNamedConstant[] NamedArguments);

internal sealed record NativeTypeReference(
    string Kind,
    string Name,
    string Nullability,
    NativeTypeReference[] Arguments,
    int Rank = 0);

internal sealed record NativeMember(
    string Name,
    string WireName,
    string Kind,
    NativeTypeReference Type,
    bool Required,
    bool JsonRequired,
    bool Readable,
    string WriteAccess,
    string Accessibility,
    NativeAttribute[] Attributes,
    NativeConstant? Initializer,
    NativeConstant? GetterConstant,
    string Documentation,
    NativeLocation? Location);

internal sealed record NativeConstructorParameter(
    string Name,
    NativeTypeReference Type,
    string? Member,
    bool Optional,
    NativeConstant? Default,
    NativeAttribute[] Attributes);

internal sealed record NativeConstructor(
    string Accessibility,
    bool JsonConstructor,
    bool SetsRequiredMembers,
    NativeConstructorParameter[] Parameters);

internal sealed record NativeTypeDeclaration(
    string Id,
    string Name,
    string Namespace,
    string Kind,
    bool External,
    NativeTypeReference? BaseType,
    NativeTypeReference[] TypeArguments,
    NativeMember[] Members,
    NativeConstructor[] Constructors,
    NativeAttribute[] Attributes,
    string Documentation,
    NativeLocation? Location);

internal sealed record NativeParameter(
    string Name,
    string Wire,
    NativeTypeReference Type,
    string? Lookup,
    NativeTypeReference? LookupWireType,
    string RefKind,
    bool Optional,
    NativeConstant? Default,
    bool Params,
    NativeAttribute[] Attributes,
    string Documentation);

internal sealed record NativeMethod(
    string Name,
    string Implementation,
    string ReturnWrapper,
    NativeTypeReference Result,
    bool Stream,
    bool Cancellation,
    string? Context,
    string ContextWire,
    NativeParameter[] Parameters,
    string Documentation,
    NativeLocation? Location);

internal sealed record NativeSerializerContext(
    string Id,
    NativeAttribute[] Attributes,
    NativeTypeReference[] RegisteredTypes,
    bool HasAuthoredMetadataImplementation);

internal sealed record NativeService(
    string Key,
    string Namespace,
    string SourceType,
    NativeSerializerContext? Serializer,
    NativeMethod[] Methods,
    string Documentation,
    NativeLocation? Location);

internal sealed record NativeModelDocument(
    int FormatVersion,
    string ContractIdentity,
    NativeCompilation Compilation,
    NativeService[] Services,
    NativeTypeDeclaration[] Types,
    NativeDiagnostic[] Diagnostics);
