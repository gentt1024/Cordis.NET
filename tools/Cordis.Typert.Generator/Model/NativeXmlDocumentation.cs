using System.Globalization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Cordis.Typert.Generator.Model;

internal sealed class NativeXmlDocumentation : DocumentationProvider
{
    private readonly string path;
    private readonly Dictionary<string, string> members;

    internal NativeXmlDocumentation(string path)
    {
        this.path = Path.GetFullPath(path);
        members = XDocument
            .Load(this.path)
            .Descendants("member")
            .Where(member => member.Attribute("name") is not null)
            .ToDictionary(
                member => member.Attribute("name")!.Value,
                member => NormalizeLineEndings(member.ToString()),
                StringComparer.Ordinal);
    }

    internal string Read(string documentationMemberId) => members.GetValueOrDefault(documentationMemberId, "");

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
