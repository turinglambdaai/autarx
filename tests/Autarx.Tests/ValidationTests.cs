using Autarx.Core.Models;
using Autarx.Core.Parsing;
using Autarx.Core.Validation;
using Xunit;

namespace Autarx.Tests;

public class ValidationTests
{
    private static readonly List<Diagnostic> FixtureDiagnostics = ValidationEngine.Validate(
        EcucReader.ReadModules(ArxmlParser.ParseFile(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal.arxml"))));

    [Fact]
    public void Valid_fixture_has_no_diagnostics() =>
        Assert.Empty(FixtureDiagnostics);

    [Fact]
    public void Missing_module_definition_ref_is_flagged()
    {
        var diagnostics = ValidationEngine.Validate([Module("Mcu", null, [])]);

        var d = Assert.Single(diagnostics);
        Assert.Equal("ARX001", d.Code);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void Missing_container_definition_ref_is_flagged()
    {
        var diagnostics = ValidationEngine.Validate(
            [Module("Mcu", "/AURIX2G/Mcu", [Container("C", null, [])])]);

        var d = Assert.Single(diagnostics);
        Assert.Equal("ARX002", d.Code);
        Assert.Equal("parent/C", d.Path);
    }

    [Fact]
    public void Duplicate_sibling_container_names_are_flagged()
    {
        var diagnostics = ValidationEngine.Validate(
        [
            Module("Mcu", "/AURIX2G/Mcu",
            [
                Container("Same", "/AURIX2G/Mcu/Same", []),
                Container("Same", "/AURIX2G/Mcu/Same", []),
            ]),
        ]);

        Assert.Equal(2, diagnostics.Count(d => d.Code == "ARX003"));
    }

    [Fact]
    public void Parameter_without_value_or_value_ref_is_flagged()
    {
        var diagnostics = ValidationEngine.Validate(
        [
            Module("Mcu", "/AURIX2G/Mcu",
            [
                Container("C", "/AURIX2G/Mcu/C", [Parameter("/AURIX2G/Mcu/C/P", null, null)]),
            ]),
        ]);

        Assert.Contains(diagnostics, d => d.Code == "ARX004");
    }

    [Fact]
    public void Foreign_definition_ref_warns()
    {
        var diagnostics = ValidationEngine.Validate(
        [
            Module("Mcu", "/AURIX2G/Mcu",
            [
                Container("C", "/AURIX2G/Mcu/C", [Parameter("/Other/Module/P", "1", null)]),
            ]),
        ]);

        var d = Assert.Single(diagnostics);
        Assert.Equal("ARX005", d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
    }

    private static EcucModule Module(string name, string? definitionRef, EcucContainer[] containers)
    {
        var module = new EcucModule { ShortName = name, DefinitionRef = definitionRef, XmlPath = name };
        foreach (var container in containers)
            module.Containers.Add(container);
        return module;
    }

    private static EcucContainer Container(string name, string? definitionRef, EcucParameter[] parameters)
    {
        var container = new EcucContainer { ShortName = name, DefinitionRef = definitionRef, XmlPath = $"parent/{name}" };
        foreach (var parameter in parameters)
            container.Parameters.Add(parameter);
        return container;
    }

    private static EcucParameter Parameter(string? definitionRef, string? value, string? valueRef) => new()
    {
        DefinitionRef = definitionRef,
        Value = value,
        ValueRef = valueRef,
        XmlPath = "parent/c/" + (definitionRef?.Split('/').LastOrDefault() ?? "?"),
    };
}
