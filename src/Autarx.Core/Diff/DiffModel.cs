using Autarx.Core.Models;

namespace Autarx.Core.Diff;

public enum ObjectChangeType
{
    Added,
    Removed,
    Modified,
}

public enum PropertyChangeType
{
    Added,
    Removed,
    Changed,
}

/// <summary>Aggregate counters of one diffed workspace side.</summary>
public sealed record WorkspaceDiffSide(
    string Path,
    int DocumentCount,
    int ObjectCount,
    int ReferenceCount);

/// <summary>
/// One workspace object that was added, removed or modified between two
/// deliveries. Identity is the absolute short-name path. <see cref="Properties"/>
/// carries the property-level explanation (filled when the diff runs with
/// detail enabled); null means "not explained", not "no property changes".
/// </summary>
public sealed record ObjectChange(
    ObjectChangeType ChangeType,
    string ShortName,
    SemanticKind SemanticKind,
    string AbsolutePath,
    string? BeforeElementType,
    string? AfterElementType,
    string? BeforeSourceFile,
    string? AfterSourceFile)
{
    /// <summary>True when the element type — and possibly the semantic kind —
    /// changed under the same identity, e.g. an SW component type retyped.</summary>
    public bool ElementTypeChanged =>
        BeforeElementType is not null
        && AfterElementType is not null
        && !string.Equals(BeforeElementType, AfterElementType, StringComparison.Ordinal);

    /// <summary>The element type as of the side the change belongs to
    /// (after for added/modified, before for removed).</summary>
    public string? ElementType =>
        ChangeType == ObjectChangeType.Removed ? BeforeElementType : AfterElementType ?? BeforeElementType;

    public IReadOnlyList<PropertyChange>? Properties { get; init; }
}

/// <summary>A reference that appeared or disappeared between deliveries.
/// Identity is (source, kind, target) — a reference moving between files is
/// not a change.</summary>
public sealed record ReferenceChange(
    ObjectChangeType ChangeType,
    string SourcePath,
    string Kind,
    string TargetPath,
    string SourceFile);

public sealed record FileChange(
    ObjectChangeType ChangeType,
    string RelativePath,
    long? BeforeSizeBytes,
    long? AfterSizeBytes);

public sealed record WorkspaceDiff(
    WorkspaceDiffSide Before,
    WorkspaceDiffSide After,
    IReadOnlyList<ObjectChange> Objects,
    IReadOnlyList<ReferenceChange> References,
    IReadOnlyList<FileChange> Files)
{
    public bool IsEmpty =>
        Objects.Count == 0 && References.Count == 0 && Files.Count == 0;
}

/// <summary>One property-level difference inside a modified object, addressed
/// by a structural path of sibling keys (SHORT-NAME, or the DEFINITION-REF's
/// last segment for unnamed ECUC values, or the element name).</summary>
public sealed record PropertyChange(
    string Path,
    PropertyChangeType ChangeType,
    string? Before,
    string? After);
