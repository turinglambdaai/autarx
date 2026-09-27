using Autarx.Core.Index;
using Autarx.Core.Models;
using Autarx.Core.Parsing;

namespace Autarx.Core.Diff;

/// <summary>
/// Engineering diff between two workspaces. Objects are matched by absolute
/// short-name path (never by file position); modified detection uses the
/// index-time content hashes, so nothing beyond the two indexes is needed to
/// know *that* something changed. Property-level *explanations* re-read the
/// affected source files on demand and compare the object subtrees.
/// </summary>
public static class SemanticDiff
{
    public static WorkspaceDiff Compare(string beforePath, string afterPath, bool includePropertyDetail = false) =>
        Compare(WorkspaceIndex.Build(beforePath), WorkspaceIndex.Build(afterPath), includePropertyDetail);

    public static WorkspaceDiff Compare(
        WorkspaceIndex before,
        WorkspaceIndex after,
        bool includePropertyDetail = false)
    {
        var beforeByPath = ByPath(before);
        var afterByPath = ByPath(after);

        var changes = new List<ObjectChange>();

        foreach (var (path, beforeObject) in beforeByPath)
        {
            if (!afterByPath.TryGetValue(path, out var afterObject))
            {
                changes.Add(Change(ObjectChangeType.Removed, beforeObject, null));
                continue;
            }

            if (beforeObject.ContentHash == afterObject.ContentHash
                && string.Equals(beforeObject.ElementType, afterObject.ElementType, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add(Change(ObjectChangeType.Modified, beforeObject, afterObject));
        }

        foreach (var (path, afterObject) in afterByPath)
        {
            if (!beforeByPath.ContainsKey(path))
                changes.Add(Change(ObjectChangeType.Added, null, afterObject));
        }

        if (includePropertyDetail)
        {
            var parsedFiles = new Dictionary<string, ArxmlElement?>(StringComparer.Ordinal);
            for (var i = 0; i < changes.Count; i++)
            {
                if (changes[i].ChangeType != ObjectChangeType.Modified)
                    continue;
                var beforeObject = beforeByPath[changes[i].AbsolutePath];
                var afterObject = afterByPath[changes[i].AbsolutePath];
                changes[i] = changes[i] with
                {
                    Properties = ExplainObject(beforeObject, afterObject, parsedFiles),
                };
            }
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.AbsolutePath, b.AbsolutePath));

        return new WorkspaceDiff(
            Side(before.SourcePath, before),
            Side(after.SourcePath, after),
            changes,
            ReferenceChanges(before, after),
            FileChanges(before, after));
    }

    private static WorkspaceDiffSide Side(string path, WorkspaceIndex index) => new(
        path,
        index.Documents.Count,
        index.Objects.Count,
        index.References.Count);

    private static Dictionary<string, SemanticObject> ByPath(WorkspaceIndex index) => index.Objects
        .GroupBy(o => o.AbsolutePath, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static ObjectChange Change(ObjectChangeType type, SemanticObject? beforeObject, SemanticObject? afterObject)
    {
        var source = afterObject ?? beforeObject!;
        return new ObjectChange(
            type,
            source.ShortName,
            source.SemanticKind,
            source.AbsolutePath,
            beforeObject?.ElementType,
            afterObject?.ElementType,
            beforeObject?.SourceFile,
            afterObject?.SourceFile);
    }

    private static IReadOnlyList<ReferenceChange> ReferenceChanges(WorkspaceIndex before, WorkspaceIndex after)
    {
        static Dictionary<(string, string, string), AutosarReference> Keyed(WorkspaceIndex index) => index.References
            .GroupBy(r => (r.SourcePath, r.Kind, r.TargetPath))
            .ToDictionary(g => g.Key, g => g.First());

        var beforeRefs = Keyed(before);
        var afterRefs = Keyed(after);

        var changes = new List<ReferenceChange>();
        foreach (var (key, reference) in beforeRefs)
        {
            if (!afterRefs.ContainsKey(key))
                changes.Add(new ReferenceChange(ObjectChangeType.Removed, key.Item1, key.Item2, key.Item3, reference.SourceFile));
        }
        foreach (var (key, reference) in afterRefs)
        {
            if (!beforeRefs.ContainsKey(key))
                changes.Add(new ReferenceChange(ObjectChangeType.Added, key.Item1, key.Item2, key.Item3, reference.SourceFile));
        }

        return changes
            .OrderBy(c => c.SourcePath, StringComparer.Ordinal)
            .ThenBy(c => c.TargetPath, StringComparer.Ordinal)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<FileChange> FileChanges(WorkspaceIndex before, WorkspaceIndex after)
    {
        var beforeFiles = before.Documents.ToDictionary(d => d.RelativePath, StringComparer.Ordinal);
        var afterFiles = after.Documents.ToDictionary(d => d.RelativePath, StringComparer.Ordinal);

        var changes = new List<FileChange>();
        foreach (var (relativePath, document) in beforeFiles)
        {
            if (!afterFiles.ContainsKey(relativePath))
                changes.Add(new FileChange(ObjectChangeType.Removed, relativePath, document.FileSizeBytes, null));
        }
        foreach (var (relativePath, document) in afterFiles)
        {
            if (!beforeFiles.TryGetValue(relativePath, out var beforeDocument))
                changes.Add(new FileChange(ObjectChangeType.Added, relativePath, null, document.FileSizeBytes));
            else if (beforeDocument.FileSizeBytes != document.FileSizeBytes)
                changes.Add(new FileChange(ObjectChangeType.Modified, relativePath, beforeDocument.FileSizeBytes, document.FileSizeBytes));
        }

        return changes
            .OrderBy(c => c.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Property-level explanation for one modified object: re-reads both
    /// source files, locates the object subtrees and compares them structurally.
    /// Returns null when either subtree cannot be located (deleted file, parse
    /// error) — the object-level modification stands regardless.
    /// </summary>
    private static IReadOnlyList<PropertyChange>? ExplainObject(
        SemanticObject beforeObject,
        SemanticObject afterObject,
        Dictionary<string, ArxmlElement?> parsedFiles)
    {
        var beforeElement = LoadObjectElement(beforeObject.SourceFile, beforeObject.AbsolutePath, parsedFiles);
        var afterElement = LoadObjectElement(afterObject.SourceFile, afterObject.AbsolutePath, parsedFiles);
        if (beforeElement is null || afterElement is null)
            return null;

        var changes = new List<PropertyChange>();
        CompareElements(beforeElement, afterElement, [], changes);
        return changes;
    }

    /// <summary>Compares two matched object subtrees. <paramref name="context"/>
    /// already carries the segments identifying the current element; children
    /// are recursed with their own key appended.</summary>
    private static void CompareElements(
        ArxmlElement before,
        ArxmlElement after,
        List<string> context,
        List<PropertyChange> changes)
    {
        // context already identifies this element; the root frame falls back
        // to the element name
        if (!string.Equals(before.Text?.Trim(), after.Text?.Trim(), StringComparison.Ordinal))
            changes.Add(new PropertyChange(
                context.Count == 0 ? before.Name : string.Join('/', context),
                PropertyChangeType.Changed,
                before.Text,
                after.Text));

        var beforeChildren = KeyChildren(before);
        var afterChildren = KeyChildren(after);

        foreach (var (key, beforeChild) in beforeChildren)
        {
            if (!afterChildren.TryGetValue(key, out var afterChild))
            {
                changes.Add(new PropertyChange(
                    SegmentPath(context, key),
                    PropertyChangeType.Removed,
                    Describe(beforeChild),
                    null));
                continue;
            }

            context.Add(key);
            CompareElements(beforeChild, afterChild, context, changes);
            context.RemoveAt(context.Count - 1);
        }

        foreach (var (key, afterChild) in afterChildren)
        {
            if (!beforeChildren.ContainsKey(key))
                changes.Add(new PropertyChange(
                    SegmentPath(context, key),
                    PropertyChangeType.Added,
                    null,
                    Describe(afterChild)));
        }
    }

    /// <summary>
    /// Sibling identity inside one parent: SHORT-NAME wins, then the
    /// DEFINITION-REF's last segment (how ECUC values are talked about),
    /// then the element name. Repeated keys get a 1-based occurrence suffix
    /// in document order on both sides, so positional repeats still pair up.
    /// </summary>
    private static Dictionary<string, ArxmlElement> KeyChildren(ArxmlElement element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<string, ArxmlElement>(StringComparer.Ordinal);

        foreach (var child in element.Children)
        {
            var segment = child.ChildText("SHORT-NAME");
            if (string.IsNullOrEmpty(segment))
            {
                var definition = child.ChildText("DEFINITION-REF");
                segment = string.IsNullOrEmpty(definition)
                    ? child.Name
                    : definition!.TrimEnd('/').Split('/').Last();
            }

            var key = segment!;
            for (var occurrence = 2; !seen.Add(key); occurrence++)
                key = $"{segment}[{occurrence}]";

            result[key] = child;
        }

        return result;
    }

    private static string Describe(ArxmlElement element)
    {
        var shortName = element.ChildText("SHORT-NAME");
        var label = shortName is null ? element.Name : $"{element.Name} '{shortName}'";
        return element.Text is { } text ? $"{label} = {text}" : label;
    }

    private static string SegmentPath(List<string> context, string leaf) =>
        context.Count == 0 ? leaf : string.Join('/', context) + "/" + leaf;

    private static ArxmlElement? LoadObjectElement(
        string sourceFile,
        string absolutePath,
        Dictionary<string, ArxmlElement?> parsedFiles)
    {
        if (!parsedFiles.TryGetValue(sourceFile, out var root))
        {
            root = File.Exists(sourceFile) ? ArxmlParser.ParseFile(sourceFile) : null;
            parsedFiles[sourceFile] = root;
        }

        return root is null ? null : FindObjectElement(root, absolutePath);
    }

    /// <summary>Locates a package-level identifiable by absolute short-name
    /// path — the mirror of the index walk: every segment but the last names
    /// an AR-PACKAGE, the last names an element in the innermost package.</summary>
    internal static ArxmlElement? FindObjectElement(ArxmlElement root, string absolutePath)
    {
        var segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var container = root.Element("AR-PACKAGES");
        ArxmlElement? package = null;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            package = container?.Elements("AR-PACKAGE")
                .FirstOrDefault(p => string.Equals(p.ChildText("SHORT-NAME"), segments[i], StringComparison.Ordinal));
            if (package is null)
                return null;
            container = package.Element("AR-PACKAGES");
        }

        return package?.Element("ELEMENTS")?.Children
            .FirstOrDefault(e => string.Equals(e.ChildText("SHORT-NAME"), segments[^1], StringComparison.Ordinal));
    }
}
