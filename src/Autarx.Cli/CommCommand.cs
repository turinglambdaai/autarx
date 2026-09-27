using Autarx.Core.Communication;
using Autarx.Core.Index;
using Autarx.Core.Models;

namespace Autarx.Cli;

internal static class CommCommand
{
    /// <summary>`autarx comm <workspace>` — communication projection:
    /// clusters → frames → PDUs → signals, orphans, ECUC comm modules.
    /// Exit 0 = projection shown, 1 = no communication content,
    /// 2 = usage/parse errors.</summary>
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var workspace, out var clusterName, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var index = WorkspaceInput.Load(workspace);
        if (index is null)
            return 2;

        var model = CommunicationProjection.Build(index);

        if (clusterName.Length > 0)
        {
            var resolution = index.Resolve(clusterName);
            if (resolution.Status == ResolveStatus.Ambiguous)
            {
                Console.Error.WriteLine($"autarx: '{clusterName}' is ambiguous, {resolution.Candidates.Count} objects match:");
                foreach (var candidate in resolution.Candidates)
                    Console.Error.WriteLine($"  {candidate.AbsolutePath}");
                return 2;
            }

            var resolved = resolution.Status == ResolveStatus.Found ? resolution.Object : null;
            if (resolved is null || resolved.SemanticKind != SemanticKind.CommunicationCluster)
            {
                Console.Error.WriteLine($"autarx: no communication cluster matches '{clusterName}'");
                return 1;
            }

            var projection = model.Clusters.FirstOrDefault(c => c.ClusterPath == resolved.AbsolutePath)
                ?? new ClusterProjection(resolved.AbsolutePath, [], []);
            model = model with { Clusters = [projection] };
        }
        else if (model.IsEmpty
            && model.OrphanFrames.Count == 0
            && model.OrphanPdus.Count == 0
            && model.OrphanSignals.Count == 0)
        {
            Console.Error.WriteLine("autarx: no communication content found in workspace");
            return 1;
        }

        if (json)
        {
            JsonOutput.Write(ToDto(model));
            return 0;
        }

        foreach (var cluster in model.Clusters)
            RenderCluster(model, cluster);

        ReportOrphans("frames", model.OrphanFrames);
        ReportOrphans("pdus", model.OrphanPdus);
        ReportOrphans("signals", model.OrphanSignals);

        if (model.EcucCommModules.Count > 0)
            Console.WriteLine($"\necuc comm modules: {string.Join(", ", model.EcucCommModules)}");

        return 0;
    }

    private static void RenderCluster(CommunicationModel model, ClusterProjection cluster)
    {
        Console.WriteLine($"{ShortName(cluster.ClusterPath)} ({cluster.ClusterPath})");
        Console.WriteLine($"  ecus: {(cluster.ConnectedEcus.Count > 0 ? string.Join(", ", cluster.ConnectedEcus.Select(ShortName)) : "(none)")}");

        var framesByPath = model.Frames.ToDictionary(f => f.FramePath, StringComparer.Ordinal);
        var pdusByPath = model.Pdus.ToDictionary(p => p.PduPath, StringComparer.Ordinal);

        if (cluster.FramePaths.Count == 0)
            Console.WriteLine("  frames: (none — no frame triggerings under this cluster)");
        foreach (var framePath in cluster.FramePaths)
        {
            Console.WriteLine($"  frame {ShortName(framePath)}");
            var frame = framesByPath.GetValueOrDefault(framePath);
            if (frame is null || frame.PduPaths.Count == 0)
            {
                Console.WriteLine("    pdus: (none — no PDU-to-frame mapping)");
                continue;
            }
            foreach (var pduPath in frame.PduPaths)
            {
                var pdu = pdusByPath.GetValueOrDefault(pduPath);
                var signals = pdu is { SignalPaths.Count: > 0 }
                    ? string.Join(", ", pdu.SignalPaths.Select(ShortName))
                    : "(none)";
                Console.WriteLine($"    pdu {ShortName(pduPath)}  signals: {signals}");
            }
        }
    }

    private static void ReportOrphans(string label, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        Console.WriteLine($"\norphan {label} ({paths.Count}) — not attached to any parent:");
        foreach (var path in paths)
            Console.WriteLine($"  {path}");
    }

    private static string ShortName(string path) => path.Split('/').Last();

    internal static CommJson ToDto(CommunicationModel model) => new(
        model.Clusters.Select(c => new ClusterJson(c.ClusterPath, c.ConnectedEcus, c.FramePaths)).ToList(),
        model.Frames.Select(f => new FrameJson(f.FramePath, f.PduPaths)).ToList(),
        model.Pdus.Select(p => new PduJson(p.PduPath, p.SignalPaths)).ToList(),
        model.OrphanFrames,
        model.OrphanPdus,
        model.OrphanSignals,
        model.EcucCommModules);

    internal sealed record CommJson(
        IReadOnlyList<ClusterJson> Clusters,
        IReadOnlyList<FrameJson> Frames,
        IReadOnlyList<PduJson> Pdus,
        IReadOnlyList<string> OrphanFrames,
        IReadOnlyList<string> OrphanPdus,
        IReadOnlyList<string> OrphanSignals,
        IReadOnlyList<string> EcucCommModules);

    internal sealed record ClusterJson(string ClusterPath, IReadOnlyList<string> ConnectedEcus, IReadOnlyList<string> FramePaths);

    internal sealed record FrameJson(string FramePath, IReadOnlyList<string> PduPaths);

    internal sealed record PduJson(string PduPath, IReadOnlyList<string> SignalPaths);

    private static bool TryParseArgs(
        string[] args,
        out string workspace,
        out string cluster,
        out bool json,
        out string? error)
    {
        workspace = "";
        cluster = "";
        json = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                json = true;
            }
            else if (arg == "--cluster")
            {
                if (i + 1 >= args.Length)
                {
                    error = "autarx: --cluster requires a value";
                    return false;
                }
                cluster = args[++i];
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
}
