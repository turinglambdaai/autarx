using Autarx.Core.Index;
using Xunit;

namespace Autarx.Tests;

public class ReferenceGraphTests
{
    private static WorkspaceIndex BuildDelivery() =>
        WorkspaceIndex.Build(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OemDelivery"));

    [Fact]
    public void Counts_all_references_and_the_unresolved_one()
    {
        var index = BuildDelivery();

        // 3x CHANNEL-REF (ECU connectors), I-SIGNAL-REF, FRAME-REF, I-PDU-REF,
        // PROVIDED-INTERFACE-TREF, REQUIRED-INTERFACE-TREF.
        Assert.Equal(8, index.References.Count);
        var unresolved = Assert.Single(index.UnresolvedReferences);
        Assert.Equal("REQUIRED-INTERFACE-TREF", unresolved.Kind);
        Assert.Equal("/Vehicle/Interfaces/IF_Calibration", unresolved.TargetPath);
        Assert.False(unresolved.IsResolved);
    }

    [Fact]
    public void Outgoing_references_carry_the_owning_object_and_nested_chain()
    {
        var index = BuildDelivery();

        var pdu = index.Resolve("/Vehicle/Pdus/Pdu_VehicleSpeed").Object!;
        var outgoing = index.OutgoingOf(pdu.AbsolutePath);

        var reference = Assert.Single(outgoing);
        Assert.Equal("I-SIGNAL-REF", reference.Kind);
        Assert.Equal("/Vehicle/Signals/VehicleSpeed", reference.TargetPath);
        Assert.True(reference.IsResolved);
        // The reference sits inside the nested ISIGNAL-TO-I-PDU-MAPPING —
        // attribution points at the exact element, not just the owning PDU.
        Assert.Equal("/Vehicle/Pdus/Pdu_VehicleSpeed/VehicleSpeedMapping", reference.SourceElementPath);
    }

    [Fact]
    public void Reverse_references_work_without_special_casing()
    {
        var index = BuildDelivery();

        var signal = index.Resolve("/Vehicle/Signals/VehicleSpeed").Object!;
        var incoming = index.IncomingOf(signal.AbsolutePath);

        var reference = Assert.Single(incoming);
        Assert.Equal("I-SIGNAL-REF", reference.Kind);
        Assert.Equal("/Vehicle/Pdus/Pdu_VehicleSpeed", reference.SourcePath);
    }

    [Fact]
    public void Ecu_connectors_reach_the_cluster_through_nested_refs()
    {
        var index = BuildDelivery();

        var radarFl = index.Resolve("/Vehicle/Ecus/RadarFL").Object!;
        var outgoing = Assert.Single(index.OutgoingOf(radarFl.AbsolutePath));

        Assert.Equal("CHANNEL-REF", outgoing.Kind);
        Assert.Equal("/Vehicle/Clusters/VehicleCan", outgoing.TargetPath);
        Assert.EndsWith("Con_RadarFL", outgoing.SourceElementPath, StringComparison.Ordinal);

        // And the reverse graph sees the cluster's three attached ECUs.
        Assert.Equal(3, index.IncomingOf("/Vehicle/Clusters/VehicleCan").Count);
    }

    [Fact]
    public void References_resolve_across_files()
    {
        var index = BuildDelivery();

        // Signal lives in communication.arxml, the referencing PDU mapping too,
        // but ECUs (system.arxml) reference the cluster across the file boundary.
        var crossFile = index.References
            .Where(r => r.Kind == "CHANNEL-REF")
            .ToList();

        Assert.NotEmpty(crossFile);
        Assert.All(crossFile, r => Assert.True(r.IsResolved));
        Assert.All(crossFile, r => Assert.NotEqual(r.SourceFile, index.Resolve(r.TargetPath).Object!.SourceFile));
    }
}
