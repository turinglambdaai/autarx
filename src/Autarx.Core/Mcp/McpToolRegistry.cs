namespace Autarx.Core.Mcp;

/// <summary>
/// The MCP tool surface: read-only analysis tools plus the two patch tools.
/// Note what is deliberately absent — no raw file write, no XML editing, no
/// way to skip the plan/diff boundary. Agents plan, review the returned
/// semantic diff, then apply; every call lands in the workspace audit log.
/// </summary>
public static class McpToolRegistry
{
    private static readonly List<(string Name, string Description, object InputSchema)> Tools =
    [
        Tool("inspect", "Workspace summary: files, identifiables, references, unresolved counts, AUTOSAR release",
            Schema(["workspace"],
                ("workspace", "File or directory containing ARXML"))),
        Tool("find", "Find identifiables by SHORT-NAME substring",
            Schema(["workspace", "pattern"],
                ("workspace", "File or directory containing ARXML"),
                ("pattern", "SHORT-NAME substring, case-insensitive"))),
        Tool("refs", "Outgoing and incoming references of one object",
            Schema(["workspace", "object"],
                ("workspace", "File or directory containing ARXML"),
                ("object", "Absolute path (/Vehicle/Ecus/RadarFL) or exact SHORT-NAME"))),
        Tool("trace", "Breadth-first walk over the reference graph from one object",
            Schema(["workspace", "object"],
                ("workspace", "File or directory containing ARXML"),
                ("object", "Absolute path or exact SHORT-NAME"),
                ("depth", "Maximum hop count (default 3)"))),
        Tool("ecus", "List ECU instances in the workspace",
            Schema(["workspace"],
                ("workspace", "File or directory containing ARXML"))),
        Tool("unresolved", "References whose target is not present in the workspace",
            Schema(["workspace"],
                ("workspace", "File or directory containing ARXML"))),
        Tool("diff", "Semantic diff between two deliveries (path identity, property level with detail)",
            Schema(["before", "after"],
                ("before", "Before delivery (file or directory)"),
                ("after", "After delivery (file or directory)"),
                ("detail", "Include property-level changes (default false)"))),
        Tool("impact", "ECU-scoped impact analysis across two deliveries",
            Schema(["before", "after", "ecu"],
                ("before", "Before delivery"),
                ("after", "After delivery"),
                ("ecu", "ECU name or absolute path"))),
        Tool("comm", "Communication projection: clusters → frames → PDUs → signals, orphans, ECUC comm modules",
            Schema(["workspace"],
                ("workspace", "File or directory containing ARXML"),
                ("cluster", "Restrict to one communication cluster"))),
        Tool("validate", "Structural checks on one ARXML file (ARX00NN) — not vendor validation",
            Schema(["input"],
                ("input", "Single ARXML file"))),
        Tool("patch_plan", "Plan reviewed ARXML edits and return the mandatory before/after semantic diff (read-only)",
            Schema(["workspace", "operations"],
                ("workspace", "File or directory containing ARXML"),
                ("operations", "Array of ops: rename | set-parameter | add-parameter | set-reference"))),
        Tool("patch_apply", "Apply a reviewed patch: re-plans, writes changed files with backups, records history. Never bypasses the plan/diff boundary",
            Schema(["workspace", "operations"],
                ("workspace", "File or directory containing ARXML"),
                ("operations", "Array of ops, same format as patch_plan"))),
    ];

    public static bool Exists(string name) => Tools.Any(t => t.Name == name);

    public static IReadOnlyList<object> Describe() => Tools
        .Select(t => (object)new { name = t.Name, description = t.Description, inputSchema = t.InputSchema })
        .ToList();

    private static (string, string, object) Tool(string name, string description, object schema) =>
        (name, description, schema);

    private static object Schema(string[] required, params (string Name, string Description)[] properties)
    {
        var propertyMap = new Dictionary<string, object>();
        foreach (var (name, description) in properties)
            propertyMap[name] = new { type = "string", description };
        if (required.Length > 0)
            return new { type = "object", properties = propertyMap, required };
        return new { type = "object", properties = propertyMap };
    }
}
