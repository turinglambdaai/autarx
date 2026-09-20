using Autarx.Core.Index;
using Xunit;

namespace Autarx.Tests;

public class TraceTests
{
    private static WorkspaceIndex BuildDelivery() =>
        WorkspaceIndex.Build(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OemDelivery"));

    private const string PduPath = "/Vehicle/Pdus/Pdu_VehicleSpeed";
    private const string ClusterPath = "/Vehicle/Clusters/VehicleCan";

    [Fact]
    public void Both_directions_depth_one()
    {
        var trace = BuildDelivery().Trace(PduPath, TraceDirection.Both, 1);

        Assert.Equal(3, trace.Nodes.Count); // pdu + signal + frame mapping
        Assert.Equal(2, trace.Edges.Count);
        Assert.Contains(trace.Edges, e => e.Kind == "I-SIGNAL-REF" && e.TargetPath == "/Vehicle/Signals/VehicleSpeed" && e.Depth == 1);
        Assert.Contains(trace.Edges, e => e.Kind == "I-PDU-REF" && e.SourcePath == "/Vehicle/FrameMappings/Map_VehicleSpeed" && e.Depth == 1);
    }

    [Fact]
    public void Both_directions_depth_two_reaches_the_frame()
    {
        var trace = BuildDelivery().Trace(PduPath, TraceDirection.Both, 2);

        // Pdu -> Signal, Pdu <- Mapping, Mapping -> Frame (+ revisited Pdu edge)
        Assert.Equal(4, trace.Nodes.Count);
        Assert.Equal(5, trace.Edges.Count);
        Assert.Contains(trace.Nodes, n => n.Path == "/Vehicle/Frames/Frame_VehicleSpeed" && n.Depth == 2);
    }

    [Fact]
    public void Outgoing_only_does_not_walk_upstream()
    {
        var trace = BuildDelivery().Trace(PduPath, TraceDirection.Outgoing, 2);

        Assert.Equal(2, trace.Nodes.Count); // pdu + signal
        Assert.All(trace.Edges, e => Assert.Equal(PduPath, e.SourcePath));
    }

    [Fact]
    public void Incoming_only_lists_every_ecu_attached_to_the_cluster()
    {
        var trace = BuildDelivery().Trace(ClusterPath, TraceDirection.Incoming, 1);

        Assert.Equal(3, trace.Edges.Count);
        Assert.All(trace.Edges, e => Assert.Equal("CHANNEL-REF", e.Kind));
        Assert.All(trace.Edges, e => Assert.Equal(ClusterPath, e.TargetPath));
        Assert.Contains(trace.Nodes, n => n.Path == "/Vehicle/Ecus/Gateway");
    }

    [Fact]
    public void Depth_zero_returns_only_the_root()
    {
        var trace = BuildDelivery().Trace(PduPath, TraceDirection.Both, 0);

        var node = Assert.Single(trace.Nodes);
        Assert.Equal(PduPath, node.Path);
        Assert.Empty(trace.Edges);
    }
}
