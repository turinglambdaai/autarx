using Autarx.Core.Index;
using Autarx.Core.Models;

namespace Autarx.Cli;

internal static class EcusCommand
{
    /// <summary>`autarx ecus <workspace>` — list ECU instances.</summary>
    public static int RunList(string[] args)
    {
        if (!TryParseWorkspaceArgs(args, out var workspace, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var index = WorkspaceInput.Load(workspace);
        if (index is null)
            return 2;

        var ecus = index.Objects
            .Where(o => o.SemanticKind == SemanticKind.Ecu)
            .OrderBy(o => o.AbsolutePath, StringComparer.Ordinal)
            .ToList();

        if (ecus.Count == 0)
        {
            Console.Error.WriteLine("autarx: no ECU instances found in workspace");
            return 1;
        }

        if (json)
        {
            JsonOutput.Write(ecus.Select(FindCommand.ToDto).ToList());
            return 0;
        }

        foreach (var ecu in ecus)
            Console.WriteLine($"{ecu.ShortName,-20} {ecu.AbsolutePath,-52} {Path.GetFileName(ecu.SourceFile)}");
        return 0;
    }

    /// <summary>`autarx ecu <workspace> <ecu-name>` — one ECU and its direct
    /// relations. Relations are derived from direct reference-graph edges only;
    /// every entry carries an explicit relationshipConfidence ("direct").
    /// Anything beyond one hop is not claimed.</summary>
    public static int RunDetail(string[] args)
    {
        if (!TryParseDetailArgs(args, out var workspace, out var name, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var index = WorkspaceInput.Load(workspace);
        if (index is null)
            return 2;

        var ecu = WorkspaceInput.ResolveOrReport(index, name);
        if (ecu is null)
            return index.Resolve(name).Status == ResolveStatus.Ambiguous ? 2 : 1;

        if (ecu.SemanticKind != SemanticKind.Ecu)
        {
            Console.Error.WriteLine($"autarx: '{name}' is a {InspectCommand.KindText(ecu.SemanticKind)}, not an ECU instance");
            return 1;
        }

        var outgoing = index.OutgoingOf(ecu.AbsolutePath);
        var incoming = index.IncomingOf(ecu.AbsolutePath);

        var related = outgoing.Select(r => (r, r.TargetPath, "outgoing"))
            .Concat(incoming.Select(r => (r, r.SourcePath, "incoming")))
            .Select(entry =>
            {
                var (r, otherPath, direction) = entry;
                var other = index.Resolve(otherPath).Object;
                return new RelatedJson(
                    otherPath,
                    other?.SemanticKind,
                    direction,
                    r.Kind,
                    "direct");
            })
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Direction, StringComparer.Ordinal)
            .ToList();

        if (json)
        {
            JsonOutput.Write(new EcuDetailJson(
                FindCommand.ToDto(ecu),
                [.. outgoing.Select(RefsCommand.ToDto)],
                [.. incoming.Select(RefsCommand.ToDto)],
                related));
            return 0;
        }

        Console.WriteLine($"{ecu.ShortName} ({InspectCommand.KindText(ecu.SemanticKind)})");
        Console.WriteLine($"  path   {ecu.AbsolutePath}");
        Console.WriteLine($"  file   {Path.GetFileName(ecu.SourceFile)}");

        Console.WriteLine($"  outgoing references ({outgoing.Count}):");
        foreach (var r in outgoing)
            Console.WriteLine($"    -> {r.Kind,-28} {r.TargetPath}{RefsCommand.UnresolvedSuffix(r)}");

        Console.WriteLine($"  incoming references ({incoming.Count}):");
        foreach (var r in incoming)
            Console.WriteLine($"    <- {r.Kind,-28} {r.SourceElementPath}");

        Console.WriteLine($"  related objects ({related.Count}):");
        foreach (var r in related)
            Console.WriteLine(
                $"    {r.RelationshipConfidence,-8} {r.Direction,-8} via {r.ViaKind,-28} {r.Path}");
        return 0;
    }

    internal sealed record RelatedJson(
        string Path,
        SemanticKind? SemanticKind,
        string Direction,
        string ViaKind,
        string RelationshipConfidence);

    internal sealed record EcuDetailJson(
        FindCommand.ObjectJson Ecu,
        RefsCommand.ReferenceJson[] Outgoing,
        RefsCommand.ReferenceJson[] Incoming,
        IReadOnlyList<RelatedJson> Related);

    private static bool TryParseWorkspaceArgs(string[] args, out string workspace, out bool json, out string? error)
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
                error = "autarx: expected <workspace>";
                return false;
            }
        }

        if (workspace.Length == 0)
            error = "autarx: expected <workspace> — try 'autarx --help'";

        return error is null;
    }

    private static bool TryParseDetailArgs(string[] args, out string workspace, out string name, out bool json, out string? error)
    {
        workspace = "";
        name = "";
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
            else if (name.Length == 0)
            {
                name = arg;
            }
            else
            {
                error = "autarx: expected <workspace> <ecu-name>";
                return false;
            }
        }

        if (workspace.Length == 0 || name.Length == 0)
            error = "autarx: expected <workspace> <ecu-name> — try 'autarx --help'";

        return error is null;
    }
}
