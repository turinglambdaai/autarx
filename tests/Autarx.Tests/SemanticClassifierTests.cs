using Autarx.Core.Models;
using Xunit;

namespace Autarx.Tests;

public class SemanticClassifierTests
{
    [Theory]
    [InlineData("SYSTEM", SemanticKind.System)]
    [InlineData("ECU-INSTANCE", SemanticKind.Ecu)]
    [InlineData("CAN-CLUSTER", SemanticKind.CommunicationCluster)]
    [InlineData("ETHERNET-CLUSTER", SemanticKind.CommunicationCluster)]
    [InlineData("CAN-FRAME", SemanticKind.Frame)]
    [InlineData("LIN-FRAME", SemanticKind.Frame)]
    [InlineData("I-SIGNAL", SemanticKind.Signal)]
    [InlineData("SYSTEM-SIGNAL", SemanticKind.Signal)]
    [InlineData("I-SIGNAL-I-PDU", SemanticKind.Pdu)]
    [InlineData("N-PDU", SemanticKind.Pdu)]
    [InlineData("SECURED-I-PDU", SemanticKind.Pdu)]
    [InlineData("APPLICATION-SW-COMPONENT-TYPE", SemanticKind.SoftwareComponent)]
    [InlineData("COMPOSITION-SW-COMPONENT-TYPE", SemanticKind.SoftwareComponent)]
    [InlineData("ECU-ABSTRACTION-SW-COMPONENT-TYPE", SemanticKind.SoftwareComponent)]
    [InlineData("P-PORT-PROTOTYPE", SemanticKind.Port)]
    [InlineData("R-PORT-PROTOTYPE", SemanticKind.Port)]
    [InlineData("PR-PORT-PROTOTYPE", SemanticKind.Port)]
    [InlineData("SENDER-RECEIVER-INTERFACE", SemanticKind.PortInterface)]
    [InlineData("CLIENT-SERVER-INTERFACE", SemanticKind.PortInterface)]
    [InlineData("ECUC-MODULE-CONFIGURATION-VALUES", SemanticKind.EcucModule)]
    public void Classifies_known_element_types(string elementType, SemanticKind expected) =>
        Assert.Equal(expected, SemanticClassifier.Classify(elementType));

    [Theory]
    [InlineData("PDU-TO-FRAME-MAPPING")]
    [InlineData("ISIGNAL-TO-I-PDU-MAPPING")]
    [InlineData("SOME-FUTURE-TYPE")]
    public void Falls_back_to_unknown_without_failing(string elementType) =>
        Assert.Equal(SemanticKind.Unknown, SemanticClassifier.Classify(elementType));
}
