using Autarx.Core.Index;
using Autarx.Core.Models;

namespace Autarx.Cli;

internal static class InspectCommand
{
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var workspace, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var index = WorkspaceInput.Load(workspace);
        if (index is null)
            return 2;

        var summary = index.Summarize();

        var result = new InspectJson(
            workspace,
            summary.FileCount,
            summary.TotalFileBytes,
            summary.TotalElementCount,
            summary.IdentifiableCount,
            summary.PackageCount,
            summary.ReferenceCount,
            summary.UnresolvedReferenceCount,
            summary.DuplicatePathCount,
            summary.FileErrorCount,
            new AutosarMetadataJson(
                summary.Namespaces,
                summary.SchemaLocations,
                summary.AutosarRelease,
                summary.MixedSchema),
            summary.SemanticCounts,
            summary.UnknownNamedCount,
            [.. index.FileErrors.Select(e => new FileErrorJson(e.FilePath, e.Message, e.LineNumber))]);

        if (json)
        {
            JsonOutput.Write(result);
            return 0;
        }

        Console.WriteLine($"workspace         {workspace}");
        Console.WriteLine($"files             {summary.FileCount} ({summary.TotalFileBytes:N0} B)");
        Console.WriteLine($"elements          {summary.TotalElementCount:N0}");
        Console.WriteLine($"identifiables     {summary.IdentifiableCount:N0}");
        Console.WriteLine($"packages          {summary.PackageCount:N0}");
        Console.WriteLine($"references        {summary.ReferenceCount:N0} ({summary.UnresolvedReferenceCount:N0} unresolved)");
        Console.WriteLine($"duplicate paths   {summary.DuplicatePathCount:N0}");
        Console.WriteLine($"file errors       {summary.FileErrorCount:N0}");
        Console.WriteLine($"autosar release   {summary.AutosarRelease ?? "unknown"}{(summary.MixedSchema ? " (mixed schema flavours!)" : "")}");
        Console.WriteLine($"namespaces        {summary.Namespaces.Count} distinct");

        Console.WriteLine("semantic inventory:");
        foreach (var kind in InventoryOrder)
        {
            var count = kind == SemanticKind.Unknown
                ? summary.UnknownNamedCount
                : summary.SemanticCounts.GetValueOrDefault(kind);
            Console.WriteLine($"  {KindText(kind),-22} {count,6:N0}");
        }

        foreach (var fileError in index.FileErrors)
            Console.Error.WriteLine($"autarx: {fileError.FilePath}: {fileError.Message}");

        return 0;
    }

    internal static readonly SemanticKind[] InventoryOrder =
    [
        SemanticKind.System,
        SemanticKind.Ecu,
        SemanticKind.CommunicationCluster,
        SemanticKind.Frame,
        SemanticKind.Pdu,
        SemanticKind.Signal,
        SemanticKind.SoftwareComponent,
        SemanticKind.Port,
        SemanticKind.PortInterface,
        SemanticKind.EcucModule,
        SemanticKind.Unknown,
    ];

    internal static string KindText(SemanticKind kind) => kind switch
    {
        SemanticKind.System => "system",
        SemanticKind.Ecu => "ecu",
        SemanticKind.CommunicationCluster => "communication-cluster",
        SemanticKind.Frame => "frame",
        SemanticKind.Pdu => "pdu",
        SemanticKind.Signal => "signal",
        SemanticKind.SoftwareComponent => "software-component",
        SemanticKind.Port => "port",
        SemanticKind.PortInterface => "port-interface",
        SemanticKind.EcucModule => "ecuc-module",
        _ => "unknown",
    };

    private static bool TryParseArgs(string[] args, out string workspace, out bool json, out string? error)
    {
        workspace = "";
        json = false;
        error = null;

        foreach (var arg in args)
        {
            if (arg == "--json")
            {
                json = true;
            }
            else if (arg.StartsWith('-'))
            {
                error = $"autarx: unknown option '{arg}' — try 'autarx --help'";
                return false;
            }
            else if (workspace.Length == 0)
            {
                workspace = arg;
            }
            else
            {
                error = "autarx: exactly one workspace path expected";
                return false;
            }
        }

        if (workspace.Length == 0)
            error = "autarx: workspace path required — try 'autarx --help'";

        return error is null;
    }

    internal sealed record InspectJson(
        string Workspace,
        int FileCount,
        long TotalFileBytes,
        int TotalElementCount,
        int IdentifiableCount,
        int PackageCount,
        int ReferenceCount,
        int UnresolvedReferenceCount,
        int DuplicatePathCount,
        int FileErrorCount,
        AutosarMetadataJson Autosar,
        IReadOnlyDictionary<SemanticKind, int> SemanticCounts,
        int UnknownNamedCount,
        FileErrorJson[] FileErrors);

    internal sealed record AutosarMetadataJson(
        IReadOnlyList<string> Namespaces,
        IReadOnlyList<string> SchemaLocations,
        string? Release,
        bool MixedSchema);

    internal sealed record FileErrorJson(string File, string Message, int Line);
}
