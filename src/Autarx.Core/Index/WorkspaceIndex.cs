using Autarx.Core.Models;

namespace Autarx.Core.Index;

/// <summary>
/// The semantic view of one workspace: documents, identifiables by absolute
/// path, the forward and reverse reference graphs, and the derived summary.
/// CLI, GUI and future AI surfaces all read through this class — none of them
/// re-parses ARXML.
/// </summary>
public sealed class WorkspaceIndex
{
    private readonly Dictionary<string, SemanticObject> _byPath;
    private readonly Dictionary<string, List<AutosarReference>> _outgoing;
    private readonly Dictionary<string, List<AutosarReference>> _incoming;
    private readonly List<AutosarReference> _unresolved;

    internal WorkspaceIndex(
        List<WorkspaceDocument> documents,
        List<SemanticObject> objects,
        List<AutosarReference> references,
        List<WorkspaceDuplicatePath> duplicatePaths,
        List<WorkspaceFileError> fileErrors,
        int packageCount)
    {
        Documents = documents;
        Objects = objects;
        References = references;
        DuplicatePaths = duplicatePaths;
        FileErrors = fileErrors;
        PackageCount = packageCount;

        _byPath = objects.GroupBy(o => o.AbsolutePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        _outgoing = GroupBy(references, r => r.SourcePath);
        _incoming = GroupBy(references, r => r.TargetPath);
        _unresolved = references.Where(r => !r.IsResolved).ToList();
    }

    public IReadOnlyList<WorkspaceDocument> Documents { get; }

    public IReadOnlyList<SemanticObject> Objects { get; }

    public IReadOnlyList<AutosarReference> References { get; }

    public IReadOnlyList<AutosarReference> UnresolvedReferences => _unresolved;

    public IReadOnlyList<WorkspaceDuplicatePath> DuplicatePaths { get; }

    public IReadOnlyList<WorkspaceFileError> FileErrors { get; }

    public int PackageCount { get; }

    public static WorkspaceIndex Build(string path) => WorkspaceIndexBuilder.Build(path);

    /// <summary>
    /// Resolves an absolute path (/Vehicle/Signals/VehicleSpeed) or a plain
    /// SHORT-NAME. Short names are only unique per workspace — ambiguity is a
    /// first-class outcome, never silently resolved.
    /// </summary>
    public ObjectResolution Resolve(string nameOrPath)
    {
        if (nameOrPath.StartsWith('/'))
        {
            var byPath = _byPath.GetValueOrDefault(nameOrPath);
            return byPath is null
                ? new ObjectResolution(ResolveStatus.NotFound, null, [])
                : new ObjectResolution(ResolveStatus.Found, byPath, []);
        }

        var matches = Objects
            .Where(o => o.ShortName == nameOrPath)
            .OrderBy(o => o.AbsolutePath, StringComparer.Ordinal)
            .ToList();

        return matches.Count switch
        {
            0 => new ObjectResolution(ResolveStatus.NotFound, null, []),
            1 => new ObjectResolution(ResolveStatus.Found, matches[0], []),
            _ => new ObjectResolution(ResolveStatus.Ambiguous, null, matches),
        };
    }

    /// <summary>Case-insensitive SHORT-NAME substring search, ordered by path.</summary>
    public IReadOnlyList<SemanticObject> FindByName(string pattern) => Objects
        .Where(o => o.ShortName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
        .OrderBy(o => o.AbsolutePath, StringComparer.Ordinal)
        .ToList();

    public IReadOnlyList<AutosarReference> OutgoingOf(string path) =>
        _outgoing.GetValueOrDefault(path, []);

    public IReadOnlyList<AutosarReference> IncomingOf(string path) =>
        _incoming.GetValueOrDefault(path, []);

    /// <summary>
    /// Breadth-first walk over the reference graph, at most <paramref name="maxDepth"/>
    /// hops from the root. Each object is visited once; edges are reported with
    /// the depth of the node they were expanded from. Purely generic graph
    /// traversal — no AUTOSAR communication semantics are hardcoded here.
    /// </summary>
    public TraceResult Trace(string rootPath, TraceDirection direction, int maxDepth)
    {
        if (maxDepth < 0)
            maxDepth = 0;

        var nodes = new List<TraceNode> { new(rootPath, 0) };
        var edges = new List<TraceEdge>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootPath };
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));

        while (queue.Count > 0 && queue.Peek().Depth < maxDepth)
        {
            var (currentPath, depth) = queue.Dequeue();
            var nextDepth = depth + 1;

            if (direction is TraceDirection.Outgoing or TraceDirection.Both)
                foreach (var reference in OutgoingOf(currentPath))
                    Visit(reference.SourcePath, reference.Kind, reference.TargetPath, reference.TargetPath);

            if (direction is TraceDirection.Incoming or TraceDirection.Both)
                foreach (var reference in IncomingOf(currentPath))
                    Visit(reference.SourcePath, reference.Kind, reference.TargetPath, reference.SourcePath);

            void Visit(string sourcePath, string kind, string targetPath, string neighbour)
            {
                edges.Add(new TraceEdge(sourcePath, kind, targetPath, nextDepth));

                if (visited.Add(neighbour))
                {
                    nodes.Add(new TraceNode(neighbour, nextDepth));
                    queue.Enqueue((neighbour, nextDepth));
                }
            }
        }

        return new TraceResult(rootPath, direction, maxDepth, nodes, edges);
    }

    public WorkspaceSummary Summarize()
    {
        var namespaces = Documents
            .Select(d => d.Namespace)
            .Where(ns => ns is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(ns => ns, StringComparer.Ordinal)
            .ToList();

        var schemaLocations = Documents
            .Select(d => d.SchemaLocation)
            .Where(sl => !string.IsNullOrEmpty(sl))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(sl => sl, StringComparer.Ordinal)
            .ToList();

        var releases = Documents
            .Select(d => d.AutosarRelease)
            .Where(r => r is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A workspace mixing schema flavours cannot be summarised as one
        // release — report null and let MixedSchema carry the warning.
        var release = releases.Count == 1 ? releases[0] : null;
        var mixedSchema = namespaces.Count > 1
            || schemaLocations.Count > 1
            || releases.Count > 1;

        var counts = new Dictionary<SemanticKind, int>();
        foreach (SemanticKind kind in Enum.GetValues<SemanticKind>())
        {
            if (kind == SemanticKind.Unknown)
                continue;
            var total = Documents.Sum(d => d.SemanticCounts.GetValueOrDefault(kind));
            if (total > 0)
                counts[kind] = total;
        }

        var namedTotal = Documents.Sum(d => d.NamedElementCount);

        return new WorkspaceSummary(
            Documents.Count,
            Documents.Sum(d => d.FileSizeBytes),
            Documents.Sum(d => d.ElementCount),
            Objects.Count,
            PackageCount,
            References.Count,
            _unresolved.Count,
            DuplicatePaths.Count,
            FileErrors.Count,
            namespaces,
            schemaLocations,
            release,
            mixedSchema,
            counts,
            namedTotal - counts.Values.Sum());
    }

    private static Dictionary<string, List<AutosarReference>> GroupBy(
        IEnumerable<AutosarReference> references,
        Func<AutosarReference, string> keySelector)
    {
        var map = new Dictionary<string, List<AutosarReference>>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var key = keySelector(reference);
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }
            list.Add(reference);
        }
        return map;
    }
}
