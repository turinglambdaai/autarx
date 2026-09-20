namespace Autarx.Core.Models;

/// <summary>One ECUC-MODULE-CONFIGURATION-VALUES projected from an ARXML file.</summary>
public sealed class EcucModule
{
    public required string ShortName { get; init; }

    /// <summary>DEFINITION-REF of the module definition, e.g. /Vendor/Mcu.</summary>
    public string? DefinitionRef { get; init; }

    /// <summary>SHORT-NAME path used in diagnostics, e.g. Mcu/McuGeneralConfiguration.</summary>
    public required string XmlPath { get; init; }

    public List<EcucContainer> Containers { get; } = [];

    public int TotalContainers => Containers.Sum(c => 1 + c.TotalSubContainers);

    public int TotalParameters => Containers.Sum(c => c.TotalParameters);
}

public sealed class EcucContainer
{
    public required string ShortName { get; init; }

    public string? DefinitionRef { get; init; }

    public required string XmlPath { get; init; }

    public List<EcucContainer> SubContainers { get; } = [];

    public List<EcucParameter> Parameters { get; } = [];

    public List<EcucReference> References { get; } = [];

    public int TotalSubContainers => SubContainers.Sum(c => 1 + c.TotalSubContainers);

    public int TotalParameters => Parameters.Count + SubContainers.Sum(c => c.TotalParameters);
}

/// <summary>An ECUC-NUMERICAL/TEXTUAL-PARAM-VALUE. Carries either a literal
/// VALUE or a symbolic reference, never both.</summary>
public sealed class EcucParameter
{
    public string? DefinitionRef { get; init; }

    public string? Value { get; init; }

    /// <summary>VALUE-REF / SYMBOLIC-NAME-REFERENCE when the parameter points elsewhere.</summary>
    public string? ValueRef { get; init; }

    public required string XmlPath { get; init; }

    public string? LeafName => DefinitionRef?.Split('/')[^1];
}

public sealed class EcucReference
{
    public string? DefinitionRef { get; init; }

    public string? ValueRef { get; init; }

    public required string XmlPath { get; init; }

    public string? LeafName => DefinitionRef?.Split('/')[^1];
}
