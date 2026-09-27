namespace Autarx.Core.Models;

/// <summary>
/// Coarse semantic classification of an AUTOSAR element type. Deliberately
/// partial: unmapped element types fall back to <see cref="Unknown"/> and
/// never fail parsing. New kinds are appended over time — never renumbered.
/// </summary>
public enum SemanticKind
{
    Unknown,
    System,
    Ecu,
    CommunicationCluster,
    Frame,
    Pdu,
    Signal,
    SoftwareComponent,
    Port,
    PortInterface,
    EcucModule,
}

/// <summary>
/// Lightweight element-type → semantic-kind mapping. Exact names cover the
/// types every OEM delivery contains; suffix rules cover families that AUTOSAR
/// keeps open-ended (PDU/SDU flavours, port interfaces, port prototypes,
/// SW component types). Everything else is Unknown.
/// </summary>
public static class SemanticClassifier
{
    private static readonly Dictionary<string, SemanticKind> ExactNames = new(StringComparer.Ordinal)
    {
        ["SYSTEM"] = SemanticKind.System,
        ["ECU-INSTANCE"] = SemanticKind.Ecu,
        ["CAN-CLUSTER"] = SemanticKind.CommunicationCluster,
        ["ETHERNET-CLUSTER"] = SemanticKind.CommunicationCluster,
        ["FLEXRAY-CLUSTER"] = SemanticKind.CommunicationCluster,
        ["LIN-CLUSTER"] = SemanticKind.CommunicationCluster,
        ["CAN-FRAME"] = SemanticKind.Frame,
        ["LIN-FRAME"] = SemanticKind.Frame,
        ["ETHERNET-FRAME"] = SemanticKind.Frame,
        ["FLEXRAY-FRAME"] = SemanticKind.Frame,
        ["I-SIGNAL"] = SemanticKind.Signal,
        ["I-SIGNAL-GROUP"] = SemanticKind.Signal,
        ["SYSTEM-SIGNAL"] = SemanticKind.Signal,
        ["APPLICATION-SW-COMPONENT-TYPE"] = SemanticKind.SoftwareComponent,
        ["COMPOSITION-SW-COMPONENT-TYPE"] = SemanticKind.SoftwareComponent,
        ["ECUC-MODULE-CONFIGURATION-VALUES"] = SemanticKind.EcucModule,
    };

    public static SemanticKind Classify(string elementType)
    {
        if (ExactNames.TryGetValue(elementType, out var kind))
            return kind;

        if (elementType.EndsWith("-SW-COMPONENT-TYPE", StringComparison.Ordinal))
            return SemanticKind.SoftwareComponent;

        if (elementType.EndsWith("-PORT-PROTOTYPE", StringComparison.Ordinal))
            return SemanticKind.Port;

        if (elementType.EndsWith("-INTERFACE", StringComparison.Ordinal))
            return SemanticKind.PortInterface;

        if (elementType.EndsWith("-PDU", StringComparison.Ordinal)
            || elementType.EndsWith("-SDU", StringComparison.Ordinal))
            return SemanticKind.Pdu;

        return SemanticKind.Unknown;
    }
}
