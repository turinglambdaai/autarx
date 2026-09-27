namespace Autarx.Core.Models;

/// <summary>
/// A node of an ARXML document. Matching is done on the XML LocalName, so
/// vendor namespace prefixes do not affect parsing or queries.
/// </summary>
public sealed class ArxmlElement
{
    private readonly List<ArxmlElement> _children = [];

    public ArxmlElement(string name) => Name = name;

    public string Name { get; }

    /// <summary>Concatenated text content, or null when the element is empty.</summary>
    public string? Text { get; private set; }

    public Dictionary<string, string> Attributes { get; } = [];

    public ArxmlElement? Parent { get; private set; }

    public IReadOnlyList<ArxmlElement> Children => _children;

    public void Add(ArxmlElement child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    internal void AppendText(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return;
        Text = Text is null ? trimmed : Text + trimmed;
    }

    public ArxmlElement? Element(string name) => _children.FirstOrDefault(c => c.Name == name);

    public IEnumerable<ArxmlElement> Elements(string name) => _children.Where(c => c.Name == name);

    public string? ChildText(string name) => Element(name)?.Text;

    /// <summary>Replaces this element's text content (semantic patching).</summary>
    public void SetText(string? text)
    {
        Text = string.IsNullOrEmpty(text) ? null : text.Trim();
    }

    public string? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value : null;

    /// <summary>All descendants in document order, excluding this element.</summary>
    public IEnumerable<ArxmlElement> Descendants()
    {
        var stack = new Stack<ArxmlElement>(_children);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var i = current._children.Count - 1; i >= 0; i--)
                stack.Push(current._children[i]);
        }
    }
}
