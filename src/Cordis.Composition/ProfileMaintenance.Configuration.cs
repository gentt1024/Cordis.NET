using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Cordis.Composition;

public static partial class ProfileMaintenance
{
    // Edits only the selected override. Existing source outside that row/value remains intact.
    internal static string WithConfiguration(string text, string id, string name, object next, bool inherited)
    {
        var rows = ConfigurationFile.ParseEntries(text);
        var parser = new Parser(new StringReader(text));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        var root = ReadNode(parser);
        var matches = rows.Select((row, index) => (row, index)).Where(item => item.row.Id == id
            && !Data.Truthy(item.row.GetValueOrDefault("insert")) && (item.row.Name.Length == 0 || item.row.Name == name)).ToArray();

        // Keep a flow root in place so comments attached to unrelated rows survive.
        // This is a source-format adaptation; the same parsed patch document is applied.
        if (root.Flow)
        {
            if (inherited)
            {
                foreach (var (row, index) in matches.Reverse())
                {
                    if (!row.Remove("config")) continue;
                    var node = root.Children[index];
                    if (row.Keys.All(key => key is "id" or "name"))
                    {
                        var comma = FlowSeparator(text, node.ContentEnd);
                        if (comma < 0 && index > 0) comma = FlowSeparator(text, root.Children[index - 1].ContentEnd);
                        if (comma >= node.ContentEnd) text = text.Remove(comma, 1);
                        text = text.Remove(node.Start, node.ContentEnd - node.Start);
                        if (comma >= 0 && comma < node.Start) text = text.Remove(comma, 1);
                    }
                    else text = text[..node.Start] + ConfigurationFile.WriteFlow(row) + text[node.ContentEnd..];
                }
                return text;
            }
            if (matches.Length == 0)
                return text[..root.End] + (rows.Count == 0 ? "" : ", ")
                    + ConfigurationFile.WriteFlow(new EntryOptions { Id = id, Name = name, Config = next }) + text[root.End..];
        }

        if (inherited)
        {
            foreach (var (row, index) in matches.Reverse())
            {
                var node = root.Children[index];
                if (!node.Fields.TryGetValue("config", out var field)) continue;
                row.Remove("config");
                if (row.Keys.All(key => key is "id" or "name"))
                {
                    var end = node.Flow ? EndOfLine(text, node.ContentEnd) : node.End - node.EndColumn + 1;
                    text = text[..(node.Start - node.Column + 1)] + text[end..];
                }
                else if (node.Flow)
                    text = text[..node.Start] + ConfigurationFile.WriteFlow(row) + text[node.ContentEnd..];
                else
                {
                    var start = field.KeyStart - node.Column + 1;
                    var end = field.Container && !field.Flow ? field.End - field.EndColumn + 1 : EndOfLine(text, field.ContentEnd);
                    text = text[..start] + text[end..];
                }
            }
            return ConfigurationFile.Parse(text) is null ? text + "[]\n" : text;
        }

        var rendered = ConfigurationFile.WriteFlow(next);
        if (matches.Length == 0)
            return text.TrimEnd() + "\n" + ConfigurationFile.Write(new[] { new EntryOptions { Id = id, Name = name, Config = next } });
        var target = root.Children[matches[^1].index];
        if (target.Fields.TryGetValue("config", out var existing))
        {
            var suffix = existing.Container && !existing.Flow ? "\n" + new string(' ', existing.EndColumn - 1) : "";
            return text[..existing.Start] + rendered + suffix + text[existing.ContentEnd..];
        }
        if (target.Flow)
            return text[..target.End] + ", config: " + rendered + text[target.End..];
        return text[..target.End] + new string(' ', target.Column - 1) + "config: " + rendered + "\n" + text[target.End..];
    }

    private static int EndOfLine(string text, int position)
    {
        var newline = text.IndexOf('\n', position);
        return newline < 0 ? text.Length : newline + 1;
    }

    private static int FlowSeparator(string text, int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position])) { position++; continue; }
            if (text[position] == '#') { position = EndOfLine(text, position); continue; }
            return text[position] == ',' ? position : -1;
        }
        return -1;
    }

    internal static async Task WriteConfigurationSourceAsync(string filename, string text)
    {
        _ = ConfigurationFile.ParseEntries(text);
        var temporary = filename + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, text);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, filename, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
