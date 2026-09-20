using Autarx.Core.Models;

namespace Autarx.Core.Validation;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string Path);

/// <summary>
/// Structural validation over projected ECUC models. Rule codes are part of
/// the CLI contract — never renumber, only append (see ARCHITECTURE.md).
/// </summary>
public static class ValidationEngine
{
    public static List<Diagnostic> Validate(IReadOnlyList<EcucModule> modules)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var module in modules)
        {
            if (module.ShortName is "(unnamed)")
                diagnostics.Add(new Diagnostic("ARX006", DiagnosticSeverity.Error,
                    "module configuration is missing SHORT-NAME", module.XmlPath));

            if (string.IsNullOrEmpty(module.DefinitionRef))
                diagnostics.Add(new Diagnostic("ARX001", DiagnosticSeverity.Error,
                    "module configuration is missing DEFINITION-REF", module.XmlPath));

            ValidateContainers(module.Containers, module.DefinitionRef, diagnostics);
        }

        return diagnostics;
    }

    private static void ValidateContainers(
        IReadOnlyList<EcucContainer> containers,
        string? moduleDefinitionRef,
        List<Diagnostic> diagnostics)
    {
        foreach (var group in containers.GroupBy(c => c.ShortName).Where(g => g.Count() > 1))
            foreach (var container in group)
                diagnostics.Add(new Diagnostic("ARX003", DiagnosticSeverity.Error,
                    $"duplicate sibling container SHORT-NAME \"{container.ShortName}\"", container.XmlPath));

        foreach (var container in containers)
        {
            if (container.ShortName is "(unnamed)")
                diagnostics.Add(new Diagnostic("ARX006", DiagnosticSeverity.Error,
                    "container is missing SHORT-NAME", container.XmlPath));

            if (string.IsNullOrEmpty(container.DefinitionRef))
                diagnostics.Add(new Diagnostic("ARX002", DiagnosticSeverity.Error,
                    "container is missing DEFINITION-REF", container.XmlPath));

            foreach (var parameter in container.Parameters)
            {
                if (parameter.Value is null && parameter.ValueRef is null)
                    diagnostics.Add(new Diagnostic("ARX004", DiagnosticSeverity.Error,
                        "parameter has neither VALUE nor VALUE-REF",
                        parameter.XmlPath));

                if (moduleDefinitionRef is not null
                    && !string.IsNullOrEmpty(parameter.DefinitionRef)
                    && !parameter.DefinitionRef.StartsWith(moduleDefinitionRef + "/", StringComparison.Ordinal))
                    diagnostics.Add(new Diagnostic("ARX005", DiagnosticSeverity.Warning,
                        $"parameter DEFINITION-REF is outside module definition \"{moduleDefinitionRef}\"",
                        parameter.XmlPath));
            }

            ValidateContainers(container.SubContainers, moduleDefinitionRef, diagnostics);
        }
    }
}
