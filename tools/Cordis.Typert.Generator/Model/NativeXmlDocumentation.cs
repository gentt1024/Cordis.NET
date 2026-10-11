using System.Globalization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Cordis.Typert.Generator.Model;

internal sealed class NativeXmlDocumentation : DocumentationProvider
{
    private readonly string path;
    private readonly Lazy<Dictionary<string, string>> members;

    internal NativeXmlDocumentation(string path) : this(Path.GetFullPath(path), () => XDocument.Load(path))
    {
    }

    private NativeXmlDocumentation(string identity, Func<XDocument> document)
    {
        path = identity;
        members = new(() => document()
            .Descendants("member")
            .Where(member => member.Attribute("name") is not null)
            .ToDictionary(
                member => member.Attribute("name")!.Value,
                member => NormalizeLineEndings(member.ToString()),
                StringComparer.Ordinal));
    }

    internal static NativeXmlDocumentation FromText(string assemblyIdentity, string text) =>
        new(assemblyIdentity, () => XDocument.Parse(text));

    internal string Read(string documentationMemberId) => members.Value.GetValueOrDefault(documentationMemberId, "");

    internal static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    protected override string GetDocumentationForSymbol(
        string documentationMemberID,
        CultureInfo preferredCulture,
        CancellationToken cancellationToken = default) =>
        Read(documentationMemberID);

    public override bool Equals(object? obj) => obj is NativeXmlDocumentation other && path == other.path;
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(path);
}
