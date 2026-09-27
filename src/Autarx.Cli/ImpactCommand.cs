using Autarx.Core.Diff;
using Autarx.Core.Impact;
using Autarx.Core.Index;
using Autarx.Core.Models;

namespace Autarx.Cli;

internal static class ImpactCommand
{
    /// <summary>`autarx impact <before> <after> --ecu <name>` — what changed
    /// for one ECU between two deliveries. Exit 0 = nothing relevant,
    /// 1 = relevant changes, 2 = usage/parse errors or unresolvable ECU.</summary>
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var before, out var after, out var ecuName, out var detail, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var beforeIndex = WorkspaceInput.Load(before);
        if (beforeIndex is null)
            return 2;
        var afterIndex = WorkspaceInput.Load(after);
        if (afterIndex is null)
            return 2;

        // The ECU may exist in the after delivery (normal case), only in the
        // before delivery (removed ECU), or nowhere (error).
        SemanticObject? ecu = null;
        WorkspaceIndex? homeIndex = null;
        foreach (var index in new[] { afterIndex, beforeIndex })
        {
            var resolution = index.Resolve(ecuName);
            switch (resolution.Status)
            {
                case ResolveStatus.Found:
                    ecu = resolution.Object;
                    homeIndex = index;
                    break;
                case ResolveStatus.Ambiguous:
                    Console.Error.WriteLine($"autarx: '{ecuName}' is ambiguous, {resolution.Candidates.Count} objects match:");
                    foreach (var candidate in resolution.Candidates)
                        Console.Error.WriteLine($"  {candidate.AbsolutePath}");
                    return 2;
            }
            if (ecu is not null)
                break;
        }

        if (ecu is null || homeIndex is null)
        {
            Console.Error.WriteLine($"autarx: no workspace object matches '{ecuName}' in either delivery");
            return 2;
        }

        if (ecu.SemanticKind != SemanticKind.Ecu)
        {
            Console.Error.WriteLine($"autarx: '{ecuName}' is a {InspectCommand.KindText(ecu.SemanticKind)}, not an ECU instance");
            return 2;
        }

        var ecuRemoved = !ReferenceEquals(homeIndex, afterIndex);

        WorkspaceDiff diff;
        try
        {
            diff = SemanticDiff.Compare(beforeIndex, afterIndex, detail);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"autarx: impact analysis failed: {ex.Message}");
            return 2;
        }

        var report = ImpactAnalysis.Analyze(beforeIndex, afterIndex, ecu.AbsolutePath, ecuRemoved, diff, detail);
        var hasRelevantChanges = report.RelevantCount > 0 || report.Findings.Count > 0;

        if (json)
        {
            JsonOutput.Write(ToDto(report));
            return hasRelevantChanges ? 1 : 0;
        }

        var status = ecuRemoved ? "ECU not present in after delivery" : "ECU present in after delivery";
        Console.WriteLine($"impact for {report.Ecu} ({report.EcuPath}) — {status}");
        Console.WriteLine($"closure: {report.ClosureSize} objects in scope, relevant changes: {report.RelevantCount}, unrelated: {report.UnrelatedCount}, breaking: {report.BreakingCount}");

        if (report.Findings.Count > 0)
        {
            Console.WriteLine("\nfindings:");
            foreach (var finding in report.Findings)
                Console.WriteLine($"  [{ImpactCommand.SeverityText(finding.Severity)}] {finding.Rule} {finding.ObjectPath}");
        }

        if (report.Communication.Changes.Count > 0)
        {
            var c = report.Communication;
            Console.WriteLine($"\ncommunication impact: {c.ClusterCount} cluster(s), {c.FrameCount} frame(s), {c.PduCount} PDU(s), {c.SignalCount} signal(s)");
        }

        return hasRelevantChanges ? 1 : 0;
    }

    private static string SeverityText(ImpactSeverity severity) =>
        severity == ImpactSeverity.Breaking ? "breaking" : "info";

    internal static ImpactJson ToDto(ImpactReport report) => new(
        report.Ecu,
        report.EcuPath,
        report.EcuRemoved,
        report.ClosureSize,
        report.RelevantCount,
        report.UnrelatedCount,
        report.BreakingCount,
        report.Findings.Select(f => new FindingJson(f.Rule, f.Severity, f.ObjectPath, f.Detail)).ToList(),
        report.RelevantChanges.Select(DiffCommand.ToObjectChangeDto).ToList(),
        new CommunicationJson(
            report.Communication.Changes.Select(DiffCommand.ToObjectChangeDto).ToList(),
            report.Communication.SignalCount,
            report.Communication.PduCount,
            report.Communication.FrameCount,
            report.Communication.ClusterCount));

    internal sealed record ImpactJson(
        string Ecu,
        string EcuPath,
        bool EcuRemoved,
        int ClosureSize,
        int RelevantCount,
        int UnrelatedCount,
        int BreakingCount,
        IReadOnlyList<FindingJson> Findings,
        IReadOnlyList<DiffCommand.ObjectChangeJson> RelevantChanges,
        CommunicationJson Communication);

    internal sealed record FindingJson(
        string Rule,
        ImpactSeverity Severity,
        string ObjectPath,
        string Detail);

    internal sealed record CommunicationJson(
        IReadOnlyList<DiffCommand.ObjectChangeJson> Changes,
        int SignalCount,
        int PduCount,
        int FrameCount,
        int ClusterCount);

    private static bool TryParseArgs(
        string[] args,
        out string before,
        out string after,
        out string ecu,
        out bool detail,
        out bool json,
        out string? error)
    {
        before = "";
        after = "";
        ecu = "";
        detail = false;
        json = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                json = true;
            }
            else if (arg == "--detail")
            {
                detail = true;
            }
            else if (arg == "--ecu")
            {
                if (i + 1 >= args.Length)
                {
                    error = "autarx: --ecu requires a value";
                    return false;
                }
                ecu = args[++i];
            }
            else if (arg.StartsWith('-'))
            {
                error = $"autarx: unknown option '{arg}' — try 'autarx --help'";
                return false;
            }
            else if (before.Length == 0)
            {
                before = arg;
            }
            else if (after.Length == 0)
            {
                after = arg;
            }
            else
            {
                error = "autarx: expected <before> <after> --ecu <name>";
                return false;
            }
        }

        if (before.Length == 0 || after.Length == 0 || ecu.Length == 0)
            error = "autarx: expected <before> <after> --ecu <name> — try 'autarx --help'";

        return error is null;
    }
}
