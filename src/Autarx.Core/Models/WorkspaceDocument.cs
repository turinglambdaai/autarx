namespace Autarx.Core.Models;

/// <summary>One parsed ARXML file inside the workspace.</summary>
public sealed class WorkspaceDocument
{
    public required string FilePath { get; init; }

    /// <summary>FilePath relative to the workspace root — the stable identity
    /// for cross-workspace comparisons (two deliveries live in different
    /// directories; only the relative name can match).</summary>
    public required string RelativePath { get; init; }

    public required long FileSizeBytes { get; init; }

    /// <summary>Root xmlns attribute, e.g. http://autosar.org/schema/r4.0.</summary>
    public string? Namespace { get; init; }

    /// <summary>Root xsi:schemaLocation attribute, verbatim.</summary>
    public string? SchemaLocation { get; init; }

    /// <summary>Release derived from the schema location when the pattern is
    /// recognised (4-3-0 → 4.3.0, R23-11 → R23-11, 00050 → 00050); else null.</summary>
    public string? AutosarRelease { get; init; }

    public required int ElementCount { get; init; }

    /// <summary>Package-level identifiables indexed from this file.</summary>
    public required int IdentifiableCount { get; init; }

    public required int PackageCount { get; init; }

    /// <summary>Elements carrying a SHORT-NAME, packages excluded.</summary>
    public required int NamedElementCount { get; init; }

    /// <summary>Per-kind classification counts (Unknown excluded — derive it
    /// from NamedElementCount minus the sum).</summary>
    public required IReadOnlyDictionary<SemanticKind, int> SemanticCounts { get; init; }
}

public sealed record WorkspaceFileError(string FilePath, string Message, int LineNumber);

public sealed record WorkspaceDuplicatePath(string AbsolutePath, IReadOnlyList<string> SourceFiles);
