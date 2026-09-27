using Autarx.Core.Diff;
using Autarx.Core.Models;

namespace Autarx.Core.Impact;

public enum ImpactSeverity
{
    Breaking,
    Info,
}

/// <summary>
/// One deterministic impact finding. Rule ids (ARX-IMP-*) are a contract:
/// never renamed, never renumbered — new rules are appended.
/// </summary>
public sealed record ImpactFinding(
    string Rule,
    ImpactSeverity Severity,
    string ObjectPath,
    string Detail);

public sealed record CommunicationImpact(
    IReadOnlyList<ObjectChange> Changes,
    int SignalCount,
    int PduCount,
    int FrameCount,
    int ClusterCount);

/// <summary>
/// The answer to "what changed for my ECU?" between two deliveries. Changes
/// are classified relevant/unrelated by closure over the union reference
/// graph of both deliveries, seeded at the ECU instance.
/// </summary>
public sealed record ImpactReport(
    string Ecu,
    string EcuPath,

    /// <summary>True when the ECU exists only in the before delivery.</summary>
    bool EcuRemoved,
    int ClosureSize,
    int RelevantCount,
    int UnrelatedCount,
    int BreakingCount,
    IReadOnlyList<ImpactFinding> Findings,
    IReadOnlyList<ObjectChange> RelevantChanges,
    CommunicationImpact Communication);
