using System.Text.Json;
using Autarx.Core.Diff;
using Autarx.Core.Patch;

namespace Autarx.Cli;

internal static class PatchCommand
{
    /// <summary>`autarx patch plan|apply|undo` — reviewed editing. Plan is
    /// read-only; apply writes only after a valid plan with its mandatory
    /// semantic diff; undo restores the most recent patch from backups.</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0)
            return Usage();

        return args[0] switch
        {
            "plan" => RunPlan(args[1..]),
            "apply" => RunApply(args[1..]),
            "undo" => RunUndo(args[1..]),
            "-h" or "--help" or "help" => Usage(0),
            _ => UnknownSubcommand(args[0]),
        };
    }

    private static int RunPlan(string[] args)
    {
        if (!TryParseOpArgs(args, out var workspace, out var opsFile, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var (operations, parseErrors) = ReadOps(opsFile);
        if (parseErrors.Count > 0 || operations.Count == 0)
        {
            foreach (var parseError in parseErrors)
                Console.Error.WriteLine($"autarx: {parseError}");
            return 2;
        }

        var plan = PatchEngine.Plan(workspace, operations, Provenance());
        if (plan.TempWorkspace is not null && !json)
        {
            RenderPlan(plan);
            PatchEngine.TryDelete(plan.TempWorkspace);
            return plan.Valid ? 0 : 1;
        }

        if (json)
        {
            JsonOutput.Write(ToDto(plan));
            if (plan.TempWorkspace is not null)
                PatchEngine.TryDelete(plan.TempWorkspace);
            return plan.Valid ? 0 : 1;
        }

        return plan.Valid ? 0 : 1;
    }

    private static int RunApply(string[] args)
    {
        if (!TryParseOpArgs(args, out var workspace, out var opsFile, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var (operations, parseErrors) = ReadOps(opsFile);
        if (parseErrors.Count > 0 || operations.Count == 0)
        {
            foreach (var parseError in parseErrors)
                Console.Error.WriteLine($"autarx: {parseError}");
            return 2;
        }

        PatchPlan plan;
        try
        {
            plan = PatchEngine.Plan(workspace, operations, Provenance());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"autarx: plan failed: {ex.Message}");
            return 2;
        }

        if (!plan.Valid || plan.Diff is null || plan.TempWorkspace is null)
        {
            if (json)
                JsonOutput.Write(ToDto(plan));
            else
                foreach (var result in plan.Operations.Where(r => !r.Ok))
                    Console.Error.WriteLine($"autarx: operation {result.Index} ({result.Op} {result.Target}) failed: {result.Error}");
            Console.Error.WriteLine("autarx: nothing was written");
            if (plan.TempWorkspace is not null)
                PatchEngine.TryDelete(plan.TempWorkspace);
            return 1;
        }

        try
        {
            var manifest = PatchEngine.Apply(workspace, plan, plan.TempWorkspace);

            if (json)
            {
                JsonOutput.Write(new ApplyJson(
                    Applied: true,
                    manifest.Timestamp,
                    manifest.Files.Select(f => f.RelativePath).ToList(),
                    manifest.OperationCount,
                    DiffCommand.ToDto(plan.Diff)));
            }
            else
            {
                Console.WriteLine($"applied {manifest.OperationCount} operation(s) to {manifest.Files.Count} file(s); backups kept as <file>.autarx-bak:");
                foreach (var file in manifest.Files)
                    Console.WriteLine($"  {file.RelativePath}");
                RenderDiff(plan.Diff);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"autarx: apply failed: {ex.Message}");
            PatchEngine.TryDelete(plan.TempWorkspace);
            return 2;
        }
    }

    private static int RunUndo(string[] args)
    {
        var json = args.Any(a => a == "--json");
        var positional = args.Where(a => !a.StartsWith('-')).ToList();
        if (positional.Count != 1)
        {
            Console.Error.WriteLine("autarx: expected <workspace> — try 'autarx patch --help'");
            return 2;
        }

        var manifest = PatchEngine.Undo(positional[0]);
        if (manifest is null)
        {
            Console.Error.WriteLine("autarx: nothing to undo (no patch history in .autarx/patch-history.jsonl)");
            return 1;
        }

        if (json)
        {
            JsonOutput.Write(new UndoJson(Undone: true, manifest.Timestamp, manifest.Files.Select(f => f.RelativePath).ToList()));
        }
        else
        {
            Console.WriteLine($"undid patch from {manifest.Timestamp:u}, restored:");
            foreach (var file in manifest.Files)
                Console.WriteLine($"  {file.RelativePath}");
        }
        return 0;
    }

    private static (IReadOnlyList<PatchOp> Operations, List<string> Errors) ReadOps(string opsFile)
    {
        if (!File.Exists(opsFile))
            return ([], [$"operations file not found: {opsFile}"]);
        return PatchOpSet.Parse(File.ReadAllText(opsFile));
    }

    private static string Provenance() =>
        $"semantic patch by autarx {Program.Version()} ({DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ})";

    private static void RenderPlan(PatchPlan plan)
    {
        Console.WriteLine($"operations: {plan.Operations.Count} ({plan.Operations.Count(r => r.Ok)} ok, {plan.Operations.Count(r => !r.Ok)} failed)");
        foreach (var result in plan.Operations)
        {
            Console.WriteLine(result.Ok
                ? $"  [{result.Op}] {result.Target} — ok"
                : $"  [{result.Op}] {result.Target} — FAILED: {result.Error}");
        }

        if (!plan.Valid)
        {
            Console.WriteLine("plan invalid — nothing will be applied");
            return;
        }

        Console.WriteLine($"\nplanned semantic diff ({plan.AffectedFiles.Count} file(s): {string.Join(", ", plan.AffectedFiles)}):");
        RenderDiff(plan.Diff!);
    }

    private static void RenderDiff(WorkspaceDiff diff)
    {
        if (diff.IsEmpty)
        {
            Console.WriteLine("  (no semantic differences — the operations are no-ops)");
            return;
        }

        foreach (var change in diff.Objects)
        {
            var marker = change.ChangeType == ObjectChangeType.Added ? "+" : change.ChangeType == ObjectChangeType.Removed ? "-" : "~";
            Console.WriteLine($"  {marker} {change.ElementType} {change.AbsolutePath}");
            if (change.Properties is { } properties)
                foreach (var property in properties)
                    Console.WriteLine($"      {property.Path}: {property.Before ?? "(absent)"} → {property.After ?? "(absent)"}");
        }
        foreach (var reference in diff.References)
            Console.WriteLine($"  {(reference.ChangeType == ObjectChangeType.Added ? '+' : '-')} {reference.Kind} {reference.SourcePath} -> {reference.TargetPath}");
    }

    internal static object ToDto(PatchPlan plan) => new
    {
        valid = plan.Valid,
        operations = plan.Operations.Select(r => new
        {
            index = r.Index,
            op = r.Op,
            target = r.Target,
            ok = r.Ok,
            error = r.Error,
        }).ToList(),
        affectedFiles = plan.AffectedFiles,
        diff = plan.Diff is null ? null : DiffCommand.ToDto(plan.Diff),
    };

    internal sealed record ApplyJson(
        bool Applied,
        DateTime Timestamp,
        IReadOnlyList<string> Files,
        int OperationCount,
        DiffCommand.DiffJson Diff);

    internal sealed record UndoJson(bool Undone, DateTime Timestamp, IReadOnlyList<string> Files);

    private static int Usage(int exitCode = 2)
    {
        Console.WriteLine("""
            autarx patch — reviewed semantic editing

            Every write is gated by a mandatory before/after semantic diff.
            Apply keeps <file>.autarx-bak backups and a manifest under
            .autarx/patch-history.jsonl for undo. This is NOT a configurator:
            rename, ECUC parameter and reference edits only.

            Usage:
              autarx patch plan <workspace> --file <ops.json> [--json]
                  Validate operations and preview the semantic diff. Read-only.
              autarx patch apply <workspace> --file <ops.json> [--json]
                  Apply after re-planning; nothing is written when any
                  operation fails.
              autarx patch undo <workspace> [--json]
                  Restore the most recent patch from backups.

            Operations file format:
              { "operations": [
                { "op": "rename", "target": "/Pkg/Obj", "newName": "NewName" },
                { "op": "set-parameter", "target": "/Pkg/Module",
                  "definition": "/Vendor/Module/Ctr/Param", "value": "2" },
                { "op": "add-parameter", "target": "/Pkg/Module",
                  "definition": "/Vendor/Module/Ctr/NewParam", "value": "5",
                  "type": "numeric" },
                { "op": "set-reference", "target": "/Pkg/Module",
                  "definition": "/Vendor/Module/Ctr/Ref", "value": "/Pkg/Other" }
              ] }

            Exit codes:
              0  success (plan valid / applied / undone)
              1  operation or plan failures, nothing to undo (nothing written)
              2  usage, file, or parse errors
            """);
        return exitCode;
    }

    private static int UnknownSubcommand(string name)
    {
        Console.Error.WriteLine($"autarx: unknown patch subcommand '{name}' — try 'autarx patch --help'");
        return 2;
    }

    private static bool TryParseOpArgs(string[] args, out string workspace, out string opsFile, out bool json, out string? error)
    {
        workspace = "";
        opsFile = "";
        json = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                json = true;
            }
            else if (arg == "--file")
            {
                if (i + 1 >= args.Length)
                {
                    error = "autarx: --file requires a value";
                    return false;
                }
                opsFile = args[++i];
            }
            else if (arg.StartsWith('-'))
            {
                error = $"autarx: unknown option '{arg}' — try 'autarx patch --help'";
                return false;
            }
            else if (workspace.Length == 0)
            {
                workspace = arg;
            }
            else
            {
                error = "autarx: expected <workspace> --file <ops.json>";
                return false;
            }
        }

        if (workspace.Length == 0 || opsFile.Length == 0)
            error = "autarx: expected <workspace> --file <ops.json> — try 'autarx patch --help'";

        return error is null;
    }
}
