using Autarx.Core.Models;

namespace Autarx.Core.Parsing;

/// <summary>
/// Projects ECUC module configuration values out of a generic ARXML tree.
/// Modules can sit directly in AR-PACKAGE/ELEMENTS or inside
/// ECUC-VALUE-COLLECTION / ECUC-VALUES wrappers, so they are located by a
/// whole-tree scan instead of a fixed path.
/// </summary>
public static class EcucReader
{
    public static List<EcucModule> ReadModules(ArxmlElement root)
    {
        var modules = new List<EcucModule>();

        foreach (var element in root.Descendants())
        {
            if (element.Name != "ECUC-MODULE-CONFIGURATION-VALUES")
                continue;

            var shortName = element.ChildText("SHORT-NAME");
            var path = shortName ?? "(unnamed)";
            var module = new EcucModule
            {
                ShortName = shortName ?? "(unnamed)",
                DefinitionRef = element.ChildText("DEFINITION-REF"),
                XmlPath = path,
            };

            foreach (var containers in element.Elements("CONTAINERS"))
                foreach (var container in containers.Elements("ECUC-CONTAINER-VALUE"))
                    ReadContainer(container, module.Containers, path);

            modules.Add(module);
        }

        return modules;
    }

    private static void ReadContainer(ArxmlElement element, List<EcucContainer> into, string parentPath)
    {
        var shortName = element.ChildText("SHORT-NAME");
        var path = $"{parentPath}/{shortName ?? "(unnamed)"}";
        var container = new EcucContainer
        {
            ShortName = shortName ?? "(unnamed)",
            DefinitionRef = element.ChildText("DEFINITION-REF"),
            XmlPath = path,
        };

        if (element.Element("PARAMETER-VALUES") is { } parameterValues)
        {
            foreach (var parameter in parameterValues.Children)
            {
                if (!parameter.Name.EndsWith("PARAM-VALUE", StringComparison.Ordinal))
                    continue;

                container.Parameters.Add(new EcucParameter
                {
                    DefinitionRef = parameter.ChildText("DEFINITION-REF"),
                    Value = parameter.ChildText("VALUE"),
                    ValueRef = parameter.ChildText("VALUE-REF") ?? parameter.ChildText("SYMBOLIC-NAME-REFERENCE"),
                    XmlPath = $"{path}/{LeafOf(parameter.ChildText("DEFINITION-REF"))}",
                });
            }
        }

        if (element.Element("REFERENCE-VALUES") is { } referenceValues)
        {
            foreach (var reference in referenceValues.Children)
            {
                if (reference.Name != "ECUC-REFERENCE-VALUE")
                    continue;

                container.References.Add(new EcucReference
                {
                    DefinitionRef = reference.ChildText("DEFINITION-REF"),
                    ValueRef = reference.ChildText("VALUE-REF") ?? reference.ChildText("SYMBOLIC-NAME-REFERENCE"),
                    XmlPath = $"{path}/{LeafOf(reference.ChildText("DEFINITION-REF"))}",
                });
            }
        }

        if (element.Element("SUB-CONTAINERS") is { } subContainers)
            foreach (var sub in subContainers.Elements("ECUC-CONTAINER-VALUE"))
                ReadContainer(sub, container.SubContainers, path);

        into.Add(container);
    }

    private static string LeafOf(string? definitionRef) =>
        string.IsNullOrEmpty(definitionRef) ? "(no-definition-ref)" : definitionRef.Split('/')[^1];
}
