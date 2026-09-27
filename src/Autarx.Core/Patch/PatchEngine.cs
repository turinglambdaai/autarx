using System.Text.Json;
using Autarx.Core.Diff;
using Autarx.Core.Index;
using Autarx.Core.Models;
using Autarx.Core.Parsing;

namespace Autarx.Core.Patch;

/// <summary>
/// Plan → apply → undo pipeline for reviewed ARXML changes. Planning never
/// touches the workspace: operations run against in-memory trees, the result
/// is materialized into a temp workspace, and the mandatory before/after
/// semantic diff is computed against it. Applying writes only files the plan
/// proved changed, keeps a backup per file, and records a manifest entry for
/// undo. No operation ever bypasses the plan/diff boundary.
/// </summary>
public static class PatchEngine
{
    public static PatchPlan Plan(string workspacePath, IReadOnlyList<PatchOp> operations, string? provenance = null)
    {
        var results = new List<PatchOpResult>();
        var errors = new List<string>();

        var index = WorkspaceIndex.Build(workspacePath);
        var trees = new Dictionary<string, ArxmlElement>(StringComparer.Ordinal);
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var pathMap = new Dictionary<string, string>(StringComparer.Ordinal); // original path → current path (renames)

        var workspaceRoot = Path.GetFullPath(workspacePath);
        var documents = index.Documents;
        var currentOp = "";

        ArxmlElement TreeFor(string filePath)
        {
            if (!trees.TryGetValue(filePath, out var tree))
            {
                tree = ArxmlParser.ParseFile(filePath);
                trees[filePath] = tree;
            }
            return tree;
        }

        SemanticObject? ResolveTarget(string target, int opIndex)
        {
            var mapped = pathMap.GetValueOrDefault(target, target);
            foreach (var document in documents)
            {
                var element = SemanticDiff.FindObjectElement(TreeFor(document.FilePath), mapped);
                if (element is not null)
                    return new SemanticObject
                    {
                        ShortName = mapped.Split('/').Last(),
                        ElementType = element.Name,
                        SemanticKind = SemanticClassifier.Classify(element.Name),
                        AbsolutePath = mapped,
                        SourceFile = document.FilePath,
                        ContentHash = 0,
                    };
            }
            results.Add(new PatchOpResult(opIndex, currentOp, target, Ok: false, Error: $"target '{mapped}' not found in workspace"));
            return null;
        }

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            currentOp = op switch
            {
                RenameOp => "rename",
                SetParameterOp => "set-parameter",
                AddParameterOp => "add-parameter",
                SetReferenceOp => "set-reference",
                _ => "?",
            };

            var tree = ResolveTarget(op.Target, i);
            if (tree is null)
                continue;

            var mappedPath = pathMap.GetValueOrDefault(op.Target, op.Target);

            switch (op)
            {
                case RenameOp rename:
                {
                    var element = SemanticDiff.FindObjectElement(TreeFor(tree.SourceFile), mappedPath)!;
                    var shortNameElement = element.Element("SHORT-NAME");
                    if (shortNameElement is null)
                    {
                        results.Add(new PatchOpResult(i, "rename", op.Target, false, "target has no SHORT-NAME"));
                        break;
                    }

                    var oldPath = mappedPath;
                    var newPath = mappedPath[..^shortNameElement.Text!.Length] + rename.NewName;

                    shortNameElement.SetText(rename.NewName);

                    // rewrite every reference pointing into the renamed subtree,
                    // across all workspace documents
                    foreach (var document in documents)
                    {
                        var docTree = TreeFor(document.FilePath);
                        var rewritten = RewriteReferences(docTree, oldPath, newPath);
                        if (rewritten)
                            dirty.Add(document.FilePath);
                    }
                    dirty.Add(tree.SourceFile);
                    pathMap[oldPath] = newPath;
                    foreach (var (oldKey, newKey) in pathMap.ToList())
                        if (newKey == oldPath)
                            pathMap[oldKey] = newPath;

                    results.Add(new PatchOpResult(i, "rename", op.Target, true, $"{oldPath} → {newPath}"));
                    break;
                }

                case SetParameterOp setParameter:
                {
                    var element = SemanticDiff.FindObjectElement(TreeFor(tree.SourceFile), mappedPath)!;
                    var param = FindByDefinition(element, setParameter.Definition);
                    if (param is null)
                    {
                        results.Add(new PatchOpResult(i, "set-parameter", op.Target, false, $"no element with DEFINITION-REF {setParameter.Definition}"));
                        break;
                    }
                    var valueElement = param.Element("VALUE");
                    if (valueElement is null)
                    {
                        results.Add(new PatchOpResult(i, "set-parameter", op.Target, false, "parameter has no VALUE element"));
                        break;
                    }
                    valueElement.SetText(setParameter.Value);
                    dirty.Add(tree.SourceFile);
                    results.Add(new PatchOpResult(i, "set-parameter", op.Target, true, $"{setParameter.Definition} = {setParameter.Value}"));
                    break;
                }

                case AddParameterOp addParameter:
                {
                    var element = SemanticDiff.FindObjectElement(TreeFor(tree.SourceFile), mappedPath)!;
                    var containerDefinition = addParameter.Definition[..(addParameter.Definition.LastIndexOf('/') + 1)];
                    var container = FindByDefinition(element, containerDefinition) ?? element;
                    if (FindByDefinition(element, addParameter.Definition) is not null)
                    {
                        results.Add(new PatchOpResult(i, "add-parameter", op.Target, false, $"parameter already exists: {addParameter.Definition}"));
                        break;
                    }

                    var parameterValues = container.Element("PARAMETER-VALUES");
                    if (parameterValues is null)
                    {
                        parameterValues = new ArxmlElement("PARAMETER-VALUES");
                        container.Add(parameterValues);
                    }

                    var elementType = addParameter.ValueType == "textual"
                        ? "ECUC-TEXTUAL-PARAM-VALUE"
                        : "ECUC-NUMERICAL-PARAM-VALUE";
                    var parameter = new ArxmlElement(elementType);
                    var definitionRef = new ArxmlElement("DEFINITION-REF");
                    definitionRef.SetText(addParameter.Definition);
                    var value = new ArxmlElement("VALUE");
                    value.SetText(addParameter.Value);
                    parameter.Add(definitionRef);
                    parameter.Add(value);
                    parameterValues.Add(parameter);

                    dirty.Add(tree.SourceFile);
                    results.Add(new PatchOpResult(i, "add-parameter", op.Target, true, $"{addParameter.Definition} = {addParameter.Value}"));
                    break;
                }

                case SetReferenceOp setReference:
                {
                    var element = SemanticDiff.FindObjectElement(TreeFor(tree.SourceFile), mappedPath)!;
                    var reference = FindByDefinition(element, setReference.Definition);
                    if (reference is null)
                    {
                        results.Add(new PatchOpResult(i, "set-reference", op.Target, false, $"no reference with DEFINITION-REF {setReference.Definition}"));
                        break;
                    }
                    var valueRef = reference.Element("VALUE-REF");
                    if (valueRef is null)
                    {
                        results.Add(new PatchOpResult(i, "set-reference", op.Target, false, "reference has no VALUE-REF"));
                        break;
                    }
                    valueRef.SetText(setReference.Value);
                    dirty.Add(tree.SourceFile);
                    results.Add(new PatchOpResult(i, "set-reference", op.Target, true, $"{setReference.Definition} → {setReference.Value}"));
                    break;
                }
            }
        }

        if (results.Any(r => !r.Ok))
        {
            return new PatchPlan(results, Valid: false, [], null);
        }

        // materialize the full workspace into a temp directory (patched files
        // rewritten, untouched files copied verbatim) and diff it — this is
        // the mandatory before/after semantic diff for the whole patch
        var tempRoot = Path.Combine(Path.GetTempPath(), "autarx-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var affectedRelative = new List<string>();
        foreach (var document in documents)
        {
            var relative = Path.GetRelativePath(workspaceRoot, document.FilePath);
            var targetPath = Path.Combine(tempRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            if (dirty.Contains(document.FilePath))
            {
                ArxmlWriter.WriteFile(TreeFor(document.FilePath), targetPath, provenance);
                affectedRelative.Add(relative.Replace('\\', '/'));
            }
            else
            {
                File.Copy(document.FilePath, targetPath, overwrite: true);
            }
        }

        try
        {
            var diff = SemanticDiff.Compare(workspacePath, tempRoot, includePropertyDetail: true);
            return new PatchPlan(results, Valid: true, affectedRelative, diff, tempRoot);
        }
        catch (Exception ex)
        {
            errors.Add($"post-patch diff failed: {ex.Message}");
            TryDelete(tempRoot);
            return new PatchPlan(results, Valid: false, [], null);
        }
    }

    /// <summary>Applies a valid plan: writes the patched files over the
    /// originals (each original kept as &lt;file&gt;.autarx-bak) and records
    /// a manifest entry for undo. Returns the manifest record.</summary>
    public static PatchManifest Apply(string workspacePath, PatchPlan plan, string tempWorkspace)
    {
        if (!plan.Valid || plan.Diff is null)
            throw new InvalidOperationException("apply requires a valid plan with a semantic diff");

        var workspaceRoot = Path.GetFullPath(workspacePath);
        var backupDir = Path.Combine(workspaceRoot, ".autarx");
        Directory.CreateDirectory(backupDir);

        var files = new List<PatchFileRecord>();
        foreach (var document in plan.AffectedFiles)
        {
            var original = Path.Combine(workspaceRoot, document.Replace('/', Path.DirectorySeparatorChar));
            var backup = BackupPathFor(workspaceRoot, document);
            File.Copy(original, backup, overwrite: true);
            var patched = Path.Combine(tempWorkspace, document.Replace('/', Path.DirectorySeparatorChar));
            File.Copy(patched, original, overwrite: true);
            files.Add(new PatchFileRecord(document, Path.GetFileName(backup)));
        }

        var manifest = new PatchManifest(
            DateTime.UtcNow,
            files,
            plan.Operations.Count,
            Undone: false);
        File.AppendAllText(
            Path.Combine(backupDir, "patch-history.jsonl"),
            JsonSerializer.Serialize(manifest, ManifestOptions) + Environment.NewLine);

        TryDelete(tempWorkspace);

        return manifest;
    }

    /// <summary>Restores the most recent not-yet-undone patch from the
    /// history. Returns null when there is nothing to undo.</summary>
    public static PatchManifest? Undo(string workspacePath)
    {
        var historyFile = Path.Combine(Path.GetFullPath(workspacePath), ".autarx", "patch-history.jsonl");
        if (!File.Exists(historyFile))
            return null;

        var entries = File.ReadAllLines(historyFile)
            .Where(l => l.Trim().Length > 0)
            .Select(l => JsonSerializer.Deserialize<PatchManifest>(l, ManifestOptions)!)
            .ToList();

        var last = entries.FindLast(e => !e.Undone);
        if (last is null)
            return null;

        var workspaceRoot = Path.GetFullPath(workspacePath);
        foreach (var file in last.Files)
        {
            var original = Path.Combine(workspaceRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var backup = Path.Combine(workspaceRoot, ".autarx", file.BackupFile);
            File.Copy(backup, original, overwrite: true);
            File.Delete(backup);
        }

        last = last with { Undone = true };
        entries[entries.FindIndex(e => e.Timestamp == last.Timestamp && !e.Undone)] = last;
        File.WriteAllLines(
            historyFile,
            entries.Select(e => JsonSerializer.Serialize(e, ManifestOptions)));

        return last;
    }

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary> Rewrites reference texts under <paramref name="root"/> that
    /// point at <paramref name="oldPath"/> or into its subtree. Reference
    /// elements are those ending in REF whose text is an absolute path.</summary>
    private static bool RewriteReferences(ArxmlElement root, string oldPath, string newPath)
    {
        var changed = false;
        foreach (var element in new[] { root }.Concat(root.Descendants()))
        {
            if (!element.Name.EndsWith("REF", StringComparison.Ordinal)
                || element.Name == "DEFINITION-REF"
                || element.Text is null
                || !element.Text.StartsWith('/'))
            {
                continue;
            }

            if (element.Text == oldPath)
            {
                element.SetText(newPath);
                changed = true;
            }
            else if (element.Text.StartsWith(oldPath + "/", StringComparison.Ordinal))
            {
                element.SetText(newPath + element.Text[oldPath.Length..]);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Finds a descendant whose DEFINITION-REF equals the given
    /// definition (comparison on the trimmed text).</summary>
    private static ArxmlElement? FindByDefinition(ArxmlElement root, string definition)
    {
        foreach (var element in new[] { root }.Concat(root.Descendants()))
        {
            var definitionRef = element.Element("DEFINITION-REF");
            if (definitionRef?.Text?.Trim() == definition.Trim())
                return element;
        }
        return null;
    }

    /// <summary>Backups live under <c>.autarx/</c> with the relative path
    /// flattened into the file name (sub-tree files share names otherwise).</summary>
    private static string BackupPathFor(string workspaceRoot, string relativePath) =>
        Path.Combine(workspaceRoot, ".autarx", relativePath.Replace('/', '_') + ".autarx-bak");

    /// <summary>Best-effort temp cleanup; temp leftovers are harmless and
    /// never fail a patch.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // temp leftovers are harmless; never fail a patch over them
        }
    }
}

public sealed record PatchFileRecord(string RelativePath, string BackupFile);

public sealed record PatchManifest(
    DateTime Timestamp,
    IReadOnlyList<PatchFileRecord> Files,
    int OperationCount,
    bool Undone);
