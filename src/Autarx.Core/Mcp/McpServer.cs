using System.Text.Json;
using Autarx.Core.Communication;
using Autarx.Core.Diff;
using Autarx.Core.Impact;
using Autarx.Core.Index;
using Autarx.Core.Models;
using Autarx.Core.Parsing;
using Autarx.Core.Patch;
using Autarx.Core.Validation;

namespace Autarx.Core.Mcp;

/// <summary>
/// MCP (Model Context Protocol) stdio server over the deterministic tool
/// API: newline-delimited JSON-RPC 2.0, one request per line. The AI layer
/// constraint lives in the design of the tools themselves — everything that
/// writes goes through PatchEngine's plan/diff boundary, and every tool call
/// is appended to the workspace audit log. There is no tool that bypasses
/// review.
/// </summary>
public static class McpServer
{
    private const string ProtocolVersion = "2024-11-05";

    public static int Serve(TextReader input, TextWriter output, string version)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument? request = null;
            try
            {
                request = JsonDocument.Parse(line);
                Handle(request.RootElement, output, version);
            }
            catch (JsonException)
            {
                Write(output, null, error: Error(-32700, "parse error"));
            }
            catch (Exception ex)
            {
                var id = request is null ? (JsonElement?)null : Id(request.RootElement);
                Write(output, id, error: Error(-32603, ex.Message));
            }
            finally
            {
                request?.Dispose();
            }
        }
        return 0;
    }

    private static void Handle(JsonElement request, TextWriter output, string version)
    {
        var id = Id(request);
        var method = request.TryGetProperty("method", out var m) ? m.GetString() : null;
        var isNotification = !request.TryGetProperty("id", out _);

        if (method == "notifications/initialized" || method is null)
            return; // notification: no response

        switch (method)
        {
            case "initialize":
                Write(output, id, result: new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "autarx", version },
                });
                break;

            case "ping":
                Write(output, id, result: new { });
                break;

            case "tools/list":
                Write(output, id, result: new { tools = McpToolRegistry.Describe() });
                break;

            case "tools/call":
                CallTool(request.GetProperty("params"), output, id, version);
                break;

            default:
                // an unknown NOTIFICATION gets no response (JSON-RPC 2.0)
                if (isNotification)
                    return;
                Write(output, id, error: Error(-32601, $"unknown method '{method}'"));
                break;
        }
    }

    private static void CallTool(JsonElement parameters, TextWriter output, JsonElement? id, string version)
    {
        var name = parameters.TryGetProperty("name", out var n) ? n.GetString() : null;
        var arguments = parameters.TryGetProperty("arguments", out var a) ? a : JsonDocument.Parse("{}").RootElement.Clone();

        if (name is null || !McpToolRegistry.Exists(name))
        {
            Write(output, id, result: ToolResult($"unknown tool '{name}'", isError: true));
            return;
        }

        var workspace = Str(arguments, "workspace") ?? Str(arguments, "before") ?? Str(arguments, "input") ?? ".";
        Audit(workspace, name, arguments);

        try
        {
            var result = name switch
            {
                "inspect" => Inspect(Str(arguments, "workspace")!),
                "find" => Find(Str(arguments, "workspace")!, Str(arguments, "pattern")!),
                "refs" => Refs(Str(arguments, "workspace")!, Str(arguments, "object")!),
                "trace" => Trace(Str(arguments, "workspace")!, Str(arguments, "object")!, Int(arguments, "depth") ?? 3),
                "ecus" => Ecus(Str(arguments, "workspace")!),
                "unresolved" => Unresolved(Str(arguments, "workspace")!),
                "diff" => Diff(Str(arguments, "before")!, Str(arguments, "after")!, Bool(arguments, "detail") ?? false),
                "impact" => Impact(Str(arguments, "before")!, Str(arguments, "after")!, Str(arguments, "ecu")!),
                "comm" => Comm(Str(arguments, "workspace")!, Str(arguments, "cluster")),
                "validate" => ValidateFile(Str(arguments, "input")!),
                "patch_plan" => PatchPlanTool(Str(arguments, "workspace")!, arguments.GetProperty("operations")),
                "patch_apply" => PatchApplyTool(Str(arguments, "workspace")!, arguments.GetProperty("operations"), version),
                _ => ToolResult($"tool '{name}' has no handler", isError: true),
            };
            Write(output, id, result: result);
        }
        catch (Exception ex)
        {
            Write(output, id, result: ToolResult(ex.Message, isError: true));
        }
    }

    // ---- tool implementations: pure Core calls, JSON-shaped results ----

    private static object Inspect(string workspace)
    {
        var index = WorkspaceIndex.Build(workspace);
        var summary = index.Summarize();
        return ToolResult(new
        {
            workspace,
            fileCount = summary.FileCount,
            totalFileBytes = summary.TotalFileBytes,
            identifiableCount = summary.IdentifiableCount,
            packageCount = summary.PackageCount,
            referenceCount = summary.ReferenceCount,
            unresolvedReferenceCount = summary.UnresolvedReferenceCount,
            duplicatePathCount = summary.DuplicatePathCount,
            fileErrorCount = summary.FileErrorCount,
            autosarRelease = summary.AutosarRelease,
            mixedSchema = summary.MixedSchema,
            semanticCounts = summary.SemanticCounts,
        });
    }

    private static object Find(string workspace, string pattern)
    {
        var index = WorkspaceIndex.Build(workspace);
        return ToolResult(index.FindByName(pattern).Select(McpObject).ToList());
    }

    private static object Refs(string workspace, string objectName)
    {
        var index = WorkspaceIndex.Build(workspace);
        var resolution = index.Resolve(objectName);
        if (resolution.Status != ResolveStatus.Found)
            return ToolResult(ResolutionMessage(objectName, resolution), isError: true);

        var target = resolution.Object!;
        return ToolResult(new
        {
            objectPath = target.AbsolutePath,
            outgoing = index.OutgoingOf(target.AbsolutePath).Select(McpReference).ToList(),
            incoming = index.IncomingOf(target.AbsolutePath).Select(McpReference).ToList(),
        });
    }

    private static object Trace(string workspace, string objectName, int depth)
    {
        var index = WorkspaceIndex.Build(workspace);
        var resolution = index.Resolve(objectName);
        if (resolution.Status != ResolveStatus.Found)
            return ToolResult(ResolutionMessage(objectName, resolution), isError: true);

        return ToolResult(index.Trace(resolution.Object!.AbsolutePath, TraceDirection.Both, depth));
    }

    private static object Ecus(string workspace)
    {
        var index = WorkspaceIndex.Build(workspace);
        return ToolResult(index.Objects
            .Where(o => o.SemanticKind == SemanticKind.Ecu)
            .Select(McpObject).ToList());
    }

    private static object Unresolved(string workspace)
    {
        var index = WorkspaceIndex.Build(workspace);
        return ToolResult(index.UnresolvedReferences.Select(McpReference).ToList());
    }

    private static object Diff(string before, string after, bool detail) =>
        ToolResult(SemanticDiff.Compare(before, after, detail));

    private static object Impact(string before, string after, string ecuName)
    {
        var beforeIndex = WorkspaceIndex.Build(before);
        var afterIndex = WorkspaceIndex.Build(after);

        SemanticObject? ecu = null;
        WorkspaceIndex? home = null;
        foreach (var index in new[] { afterIndex, beforeIndex })
        {
            var resolution = index.Resolve(ecuName);
            if (resolution.Status == ResolveStatus.Ambiguous)
                return ToolResult(ResolutionMessage(ecuName, resolution), isError: true);
            if (resolution.Status == ResolveStatus.Found)
            {
                ecu = resolution.Object;
                home = index;
                break;
            }
        }

        if (ecu is null || home is null)
            return ToolResult($"no workspace object matches '{ecuName}' in either delivery", isError: true);
        if (ecu.SemanticKind != SemanticKind.Ecu)
            return ToolResult($"'{ecuName}' is a {ecu.SemanticKind}, not an ECU instance", isError: true);

        var diff = SemanticDiff.Compare(beforeIndex, afterIndex, includePropertyDetail: false);
        var report = ImpactAnalysis.Analyze(beforeIndex, afterIndex, ecu.AbsolutePath, !ReferenceEquals(home, afterIndex), diff, includePropertyDetail: false);
        return ToolResult(report);
    }

    private static object Comm(string workspace, string? cluster)
    {
        var index = WorkspaceIndex.Build(workspace);
        var model = CommunicationProjection.Build(index);
        if (cluster is not null)
        {
            var resolved = index.Resolve(cluster);
            var projection = resolved.Status == ResolveStatus.Found && resolved.Object!.SemanticKind == SemanticKind.CommunicationCluster
                ? model.Clusters.FirstOrDefault(c => c.ClusterPath == resolved.Object!.AbsolutePath)
                : null;
            model = model with { Clusters = projection is null ? [] : [projection] };
        }
        return ToolResult(model);
    }

    private static object ValidateFile(string input)
    {
        var root = ArxmlParser.ParseFile(input);
        var modules = EcucReader.ReadModules(root);
        var diagnostics = ValidationEngine.Validate(modules);
        return ToolResult(new
        {
            input,
            errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
            warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
            diagnostics = diagnostics.Select(d => new
            {
                code = d.Code,
                severity = d.Severity.ToString().ToLowerInvariant(),
                message = d.Message,
                path = d.Path,
            }).ToList(),
        });
    }

    private static object PatchPlanTool(string workspace, JsonElement operations) =>
        ToolResult(RunPlan(workspace, operations));

    private static object PatchApplyTool(string workspace, JsonElement operations, string version)
    {
        var plan = RunPlan(workspace, operations);
        if (!plan.Valid || plan.Diff is null || plan.TempWorkspace is null)
            return ToolResult(new { applied = false, plan.Operations, reason = "plan invalid — nothing was written" }, isError: true);

        var manifest = PatchEngine.Apply(workspace, plan, plan.TempWorkspace);
        return ToolResult(new
        {
            applied = true,
            timestamp = manifest.Timestamp,
            files = manifest.Files.Select(f => f.RelativePath).ToList(),
            operationCount = manifest.OperationCount,
            provenance = $"semantic patch by autarx {version}",
            diff = plan.Diff,
        });
    }

    private static PatchPlan RunPlan(string workspace, JsonElement operations)
    {
        var raw = operations.ValueKind == JsonValueKind.String
            ? operations.GetString()!
            : """{ "operations": """ + operations.GetRawText() + " }";
        var (ops, errors) = PatchOpSet.Parse(raw);
        if (errors.Count > 0)
            return new PatchPlan(
                errors.Select((e, i) => new PatchOpResult(i, "parse", "", false, e)).ToList(),
                Valid: false, [], null);

        return PatchEngine.Plan(workspace, ops);
    }

    // ---- plumbing ----

    private static object ToolResult(object payload, bool isError = false) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload, McpJson.Options) } },
        isError,
    };

    private static object ResolutionMessage(string name, ObjectResolution resolution) =>
        resolution.Status == ResolveStatus.Ambiguous
            ? $"'{name}' is ambiguous, {resolution.Candidates.Count} objects match: {string.Join(", ", resolution.Candidates.Select(c => c.AbsolutePath))}"
            : $"no workspace object matches '{name}'";

    private static object McpObject(SemanticObject o) => new
    {
        shortName = o.ShortName,
        elementType = o.ElementType,
        semanticKind = o.SemanticKind,
        absolutePath = o.AbsolutePath,
        sourceFile = Path.GetFileName(o.SourceFile),
    };

    private static object McpReference(AutosarReference r) => new
    {
        kind = r.Kind,
        sourcePath = r.SourcePath,
        targetPath = r.TargetPath,
        resolved = r.IsResolved,
    };

    private static void Audit(string workspace, string tool, JsonElement arguments)
    {
        try
        {
            var root = Path.GetFullPath(workspace);
            if (!Directory.Exists(root))
                return;
            var dir = Path.Combine(root, ".autarx");
            Directory.CreateDirectory(dir);
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow,
                tool,
                arguments = arguments.ValueKind == JsonValueKind.Object ? (object)arguments : null,
            }, McpJson.Options);
            File.AppendAllText(Path.Combine(dir, "audit.jsonl"), entry + Environment.NewLine);
        }
        catch (Exception)
        {
            // audit is best-effort: a failing log must never block a tool
        }
    }

    private static void Write(TextWriter output, JsonElement? id, object? result = null, object? error = null)
    {
        object response = error is not null
            ? new { jsonrpc = "2.0", id = IdValue(id), error }
            : new { jsonrpc = "2.0", id = IdValue(id), result };
        output.WriteLine(JsonSerializer.Serialize(response, McpJson.Envelope));
        output.Flush();
    }

    private static object IdValue(JsonElement? id) =>
        id is { } value && value.ValueKind != JsonValueKind.Undefined ? value.Clone() : null!;

    private static JsonElement? Id(JsonElement request) =>
        request.TryGetProperty("id", out var id) ? id : (JsonElement?)null;

    private static object Error(int code, string message) => new { code, message };

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static bool? Bool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.True ? true
            : value.ValueKind == JsonValueKind.False ? false
            : null;
    }
}
