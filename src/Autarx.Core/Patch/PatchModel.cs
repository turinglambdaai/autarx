using System.Text.Json;
using Autarx.Core.Diff;

namespace Autarx.Core.Patch;

/// <summary>
/// One reviewed change. Targets are absolute short-name paths; ECUC values
/// are addressed by their DEFINITION-REF — the identity engineers and
/// diff tools talk about, stable across file reorganisation.
/// </summary>
public abstract record PatchOp(string Target);

public sealed record RenameOp(string Target, string NewName) : PatchOp(Target);

public sealed record SetParameterOp(string Target, string Definition, string Value) : PatchOp(Target);

public sealed record AddParameterOp(string Target, string Definition, string Value, string ValueType) : PatchOp(Target);

public sealed record SetReferenceOp(string Target, string Definition, string Value) : PatchOp(Target);

/// <summary>Outcome of one operation inside a plan. Index = position in the
/// submitted operation list.</summary>
public sealed record PatchOpResult(int Index, string Op, string Target, bool Ok, string? Error);

/// <summary>
/// The reviewed contract for one patch run: per-operation results, the
/// affected files, and the mandatory before/after semantic diff (null when
/// any operation failed — nothing is applied unless every op is OK and the
/// diff is present).
/// </summary>
public sealed record PatchPlan(
    IReadOnlyList<PatchOpResult> Operations,
    bool Valid,

    /// <summary>Workspace-relative file names the patch touches.</summary>
    IReadOnlyList<string> AffectedFiles,

    /// <summary>Mandatory before/after semantic diff; null when invalid.</summary>
    WorkspaceDiff? Diff,

    /// <summary>Temp workspace holding the patched materialization; removed
    /// by Apply, cleaned up by the caller otherwise.</summary>
    string? TempWorkspace = null);

/// <summary>JSON operation set — the format AI agents and humans submit.</summary>
public static class PatchOpSet
{
    public static (IReadOnlyList<PatchOp> Ops, List<string> Errors) Parse(string json)
    {
        var errors = new List<string>();
        var ops = new List<PatchOp>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            errors.Add($"invalid JSON: {ex.Message}");
            return (ops, errors);
        }

        if (!document.RootElement.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
        {
            errors.Add("expected a top-level { \"operations\": [ ... ] } array");
            return (ops, errors);
        }

        var index = 0;
        foreach (var element in operations.EnumerateArray())
        {
            var op = element.TryGetProperty("op", out var opElement) ? opElement.GetString() : null;
            var target = Str(element, "target");

            switch (op)
            {
                case "rename" when Str(element, "newName") is { Length: > 0 } newName && target is not null:
                    ops.Add(new RenameOp(target, newName));
                    break;
                case "set-parameter" when Str(element, "definition") is { Length: > 0 } def && Str(element, "value") is { Length: > 0 } value && target is not null:
                    ops.Add(new SetParameterOp(target, def, value));
                    break;
                case "add-parameter" when Str(element, "definition") is { Length: > 0 } defAdd && Str(element, "value") is { Length: > 0 } valueAdd && target is not null:
                    var valueType = Str(element, "type") ?? "numeric";
                    if (valueType is not ("numeric" or "textual"))
                    {
                        errors.Add($"operation {index}: type must be \"numeric\" or \"textual\"");
                        break;
                    }
                    ops.Add(new AddParameterOp(target, defAdd, valueAdd, valueType));
                    break;
                case "set-reference" when Str(element, "definition") is { Length: > 0 } defRef && Str(element, "value") is { Length: > 0 } valueRef && target is not null:
                    ops.Add(new SetReferenceOp(target, defRef, valueRef));
                    break;
                default:
                    errors.Add($"operation {index}: incomplete or unsupported op '{op}' (rename | set-parameter | add-parameter | set-reference; all need target)");
                    break;
            }
            index++;
        }

        return (ops, errors);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
