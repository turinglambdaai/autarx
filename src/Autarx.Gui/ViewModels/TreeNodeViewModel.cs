using System.Collections.ObjectModel;
using Autarx.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Autarx.Gui.ViewModels;

public partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string? Detail { get; init; }

    /// <summary>Absolute short-name path when this node is a workspace object
    /// (leaf); null for structural nodes (workspace root, package groups).</summary>
    public string? ObjectPath { get; init; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public static TreeNodeViewModel FromObject(SemanticObject o) => new()
    {
        Kind = o.SemanticKind.ToString(),
        Name = o.ShortName,
        Detail = $"{o.ElementType} — {Path.GetFileName(o.SourceFile)}",
        ObjectPath = o.AbsolutePath,
    };

    public static TreeNodeViewModel FromModule(EcucModule module)
    {
        var node = new TreeNodeViewModel
        {
            Kind = "Module",
            Name = module.ShortName,
            Detail = module.DefinitionRef,
            IsExpanded = true,
        };

        foreach (var container in module.Containers)
            node.Children.Add(FromContainer(container));

        return node;
    }

    private static TreeNodeViewModel FromContainer(EcucContainer container)
    {
        var node = new TreeNodeViewModel
        {
            Kind = "Container",
            Name = container.ShortName,
            Detail = container.DefinitionRef,
        };

        foreach (var parameter in container.Parameters)
            node.Children.Add(new TreeNodeViewModel
            {
                Kind = "Parameter",
                Name = parameter.LeafName ?? "(no-definition-ref)",
                Detail = parameter.Value is not null
                    ? $"{parameter.Value}   ({parameter.DefinitionRef})"
                    : $"{parameter.ValueRef}   ({parameter.DefinitionRef})",
            });

        foreach (var reference in container.References)
            node.Children.Add(new TreeNodeViewModel
            {
                Kind = "Reference",
                Name = reference.LeafName ?? "(no-definition-ref)",
                Detail = reference.ValueRef,
            });

        foreach (var sub in container.SubContainers)
            node.Children.Add(FromContainer(sub));

        return node;
    }
}
