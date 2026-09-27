using Autarx.Core.Index;
using Autarx.Core.Models;
using Autarx.Core.Parsing;
using Xunit;

namespace Autarx.Tests;

public class WorkspaceSummaryTests
{
    private static WorkspaceSummary BuildDeliverySummary() =>
        WorkspaceIndex
            .Build(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OemDelivery"))
            .Summarize();

    [Fact]
    public void Counts_files_elements_and_packages()
    {
        var summary = BuildDeliverySummary();

        Assert.Equal(2, summary.FileCount);
        Assert.Equal(11, summary.PackageCount);
        Assert.Equal(11, summary.IdentifiableCount);
        Assert.True(summary.TotalElementCount > 50);
        Assert.True(summary.TotalFileBytes > 0);
    }

    [Fact]
    public void Counts_references_and_unresolved()
    {
        var summary = BuildDeliverySummary();

        Assert.Equal(8, summary.ReferenceCount);
        Assert.Equal(1, summary.UnresolvedReferenceCount);
    }

    [Fact]
    public void Reports_semantic_inventory()
    {
        var summary = BuildDeliverySummary();

        Assert.Equal(1, summary.SemanticCounts.GetValueOrDefault(SemanticKind.System));
        Assert.Equal(3, summary.SemanticCounts.GetValueOrDefault(SemanticKind.Ecu));
        Assert.Equal(1, summary.SemanticCounts.GetValueOrDefault(SemanticKind.CommunicationCluster));
        Assert.Equal(1, summary.SemanticCounts.GetValueOrDefault(SemanticKind.Frame));
        Assert.Equal(1, summary.SemanticCounts.GetValueOrDefault(SemanticKind.Pdu));
        Assert.Equal(1, summary.SemanticCounts.GetValueOrDefault(SemanticKind.Signal));
        Assert.Equal(1, summary.SemanticCounts.GetValueOrDefault(SemanticKind.SoftwareComponent));
        Assert.Equal(2, summary.SemanticCounts.GetValueOrDefault(SemanticKind.Port));
        Assert.Equal(1, summary.SemanticCounts.GetValueOrDefault(SemanticKind.PortInterface));
    }

    [Fact]
    public void Counts_nested_named_elements_including_unknowns()
    {
        var summary = BuildDeliverySummary();

        // Named elements: 11 identifiables + 2 ports + 1 nested signal/pdu
        // mapping + 3 CAN communication connectors = 17. Classified kinds sum
        // to 12; the mappings and connectors are honestly reported as
        // unknown, never faked into a known kind.
        Assert.Equal(17, summary.UnknownNamedCount + summary.SemanticCounts.Values.Sum());
        Assert.Equal(5, summary.UnknownNamedCount);
    }

    [Fact]
    public void Derives_autosar_release_from_schema_location()
    {
        var summary = BuildDeliverySummary();

        Assert.Equal(["http://autosar.org/schema/r4.0"], summary.Namespaces);
        Assert.Equal("4.3.0", summary.AutosarRelease);
        Assert.False(summary.MixedSchema);
    }

    [Theory]
    [InlineData("AUTOSAR_4-3-0.xsd", "4.3.0")]
    [InlineData("AUTOSAR_R24-11.xsd", "R24-11")]
    [InlineData("AUTOSAR_00050.xsd", "00050")]
    [InlineData("vendor-specific.xsd", null)]
    [InlineData(null, null)]
    public void Release_parser_handles_known_patterns(string? schemaLocation, string? expected) =>
        Assert.Equal(expected, AutosarReleaseParser.Derive(schemaLocation));
}
