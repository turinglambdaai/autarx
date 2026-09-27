namespace Autarx.Core.Index;

public enum TraceDirection
{
    Outgoing,
    Incoming,
    Both,
}

public enum ResolveStatus
{
    Found,
    NotFound,
    Ambiguous,
}

public sealed record ObjectResolution(
    ResolveStatus Status,
    Models.SemanticObject? Object,
    IReadOnlyList<Models.SemanticObject> Candidates);

public sealed record TraceNode(string Path, int Depth);

public sealed record TraceEdge(string SourcePath, string Kind, string TargetPath, int Depth);

public sealed record TraceResult(
    string Root,
    TraceDirection Direction,
    int MaxDepth,
    IReadOnlyList<TraceNode> Nodes,
    IReadOnlyList<TraceEdge> Edges);
