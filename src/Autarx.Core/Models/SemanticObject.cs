namespace Autarx.Core.Models;

/// <summary>
/// A package-level AUTOSAR identifiable as seen by the workspace index.
/// Identity is the AUTOSAR absolute short-name path — never the XML line
/// number — so the same object is stable across file rewrites and schema
/// versions. Nested identifiables (ports, mappings, containers) are not
/// indexed as objects; they appear through reference attribution and
/// semantic counts.
/// </summary>
public sealed class SemanticObject
{
    public required string ShortName { get; init; }

    /// <summary>Raw ARXML element type, e.g. CAN-CLUSTER.</summary>
    public required string ElementType { get; init; }

    public required SemanticKind SemanticKind { get; init; }

    /// <summary>AUTOSAR absolute short-name path, e.g. /Vehicle/Signals/VehicleSpeed.</summary>
    public required string AbsolutePath { get; init; }

    public required string SourceFile { get; init; }
}
