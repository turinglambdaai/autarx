using Autarx.Core.Diff;
using Xunit;

namespace Autarx.Tests;

public class SemanticDiffTests
{
    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static (WorkspaceDiff Diff, Dictionary<string, ObjectChange> ByPath) CompareTo(string afterDir)
    {
        var diff = SemanticDiff.Compare(FixtureDir("DiffBefore"), FixtureDir(afterDir), includePropertyDetail: true);
        return (diff, diff.Objects.ToDictionary(o => o.AbsolutePath));
    }

    [Fact]
    public void Same_workspace_yields_empty_diff()
    {
        var diff = SemanticDiff.Compare(FixtureDir("DiffBefore"), FixtureDir("DiffBefore"));

        Assert.True(diff.IsEmpty);
        Assert.Empty(diff.Objects);
        Assert.Empty(diff.References);
    }

    [Fact]
    public void Detects_added_removed_and_modified_objects()
    {
        var (diff, byPath) = CompareTo("DiffAfter");

        Assert.Equal(ObjectChangeType.Added, byPath["/Vehicle/Ecus/Display"].ChangeType);
        Assert.Equal(ObjectChangeType.Removed, byPath["/Vehicle/Ecus/Legacy"].ChangeType);
        Assert.Equal(ObjectChangeType.Removed, byPath["/Vehicle/Clusters/SpareCan"].ChangeType);
        Assert.Equal(ObjectChangeType.Modified, byPath["/Config/Diag"].ChangeType);

        // unchanged on both sides — must not appear
        Assert.DoesNotContain("/Vehicle/Ecus/RadarFL", byPath.Keys);
        Assert.DoesNotContain("/Vehicle/Systems/VehicleSystem", byPath.Keys);
    }

    [Fact]
    public void Explains_parameter_and_reference_changes_at_property_level()
    {
        var (_, byPath) = CompareTo("DiffAfter");

        var properties = byPath["/Config/Diag"].Properties!;
        var paramChange = Assert.Single(properties, p => p.Path.EndsWith("/DiagParamA/VALUE"));
        Assert.Equal(PropertyChangeType.Changed, paramChange.ChangeType);
        Assert.Equal("1", paramChange.Before);
        Assert.Equal("2", paramChange.After);

        var refChange = Assert.Single(properties, p => p.Path.EndsWith("/DiagTesterRef/VALUE-REF"));
        Assert.Equal("/Vehicle/Ecus/Legacy", refChange.Before);
        Assert.Equal("/Vehicle/Ecus/RadarFL", refChange.After);

        // untouched nested parameter must not be reported
        Assert.DoesNotContain(properties, p => p.Path.Contains("DiagParamB"));
    }

    [Fact]
    public void Flags_element_type_change_under_same_identity()
    {
        var (_, byPath) = CompareTo("DiffAfter");

        var change = byPath["/SwCs/AdaptiveApp"];
        Assert.Equal(ObjectChangeType.Modified, change.ChangeType);
        Assert.True(change.ElementTypeChanged);
        Assert.Equal("APPLICATION-SW-COMPONENT-TYPE", change.BeforeElementType);
        Assert.Equal("COMPOSITION-SW-COMPONENT-TYPE", change.AfterElementType);
    }

    [Fact]
    public void Diffs_references_by_semantic_identity_not_by_file()
    {
        var (diff, _) = CompareTo("DiffAfter");

        var removed = diff.References.Where(r => r.ChangeType == ObjectChangeType.Removed).ToList();
        var added = diff.References.Where(r => r.ChangeType == ObjectChangeType.Added).ToList();

        Assert.Contains(removed, r => r.SourcePath == "/Vehicle/Ecus/Legacy" && r.TargetPath == "/Vehicle/Clusters/SpareCan");
        Assert.Contains(removed, r => r.SourcePath == "/Config/Diag" && r.TargetPath == "/Vehicle/Ecus/Legacy" && r.Kind == "VALUE-REF");
        Assert.Contains(added, r => r.SourcePath == "/Vehicle/Ecus/Display" && r.TargetPath == "/Vehicle/Clusters/VehicleCan");
        Assert.Contains(added, r => r.SourcePath == "/Config/Diag" && r.TargetPath == "/Vehicle/Ecus/RadarFL" && r.Kind == "VALUE-REF");
        Assert.Equal(4, diff.References.Count);
    }

    [Fact]
    public void Without_detail_modified_objects_carry_no_property_explanation()
    {
        var diff = SemanticDiff.Compare(FixtureDir("DiffBefore"), FixtureDir("DiffAfter"), includePropertyDetail: false);

        var diag = diff.Objects.Single(o => o.AbsolutePath == "/Config/Diag");
        Assert.Null(diag.Properties);
    }
}
