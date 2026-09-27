using Autarx.Core.Index;

namespace Autarx.Cli;

internal static class TraceCommand
{
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var nameOrPath, out var workspace, out var depth, out var direction, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var index = WorkspaceInput.Load(workspace);
        if (index is null)
            return 2;

        var root = WorkspaceInput.ResolveOrReport(index, nameOrPath);
        if (root is null)
            return 1;

        var trace = index.Trace(root.AbsolutePath, direction, depth);

        if (json)
        {
            JsonOutput.Write(new TraceJson(
                trace.Root,
                trace.Direction,
                trace.MaxDepth,
                [.. trace.Nodes.Select(n => new TraceNodeJson(n.Path, n.Depth))],
                [.. trace.Edges.Select(e => new TraceEdgeJson(e.SourcePath, e.Kind, e.TargetPath, e.Depth))]));
            return 0;
        }

        Console.WriteLine($"trace from {trace.Root} (depth {trace.MaxDepth}, {trace.Direction.ToString().ToLowerInvariant()}):");
        foreach (var edge in trace.Edges)
            Console.WriteLine($"  [{edge.Depth}] {edge.Kind,-28} {edge.SourcePath} -> {edge.TargetPath}");
        Console.WriteLine($"{trace.Nodes.Count} object(s) reachable, {trace.Edges.Count} edge(s)");
        return 0;
    }

    internal sealed record TraceJson(
        string Root,
        TraceDirection Direction,
        int MaxDepth,
        TraceNodeJson[] Nodes,
        TraceEdgeJson[] Edges);

    internal sealed record TraceNodeJson(string Path, int Depth);

    internal sealed record TraceEdgeJson(string SourcePath, string Kind, string TargetPath, int Depth);

    private static bool TryParseArgs(
        string[] args,
        out string nameOrPath,
        out string workspace,
        out int depth,
        out TraceDirection direction,
        out bool json,
        out string? error)
    {
        nameOrPath = "";
        workspace = "";
        depth = 2;
        direction = TraceDirection.Both;
        json = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == "--json")
            {
                json = true;
            }
            else if (arg == "--incoming")
            {
                direction = TraceDirection.Incoming;
            }
            else if (arg == "--outgoing")
            {
                direction = TraceDirection.Outgoing;
            }
            else if (arg == "--both")
            {
                direction = TraceDirection.Both;
            }
            else if (arg == "--depth")
            {
                if (i + 1 >= args.Length
                    || !int.TryParse(args[++i], out depth)
                    || depth < 0)
                {
                    error = "autarx: --depth requires a non-negative number, e.g. --depth 3";
                    return false;
                }
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
