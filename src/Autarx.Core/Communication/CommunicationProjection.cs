using Autarx.Core.Index;
using Autarx.Core.Models;

namespace Autarx.Core.Communication;

/// <summary>
/// Communication projection of a workspace, derived purely from the reference
/// graph — no AUTOSAR schema semantics are hardcoded beyond element-kind
/// classification. Frames attach to clusters through frame triggerings nested
/// inside the cluster (attributed to the cluster by the index); PDUs attach to
/// frames through PDU-to-frame mapping objects that reference both; signals
/// attach to PDUs through nested signal-to-PDU mappings. Relationship
/// directions (sender/receiver) are deliberately not claimed: the structural
/// graph does not carry them reliably, and the projection never invents them.
/// </summary>
public static class CommunicationProjection
{
    public static CommunicationModel Build(WorkspaceIndex index)
    {
        var objectsByPath = index.Objects.ToDictionary(o => o.AbsolutePath, StringComparer.Ordinal);

        var connectedEcus = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var clusterFrames = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var framePdus = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var pduSignals = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        // ECU → cluster/channel edges from the ECU side (connectors are nested
        // under the ECU, so CHANNEL-REF is attributed to the ECU-INSTANCE).
        foreach (var reference in index.References)
        {
            if (objectsByPath.TryGetValue(reference.SourcePath, out var source)
                && source.SemanticKind == SemanticKind.Ecu
                && reference.Kind.EndsWith("CHANNEL-REF", StringComparison.Ordinal))
            {
                Connected(connectedEcus, reference.TargetPath).Add(reference.SourcePath);
            }
        }

        // Frame mappings: one object holding FRAME-REF + *PDU-REF pairs the
        // two endpoints. The mapping object itself stays anonymous in the
        // projection — engineering identity lives in frame and PDU.
        var refsBySource = index.References.ToLookup(r => r.SourcePath);
        foreach (var group in refsBySource)
        {
            var frameTargets = group.Where(r => r.Kind.EndsWith("FRAME-REF", StringComparison.Ordinal))
                .Select(r => r.TargetPath).ToList();
            var pduTargets = group.Where(r => r.Kind.EndsWith("PDU-REF", StringComparison.Ordinal))
                .Select(r => r.TargetPath).ToList();

            if (frameTargets.Count == 0 || pduTargets.Count == 0)
                continue;

            foreach (var frame in frameTargets)
                foreach (var pdu in pduTargets)
                    Connected(framePdus, frame).Add(pdu);
        }

        // Signal mappings are nested inside the PDU → attributed to the PDU.
        foreach (var reference in index.References)
        {
            if (reference.Kind.EndsWith("SIGNAL-REF", StringComparison.Ordinal))
                Connected(pduSignals, reference.SourcePath).Add(reference.TargetPath);
        }

        // Frames under clusters: triggerings nested inside the cluster
        // attribute their FRAME-REF to the cluster object.
        foreach (var reference in index.References)
        {
            if (reference.Kind.EndsWith("FRAME-REF", StringComparison.Ordinal)
                && reference.SourcePath != reference.TargetPath
                && objectsByPath.TryGetValue(reference.SourcePath, out var holder)
                && holder.SemanticKind == SemanticKind.CommunicationCluster)
            {
                Connected(clusterFrames, reference.SourcePath).Add(reference.TargetPath);
            }
        }

        var clusters = connectedEcus.Keys
            .Concat(clusterFrames.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(path => new ClusterProjection(
                path,
                List(connectedEcus, path),
                List(clusterFrames, path)))
            .ToList();

        var frames = index.Objects
            .Where(o => o.SemanticKind == SemanticKind.Frame)
            .Select(o => o.AbsolutePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(path => new FrameProjection(path, List(framePdus, path)))
            .ToList();

        var pdus = index.Objects
            .Where(o => o.SemanticKind == SemanticKind.Pdu)
            .Select(o => o.AbsolutePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(path => new PduProjection(path, List(pduSignals, path)))
            .ToList();

        var attachedPdus = framePdus.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);
        var signalPaths = pduSignals.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);
        var clusteredFrames = clusterFrames.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);

        var orphanFrames = index.Objects
            .Where(o => o.SemanticKind == SemanticKind.Frame && !clusteredFrames.Contains(o.AbsolutePath))
            .Select(o => o.AbsolutePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var orphanPdus = index.Objects
            .Where(o => o.SemanticKind == SemanticKind.Pdu && !attachedPdus.Contains(o.AbsolutePath))
            .Select(o => o.AbsolutePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var orphanSignals = index.Objects
            .Where(o => o.SemanticKind == SemanticKind.Signal && !signalPaths.Contains(o.AbsolutePath))
            .Select(o => o.AbsolutePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // ECUC modules carrying communication configuration (Com/PduR/CanIf/…)
        // — presence only, never a substitute for vendor tooling.
        var ecucCommModules = index.Objects
            .Where(o => o.SemanticKind == SemanticKind.EcucModule && CommunicationModuleNames.Contains(o.ShortName))
            .Select(o => o.ShortName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return new CommunicationModel(clusters, frames, pdus, orphanFrames, orphanPdus, orphanSignals, ecucCommModules);
    }

    /// <summary>ECUC module short-names whose configuration is communication
    /// related. Presence in the workspace is reported; contents are not
    /// interpreted.</summary>
    private static readonly HashSet<string> CommunicationModuleNames = new(StringComparer.Ordinal)
    {
        "CanIf", "CanSM", "CanNm", "CanTp", "Com", "ComM", "EthIf", "FrIf", "FrSM",
        "LinIf", "LinSM", "PduR", "SecOC", "SoAd", "TcpIp", "Xcp",
    };

    private static SortedSet<string> Connected(Dictionary<string, SortedSet<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new SortedSet<string>(StringComparer.Ordinal);
            map[key] = set;
        }
        return set;
    }

    private static IReadOnlyList<string> List(Dictionary<string, SortedSet<string>> map, string key) =>
        map.TryGetValue(key, out var set) ? set.ToList() : [];
}
