using Autarx.Core.Diff;
using Autarx.Core.Index;
using Autarx.Core.Models;

namespace Autarx.Core.Impact;

/// <summary>
/// ECU-scoped impact analysis over a semantic diff. Relevance is closure over
/// the union of both deliveries' reference graphs, seeded at the ECU instance:
/// anything the ECU reaches, or that reaches it, in either delivery counts —
/// a change on the far side of the shared bus is still this ECU's business.
/// </summary>
public static class ImpactAnalysis
{
    public static ImpactReport Analyze(
        WorkspaceIndex before,
        WorkspaceIndex after,
        string ecuPath,
        bool ecuRemoved,
        WorkspaceDiff diff,
        bool includePropertyDetail)
    {
        var closure = Closure(before, after, ecuPath);

        var relevant = diff.Objects
            .Where(o => closure.Contains(o.AbsolutePath))
            .ToList();
        var unrelatedCount = diff.Objects.Count - relevant.Count;

        var findings = new List<ImpactFinding>();

        foreach (var change in relevant)
        {
            switch (change.ChangeType)
            {
                case ObjectChangeType.Added:
                    findings.Add(new ImpactFinding(
                        "ARX-IMP-OBJECT-ADDED", ImpactSeverity.Info, change.AbsolutePath,
                        $"{change.ElementType} '{change.ShortName}' was added and connects to the ECU's closure"));
                    break;

                case ObjectChangeType.Removed:
                    var hadIncoming = before.IncomingOf(change.AbsolutePath).Count > 0;
                    findings.Add(new ImpactFinding(
                        hadIncoming ? "ARX-IMP-REMOVED-REFERENCED" : "ARX-IMP-OBJECT-REMOVED",
                        hadIncoming ? ImpactSeverity.Breaking : ImpactSeverity.Info,
                        change.AbsolutePath,
                        hadIncoming
                            ? $"{change.ElementType} '{change.ShortName}' was removed while other objects referenced it"
                            : $"{change.ElementType} '{change.ShortName}' was removed (no references pointed at it)"));
                    break;

                case ObjectChangeType.Modified:
                    if (change.ElementTypeChanged)
                    {
                        findings.Add(new ImpactFinding(
                            "ARX-IMP-TYPE-CHANGED", ImpactSeverity.Breaking, change.AbsolutePath,
                            $"element type changed {change.BeforeElementType} → {change.AfterElementType} under the same identity"));
                    }
                    else if (includePropertyDetail && change.Properties is { Count: > 0 })
                    {
                        findings.Add(new ImpactFinding(
                            "ARX-IMP-PROPERTY-CHANGED", ImpactSeverity.Info, change.AbsolutePath,
                            $"{change.Properties.Count} property change(s): " +
                            string.Join("; ", change.Properties.Take(5).Select(p =>
                                $"{p.Path}: {p.Before ?? "(absent)"} → {p.After ?? "(absent)"}")) +
                            (change.Properties.Count > 5 ? "; …" : "")));
                    }
                    else
                    {
                        findings.Add(new ImpactFinding(
                            "ARX-IMP-OBJECT-MODIFIED", ImpactSeverity.Info, change.AbsolutePath,
                            $"content changed under {change.ElementType} '{change.ShortName}'"));
                    }
                    break;
            }
        }

        foreach (var reference in diff.References)
        {
            var sourceRelevant = closure.Contains(reference.SourcePath);
            var targetRelevant = closure.Contains(reference.TargetPath);
            if (!sourceRelevant && !targetRelevant)
                continue;

            var touchesEcu = reference.SourcePath == ecuPath || reference.TargetPath == ecuPath;
            var severity = reference.ChangeType == ObjectChangeType.Removed && touchesEcu
                ? ImpactSeverity.Breaking
                : ImpactSeverity.Info;

            findings.Add(new ImpactFinding(
                reference.ChangeType == ObjectChangeType.Added ? "ARX-IMP-REF-ADDED" : "ARX-IMP-REF-REMOVED",
                severity,
                reference.SourcePath,
                $"{reference.Kind} {reference.ChangeType.ToString().ToLowerInvariant()}: {reference.TargetPath}"));
        }

        findings.Sort((a, b) =>
        {
            var severity = a.Severity.CompareTo(b.Severity);
            return severity != 0 ? severity : string.CompareOrdinal(a.ObjectPath, b.ObjectPath);
        });

        var communicationKinds = new HashSet<SemanticKind>
        {
            SemanticKind.Signal, SemanticKind.Pdu, SemanticKind.Frame, SemanticKind.CommunicationCluster,
        };
        var communicationChanges = relevant
            .Where(o => communicationKinds.Contains(o.SemanticKind))
            .ToList();

        return new ImpactReport(
            ecuPath.Split('/').Last(),
            ecuPath,
            ecuRemoved,
            closure.Count,
            relevant.Count,
            unrelatedCount,
            findings.Count(f => f.Severity == ImpactSeverity.Breaking),
            findings,
            relevant,
            new CommunicationImpact(
                communicationChanges,
                communicationChanges.Count(o => o.SemanticKind == SemanticKind.Signal),
                communicationChanges.Count(o => o.SemanticKind == SemanticKind.Pdu),
                communicationChanges.Count(o => o.SemanticKind == SemanticKind.Frame),
                communicationChanges.Count(o => o.SemanticKind == SemanticKind.CommunicationCluster)));
    }

    /// <summary>
    /// Breadth-first closure over the union graph of both deliveries. Edges
    /// only join closure when their far endpoint is an indexed object on that
    /// side, so unresolved targets never inflate the set.
    /// </summary>
    private static HashSet<string> Closure(WorkspaceIndex before, WorkspaceIndex after, string seed)
    {
        var beforePaths = new HashSet<string>(
            before.Objects.Select(o => o.AbsolutePath), StringComparer.Ordinal);
        var afterPaths = new HashSet<string>(
            after.Objects.Select(o => o.AbsolutePath), StringComparer.Ordinal);

        var closure = new HashSet<string>(StringComparer.Ordinal) { seed };
        var queue = new Queue<string>();
        queue.Enqueue(seed);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var neighbour in Neighbours(before, current, beforePaths)
                .Concat(Neighbours(after, current, afterPaths)))
            {
                if (closure.Add(neighbour))
                    queue.Enqueue(neighbour);
            }
        }

        return closure;

        static IEnumerable<string> Neighbours(WorkspaceIndex index, string path, HashSet<string> known)
        {
            foreach (var reference in index.OutgoingOf(path))
                if (known.Contains(reference.TargetPath))
                    yield return reference.TargetPath;
            foreach (var reference in index.IncomingOf(path))
                if (known.Contains(reference.SourcePath))
                    yield return reference.SourcePath;
        }
    }
}
