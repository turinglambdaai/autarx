using Autarx.Core.Communication;
using Autarx.Core.Index;
using Xunit;

namespace Autarx.Tests;

public class CommunicationProjectionTests
{
    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static CommunicationModel Build(string fixture) =>
        CommunicationProjection.Build(WorkspaceIndex.Build(FixtureDir(fixture)));

    [Fact]
    public void Walks_the_full_chain_from_cluster_to_signals()
    {
        var model = Build("CommDelivery");

        var cluster = Assert.Single(model.Clusters);
        Assert.Equal("/Vehicle/Clusters/VehicleCan", cluster.ClusterPath);
        Assert.Equal(["/Vehicle/Ecus/Gateway", "/Vehicle/Ecus/RadarFL"], cluster.ConnectedEcus);
        Assert.Equal(
            ["/Vehicle/Frames/Frame_VehicleSpeed", "/Vehicle/Frames/Frame_VehicleSpeedFa"],
            cluster.FramePaths);
    }

    [Fact]
    public void Pairs_pdus_to_frames_through_mapping_objects()
    {
        var model = Build("CommDelivery");

        var speed = model.Frames.Single(f => f.FramePath == "/Vehicle/Frames/Frame_VehicleSpeed");
        Assert.Equal(["/Vehicle/Pdus/Pdu_VehicleSpeed"], speed.PduPaths);
    }

    [Fact]
    public void Attaches_signals_to_pdus_from_nested_mappings()
    {
        var model = Build("CommDelivery");

        var fa = model.Pdus.Single(p => p.PduPath == "/Vehicle/Pdus/Pdu_VehicleSpeedFa");
        Assert.Equal(
            ["/Vehicle/Signals/BrakePressure", "/Vehicle/Signals/VehicleSpeed"],
            fa.SignalPaths);
    }

    [Fact]
    public void Reports_unattached_objects_as_orphans()
    {
        var model = Build("CommDelivery");

        Assert.Empty(model.OrphanFrames);
        Assert.Empty(model.OrphanPdus);
        Assert.Equal(["/Vehicle/Signals/DiagCounter"], model.OrphanSignals);
    }

    [Fact]
    public void Lists_ecuc_communication_modules_by_presence()
    {
        var model = Build("CommDelivery");

        // PduR is communication, Mcu is not
        Assert.Equal(["PduR"], model.EcucCommModules);
    }

    [Fact]
    public void Oem_delivery_without_triggerings_keeps_frames_orphaned()
    {
        var model = Build("OemDelivery");

        // the cluster exists with ECUs but no frame triggerings
        var cluster = Assert.Single(model.Clusters);
        Assert.Equal("/Vehicle/Clusters/VehicleCan", cluster.ClusterPath);
        Assert.Empty(cluster.FramePaths);
        Assert.Contains("/Vehicle/Frames/Frame_VehicleSpeed", model.OrphanFrames);

        // the frame↔PDU mapping and the nested signal mapping still resolve
        var frame = model.Frames.Single(f => f.FramePath == "/Vehicle/Frames/Frame_VehicleSpeed");
        Assert.Equal(["/Vehicle/Pdus/Pdu_VehicleSpeed"], frame.PduPaths);
        Assert.Empty(model.OrphanPdus);
        Assert.Empty(model.OrphanSignals);
    }
}
