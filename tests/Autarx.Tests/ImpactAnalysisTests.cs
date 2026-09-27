using Autarx.Core.Diff;
using Autarx.Core.Impact;
using Autarx.Core.Index;
using Xunit;

namespace Autarx.Tests;

public class ImpactAnalysisTests
{
    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static (ImpactReport Report, WorkspaceDiff Diff) Analyze(string ecuName)
    {
        var before = WorkspaceIndex.Build(FixtureDir("DiffBefore"));
        var after = WorkspaceIndex.Build(FixtureDir("DiffAfter"));

        var index = after.Resolve(ecuName).Object ?? before.Resolve(ecuName).Object
            ?? throw new InvalidOperationException($"fixture ECU {ecuName} not found");
        var ecuRemoved = after.Resolve(ecuName).Object is null;

        var diff = SemanticDiff.Compare(before, after, includePropertyDetail: true);
        var report = ImpactAnalysis.Analyze(before, after, index.AbsolutePath, ecuRemoved, diff, includePropertyDetail: true);
        return (report, diff);
    }

    [Fact]
    public void Bus_coupled_changes_count_as_relevant()
    {
        var (report, _) = Analyze("RadarFL");

        // RadarFL sits on VehicleCan; the delivery adds Display and removes
        // Legacy + SpareCan on the same bus — all of it is RadarFL's business.
        var relevantPaths = report.RelevantChanges.Select(o => o.AbsolutePath).ToHashSet();
        Assert.Contains("/Vehicle/Ecus/Display", relevantPaths);
        Assert.Contains("/Vehicle/Ecus/Legacy", relevantPaths);
        Assert.Contains("/Vehicle/Clusters/SpareCan", relevantPaths);
        Assert.Contains("/Config/Diag", relevantPaths);

        // the retyped SW component has no relationship to RadarFL
        Assert.DoesNotContain("/SwCs/AdaptiveApp", relevantPaths);
        Assert.Equal(4, report.RelevantCount);
        Assert.Equal(1, report.UnrelatedCount);
    }

    [Fact]
    public void Removed_objects_that_were_referenced_break_the_ecu()
    {
        var (report, _) = Analyze("RadarFL");

        // Legacy (referenced by /Config/Diag) and SpareCan (referenced by Legacy)
        Assert.Equal(2, report.BreakingCount);
        Assert.Contains(report.Findings, f => f.Rule == "ARX-IMP-REMOVED-REFERENCED" && f.ObjectPath == "/Vehicle/Ecus/Legacy");
        Assert.Contains(report.Findings, f => f.Rule == "ARX-IMP-REMOVED-REFERENCED" && f.ObjectPath == "/Vehicle/Clusters/SpareCan");
    }

    [Fact]
    public void Reference_retarget_away_from_the_ecu_is_reported()
    {
        var (report, _) = Analyze("RadarFL");

        // /Config/Diag retargeted its VALUE-REF from Legacy to RadarFL:
        // the added edge touches the ECU itself.
        Assert.Contains(report.Findings, f =>
            f.Rule == "ARX-IMP-REF-ADDED"
            && f.ObjectPath == "/Config/Diag"
            && f.Detail.Contains("/Vehicle/Ecus/RadarFL"));
    }

    [Fact]
    public void Communication_impact_counts_cluster_level_changes()
    {
        var (report, _) = Analyze("RadarFL");

        Assert.Equal(1, report.Communication.ClusterCount);
        Assert.Equal(0, report.Communication.FrameCount);
        Assert.Contains(report.Communication.Changes, o => o.AbsolutePath == "/Vehicle/Clusters/SpareCan");
    }

    [Fact]
    public void Removed_ecu_is_detected_and_still_analyzed()
    {
        var (report, _) = Analyze("Legacy");

        Assert.True(report.EcuRemoved);
        Assert.Contains(report.RelevantChanges, o => o.AbsolutePath == "/Vehicle/Ecus/Legacy");
        Assert.True(report.BreakingCount > 0);
    }

    [Fact]
    public void Closure_stays_within_the_union_of_both_deliveries()
    {
        var before = WorkspaceIndex.Build(FixtureDir("DiffBefore"));
        var after = WorkspaceIndex.Build(FixtureDir("DiffAfter"));
        var known = before.Objects.Select(o => o.AbsolutePath)
            .Concat(after.Objects.Select(o => o.AbsolutePath))
            .ToHashSet();

        var diff = SemanticDiff.Compare(before, after, includePropertyDetail: false);
        var report = ImpactAnalysis.Analyze(before, after, "/Vehicle/Ecus/RadarFL", ecuRemoved: false, diff, includePropertyDetail: false);

        // unresolved reference targets must never join the closure
        Assert.True(report.ClosureSize <= known.Count);
        Assert.All(report.RelevantChanges, o => Assert.Contains(o.AbsolutePath, known));
    }
}
