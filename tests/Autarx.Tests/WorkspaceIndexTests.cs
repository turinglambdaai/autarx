using Autarx.Core.Index;
using Xunit;

namespace Autarx.Tests;

public class WorkspaceIndexTests
{
    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static WorkspaceIndex BuildDelivery() => WorkspaceIndex.Build(FixtureDir("OemDelivery"));

    [Fact]
    public void Indexes_both_files_and_all_identifiables()
    {
        var index = BuildDelivery();

        Assert.Equal(2, index.Documents.Count);
        Assert.Equal(11, index.Objects.Count);
        Assert.Equal(11, index.PackageCount);
    }

    [Fact]
    public void Builds_absolute_paths_across_nested_packages()
    {
        var index = BuildDelivery();

        var byPath = index.Objects.ToDictionary(o => o.AbsolutePath);
        Assert.Contains("/Vehicle/Systems/VehicleSystem", byPath.Keys);
        Assert.Contains("/Vehicle/Ecus/RadarFL", byPath.Keys);
        Assert.Contains("/Vehicle/Clusters/VehicleCan", byPath.Keys);
        Assert.Contains("/Vehicle/Signals/VehicleSpeed", byPath.Keys);
        Assert.Contains("/Vehicle/Frames/Frame_VehicleSpeed", byPath.Keys);
    }

    [Fact]
    public void Classifies_semantic_kinds()
    {
        var index = BuildDelivery();

        Assert.Equal(Core.Models.SemanticKind.System, index.Resolve("/Vehicle/Systems/VehicleSystem").Object!.SemanticKind);
        Assert.Equal(Core.Models.SemanticKind.Ecu, index.Resolve("/Vehicle/Ecus/RadarFL").Object!.SemanticKind);
        Assert.Equal(Core.Models.SemanticKind.CommunicationCluster, index.Resolve("/Vehicle/Clusters/VehicleCan").Object!.SemanticKind);
        Assert.Equal(Core.Models.SemanticKind.Pdu, index.Resolve("/Vehicle/Pdus/Pdu_VehicleSpeed").Object!.SemanticKind);
        Assert.Equal(Core.Models.SemanticKind.Frame, index.Resolve("/Vehicle/Frames/Frame_VehicleSpeed").Object!.SemanticKind);
        Assert.Equal(Core.Models.SemanticKind.SoftwareComponent, index.Resolve("/Vehicle/SwCs/SpeedSensor").Object!.SemanticKind);
        Assert.Equal(Core.Models.SemanticKind.PortInterface, index.Resolve("/Vehicle/Interfaces/IF_VehicleSpeed").Object!.SemanticKind);

        // Mapping objects are honestly Unknown for now — indexed and
        // referenceable, but not pretended to be a known kind.
        Assert.Equal(Core.Models.SemanticKind.Unknown, index.Resolve("/Vehicle/FrameMappings/Map_VehicleSpeed").Object!.SemanticKind);
    }

    [Fact]
    public void Finds_by_name_substring_case_insensitive()
    {
        var index = BuildDelivery();
        var matches = index.FindByName("Speed");

        Assert.Equal(
        [
            "/Vehicle/FrameMappings/Map_VehicleSpeed",
            "/Vehicle/Frames/Frame_VehicleSpeed",
            "/Vehicle/Interfaces/IF_VehicleSpeed",
            "/Vehicle/Pdus/Pdu_VehicleSpeed",
            "/Vehicle/Signals/VehicleSpeed",
            "/Vehicle/SwCs/SpeedSensor",
        ],
        matches.Select(m => m.AbsolutePath));
    }

    [Fact]
    public void Resolves_paths_and_names()
    {
        var index = BuildDelivery();

        Assert.Equal(ResolveStatus.Found, index.Resolve("/Vehicle/Signals/VehicleSpeed").Status);
        Assert.Equal(ResolveStatus.Found, index.Resolve("VehicleSpeed").Status);
        Assert.Equal(ResolveStatus.NotFound, index.Resolve("/No/Such/Object").Status);
        Assert.Equal(ResolveStatus.NotFound, index.Resolve("NoSuchName").Status);
    }

    [Fact]
    public void Reports_ambiguous_short_names_instead_of_guessing()
    {
        var index = WorkspaceIndex.Build(FixtureDir("Ambiguous"));

        var resolution = index.Resolve("SameName");
        Assert.Equal(ResolveStatus.Ambiguous, resolution.Status);
        Assert.Equal(
        ["/A/SameName", "/B/SameName"],
        resolution.Candidates.Select(c => c.AbsolutePath));

        // Absolute paths stay unique and resolvable.
        Assert.Equal(ResolveStatus.Found, index.Resolve("/A/SameName").Status);
    }

    [Fact]
    public void Detects_duplicate_paths_across_files()
    {
        var index = WorkspaceIndex.Build(FixtureDir("DuplicatePaths"));

        var duplicate = Assert.Single(index.DuplicatePaths);
        Assert.Equal("/Dup/DupEcu", duplicate.AbsolutePath);
        Assert.Equal(2, duplicate.SourceFiles.Count);

        // Both occurrences stay listed; path lookup deterministically
        // resolves to the first one (files are processed in sorted order).
        Assert.Equal(2, index.Objects.Count(o => o.AbsolutePath == "/Dup/DupEcu"));
        Assert.EndsWith("dup-a.arxml", index.Resolve("/Dup/DupEcu").Object!.SourceFile, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_ecuc_file_indexes_modules_but_no_definition_refs()
    {
        var index = WorkspaceIndex.Build(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal.arxml"));

        Assert.Equal(2, index.Objects.Count);

        // DEFINITION-REF points at module definitions that live outside the
        // delivery — excluded from the reference graph by design. The one
        // ECUC VALUE-REF stays a genuine (here unresolved) reference.
        var reference = Assert.Single(index.References);
        Assert.Equal("VALUE-REF", reference.Kind);
        Assert.False(reference.IsResolved);
    }
}
