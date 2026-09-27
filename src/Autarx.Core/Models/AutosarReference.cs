namespace Autarx.Core.Models;

/// <summary>
/// A directed reference between workspace objects, captured from *-REF /
/// *-TREF elements whose text is an absolute path. DEFINITION-REF is excluded
/// (it points at module definitions that usually live outside the delivery);
/// see ARCHITECTURE.md.
/// </summary>
public sealed class AutosarReference
{
    /// <summary>Raw element name, e.g. I-SIGNAL-REF, CHANNEL-REF, TREF.</summary>
    public required string Kind { get; init; }

    /// <summary>Absolute path of the owning (package-level) object.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Source path extended by nested SHORT-NAMEs, e.g.
    /// /Vehicle/Pdus/Pdu_VehicleSpeed/VehicleSpeedMapping.</summary>
    public required string SourceElementPath { get; init; }

    public required string TargetPath { get; init; }

    public required string SourceFile { get; init; }

    /// <summary>Set during index build: true when the target path exists in the workspace.</summary>
    public bool IsResolved { get; internal set; }
}
