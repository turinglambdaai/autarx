using System.Xml;
using Autarx.Core.Models;

namespace Autarx.Core.Parsing;

public sealed class ArxmlParseException : Exception
{
    public ArxmlParseException(string message, int lineNumber = 0, int linePosition = 0)
        : base(message)
    {
        LineNumber = lineNumber;
        LinePosition = linePosition;
    }

    public int LineNumber { get; }

    public int LinePosition { get; }
}

/// <summary>
/// Streaming XML reader that builds an ArxmlElement tree in a single pass.
/// Namespace prefixes are stripped (LocalName matching), which is how most
/// practical AUTOSAR tooling treats vendor-flavoured files.
/// </summary>
public static class ArxmlParser
{
    public static ArxmlElement ParseFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    public static ArxmlElement Parse(string xml)
    {
        using var reader = new StringReader(xml);
        return Parse(reader);
    }

    private static ArxmlElement Parse(TextReader input)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = true,
        };

        try
        {
            using var xml = XmlReader.Create(input, settings);
            return ReadDocument(xml);
        }
        catch (XmlException ex)
        {
            throw new ArxmlParseException(ex.Message, ex.LineNumber, ex.LinePosition);
        }
    }

    private static ArxmlElement ReadDocument(XmlReader reader)
    {
        ArxmlElement? root = null;
        ArxmlElement? current = null;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var element = new ArxmlElement(reader.LocalName);
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                            element.Attributes[reader.LocalName] = reader.Value;
                        reader.MoveToElement();
                    }

                    if (current is null)
                    {
                        if (root is not null)
                            throw new ArxmlParseException("Multiple root elements");
                        root = element;
                    }
                    else
                    {
                        current.Add(element);
                    }

                    if (!reader.IsEmptyElement)
                        current = element;
                    break;

                case XmlNodeType.Text:
                    current?.AppendText(reader.Value);
                    break;

                case XmlNodeType.EndElement:
                    if (current is not null)
                        current = ReferenceEquals(current, root) ? null : current.Parent;
                    break;
            }
        }

        return root ?? throw new ArxmlParseException("No root element found");
    }
}
