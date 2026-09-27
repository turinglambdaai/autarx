using Autarx.Core.Models;

namespace Autarx.Core.Index;

/// <summary>
/// FNV-1a hash over the canonical form of an element subtree: name, sorted
/// attributes, text, then children in document order. Gives the semantic diff
/// a cheap "did anything under this object change" signal without retaining
/// the subtree. A hash difference is always treated as modified — the
/// conservative direction; the diff explains the change by re-reading the
/// two source files.
/// </summary>
internal static class ContentHasher
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static ulong Hash(ArxmlElement element)
    {
        var h = FnvOffset;
        h = Mix(h, element.Name);

        foreach (var attr in element.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            h = Mix(h, attr.Key);
            h = Mix(h, attr.Value);
        }

        if (element.Text is { } text)
            h = Mix(h, text);

        foreach (var child in element.Children)
            h = (h ^ Hash(child)) * FnvPrime;

        return h;
    }

    private static ulong Mix(ulong h, string value)
    {
        foreach (var c in value)
            h = (h ^ c) * FnvPrime;
        return (h ^ 0x1fUL) * FnvPrime;
    }
}
