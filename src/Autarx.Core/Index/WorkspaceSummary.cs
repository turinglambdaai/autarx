using Autarx.Core.Models;

namespace Autarx.Core.Index;

/// <summary>Aggregate numbers for one workspace. Computed by
/// <see cref="WorkspaceIndex.Summarize"/>; consumed by inspect, reports and
/// any future automation surface.</summary>
public sealed record WorkspaceSummary(
    int FileCount,
    long TotalFileBytes,
    int TotalElementCount,
    int IdentifiableCount,
    int PackageCount,
    int ReferenceCount,
    int UnresolvedReferenceCount,
    int DuplicatePathCount,
    int FileErrorCount,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<string> SchemaLocations,
    string? AutosarRelease,
    bool MixedSchema,
    IReadOnlyDictionary<SemanticKind, int> SemanticCounts,
    int UnknownNamedCount);
