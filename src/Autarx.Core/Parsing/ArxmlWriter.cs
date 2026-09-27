using System.Text;
using Autarx.Core.Models;

namespace Autarx.Core.Parsing;

/// <summary>
/// Canonical ARXML serializer for patched documents. Output is deterministic
/// (2-space indent, attributes sorted) and semantically equivalent to the
/// input — it is NOT byte-faithful: comments and vendor formatting are not
/// preserved (the parser drops them), which is why every patch is gated by a
/// before/after SEMANTIC diff rather than a text diff.
/// The LocalName parser collapses namespace-prefixed attributes; the root's
/// xmlns / xmlns:xsi / xsi:schemaLocation are reconstructed here.
/// </summary>
public static class ArxmlWriter
{
    public static void WriteFile(ArxmlElement root, string path, string? provenanceComment = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        if (!string.IsNullOrEmpty(provenanceComment))
            sb.AppendLine($"<!-- {provenanceComment} -->");
        WriteElement(sb, root, 0, isRoot: true);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteElement(StringBuilder sb, ArxmlElement element, int indent, bool isRoot = false)
    {
        var pad = new string(' ', indent * 2);
        var name = element.Name;

        var attributes = element.Attributes
            .OrderBy(a => a.Key, StringComparer.Ordinal)
            .ToList();

        if (isRoot)
        {
            attributes = ReorderRootAttributes(attributes);
        }

        var openTag = attributes.Count == 0
            ? name
            : name + string.Concat(attributes.Select(a => $" {a.Key}=\"{Escape(a.Value)}\""));

        if (element.Children.Count == 0 && element.Text is null)
        {
            sb.AppendLine($"{pad}<{openTag}/>");
            return;
        }

        if (element.Children.Count == 0)
        {
            sb.AppendLine($"{pad}<{openTag}>{Escape(element.Text!)}</{name}>");
            return;
        }

        sb.AppendLine($"{pad}<{openTag}>");
        foreach (var child in element.Children)
            WriteElement(sb, child, indent + 1);
        sb.AppendLine($"{pad}</{name}>");
    }

    /// <summary>Maps the parser's LocalName attribute keys back to their
    /// declared form on the root element.</summary>
    private static List<KeyValuePair<string, string>> ReorderRootAttributes(List<KeyValuePair<string, string>> attributes)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var attr in attributes)
        {
            switch (attr.Key)
            {
                case "xmlns":
                    result.Insert(0, new("xmlns", attr.Value));
                    break;
                case "xsi":
                    result.Add(new("xmlns:xsi", attr.Value));
                    break;
                case "schemaLocation":
                    result.Add(new("xsi:schemaLocation", attr.Value));
                    break;
                default:
                    result.Add(new(attr.Key, attr.Value));
                    break;
            }
        }
        return result;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
