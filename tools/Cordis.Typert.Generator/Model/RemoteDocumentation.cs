using System.Xml.Linq;

namespace Cordis.Typert.Generator.Model;

internal static class RemoteDocumentation
{
    internal static string? Summary(string xml) => Read(xml, "summary");
    internal static string? Returns(string xml) => Read(xml, "returns");
    internal static string? Parameter(string xml, string name) => Read(xml, "param", name);

    private static string? Read(string xml, string element, string? parameter = null)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        var value = XElement
            .Parse(xml)
            .Elements(element)
            .FirstOrDefault(value => parameter is null || (string?)value.Attribute("name") == parameter);
        if (value is null)
            return null;
        var text = string.Join(" ", value.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text;
    }
}
