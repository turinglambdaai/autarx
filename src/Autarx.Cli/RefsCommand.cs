using Autarx.Core.Index;
using Autarx.Core.Models;

namespace Autarx.Cli;

internal static class RefsCommand
{
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var nameOrPath, out var workspace, out var incoming, out var outgoing, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var index = WorkspaceInput.Load(workspace);
        if (index is null)
            return 2;

        var target = WorkspaceInput.ResolveOrReport(index, nameOrPath);
        if (target is null)
            return 1;

        // Default: show both directions.
        var showIncoming = incoming || !outgoing;
        var showOutgoing = outgoing || !incoming;

        var outgoingRefs = showOutgoing ? index.OutgoingOf(target.AbsolutePath) : [];
        var incomingRefs = showIncoming ? index.IncomingOf(target.AbsolutePath) : [];

        if (json)
        {
            JsonOutput.Write(new RefsJson(
                FindCommand.ToDto(target),
                [.. outgoingRefs.Select(ToDto)],
                [.. incomingRefs.Select(ToDto)]));
            return 0;
        }

        Console.WriteLine($"object    {target.AbsolutePath} ({InspectCommand.KindText(target.SemanticKind)})");

        Console.WriteLine($"outgoing ({outgoingRefs.Count}):");
        foreach (var reference in outgoingRefs)
            Console.WriteLine(
                $"  -> {reference.Kind,-28} {reference.TargetPath,-52} " +
                $"via {RelativePath(reference.SourceElementPath, target.AbsolutePath)}{UnresolvedSuffix(reference)}");

        Console.WriteLine($"incoming ({incomingRefs.Count}):");
        foreach (var reference in incomingRefs)
            Console.WriteLine(
                ($"  <- {reference.Kind,-28} {reference.SourceElementPath,-52} " +
                 $"{UnresolvedSuffix(reference)}").TrimEnd());
        return 0;
    }

    internal static ReferenceJson ToDto(AutosarReference r) => new(
        r.Kind,
        r.SourcePath,
        r.SourceElementPath,
        r.TargetPath,
        r.SourceFile,
        r.IsResolved);

    internal sealed record ReferenceJson(
        string Kind,
        string SourcePath,
        string SourceElementPath,
        string TargetPath,
        string SourceFile,
        bool Resolved);

    internal sealed record RefsJson(
        FindCommand.ObjectJson Object,
        ReferenceJson[] Outgoing,
        ReferenceJson[] Incoming);

    internal static string UnresolvedSuffix(AutosarReference reference) =>
        reference.IsResolved ? "" : "  (unresolved)";

    /// <summary>The nested chain below the object, e.g. "/VehicleSpeedMapping".</summary>
    internal static string RelativePath(string sourceElementPath, string objectPath) =>
        sourceElementPath.Length > objectPath.Length
            ? sourceElementPath[objectPath.Length..]
            : "";

    private static bool TryParseArgs(
        string[] args,
        out string nameOrPath,
        out string workspace,
        out bool incoming,
        out bool outgoing,
        out bool json,
        out string? error)
    {
        nameOrPath = "";
        workspace = "";
        incoming = false;
        outgoing = false;
        json = false;
        error = null;

        foreach (var arg in args)
        {
            if (arg == "--json")
            {
                json = true;
            }
            else if (arg == "--incoming")
            {
                incoming = true;
            }
            else if (arg == "--outgoing")
            {
                outgoing = true;
            }
            else if (arg.StartsWith('-'))
            {
                error = $"autarx: unknown option '{arg}' — try 'autarx --help'";
                return false;
            }
            else if (nameOrPath.Length == 0)
            {
                nameOrPath = arg;
            }
            else if (workspace.Length == 0)
            {
                workspace = arg;
            }
            else
            {
                error = "autarx: expected <name-or-path> <workspace>";
                return false;
            }
        }

        if (nameOrPath.Length == 0 || workspace.Length == 0)
            error = "autarx: expected <name-or-path> <workspace> — try 'autarx --help'";

        return error is null;
    }
}
