namespace Autarx.Core.Communication;

/// <summary>
/// The communication projection: clusters with their connected ECUs and
/// frames, frames with their PDUs, PDUs with their signals — plus everything
/// not reachable from a cluster ("orphans") and the ECUC communication
/// modules present in the workspace. Purely structural: sender/receiver
/// direction is not claimed anywhere.
/// </summary>
public sealed record CommunicationModel(
    IReadOnlyList<ClusterProjection> Clusters,
    IReadOnlyList<FrameProjection> Frames,
    IReadOnlyList<PduProjection> Pdus,
    IReadOnlyList<string> OrphanFrames,
    IReadOnlyList<string> OrphanPdus,
    IReadOnlyList<string> OrphanSignals,
    IReadOnlyList<string> EcucCommModules)
{
    /// <summary>True when the workspace carries no communication content at all.</summary>
    public bool IsEmpty =>
        Clusters.Count == 0 && Frames.Count == 0 && Pdus.Count == 0;
}

public sealed record ClusterProjection(
    string ClusterPath,
    IReadOnlyList<string> ConnectedEcus,
    IReadOnlyList<string> FramePaths);

public sealed record FrameProjection(
    string FramePath,
    IReadOnlyList<string> PduPaths);

public sealed record PduProjection(
    string PduPath,
    IReadOnlyList<string> SignalPaths);
