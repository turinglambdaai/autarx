using Autarx.Core.Diff;
using Autarx.Core.Index;
using Autarx.Core.Models;

namespace Autarx.Cli;

internal static class DiffCommand
{
    /// <summary>`autarx diff <before> <after>` — semantic diff between two
    /// deliveries. Exit 0 = identical, 1 = differences, 2 = usage/parse error.</summary>
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var before, out var after, out var detail, out var json, out var error))
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

        WorkspaceDiff diff;
        try
        {
            diff = SemanticDiff.Compare(beforeIndex, afterIndex, detail);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"autarx: diff failed: {ex.Message}");
            return 2;
        }

        if (json)
        {
            JsonOutput.Write(ToDto(diff));
            return diff.IsEmpty ? 0 : 1;
        }

        Console.WriteLine($"before: {diff.Before.DocumentCount} files, {diff.Before.ObjectCount} objects, {diff.Before.ReferenceCount} references — {diff.Before.Path}");
        Console.WriteLine($"after:  {diff.After.DocumentCount} files, {diff.After.ObjectCount} objects, {diff.After.ReferenceCount} references — {diff.After.Path}");

        if (diff.IsEmpty)
        {
            Console.WriteLine("no semantic differences");
            return 0;
        }

        ReportSection("added", diff.Objects.Where(o => o.ChangeType == ObjectChangeType.Added),
            o => $"+ {o.ElementType} {o.AbsolutePath}");
        ReportSection("removed", diff.Objects.Where(o => o.ChangeType == ObjectChangeType.Removed),
            o => $"- {o.ElementType} {o.AbsolutePath}");
        ReportSection("modified", diff.Objects.Where(o => o.ChangeType == ObjectChangeType.Modified), o =>
        {
            var type = o.ElementTypeChanged ? $"{o.BeforeElementType} → {o.AfterElementType} " : $"{o.AfterElementType ?? o.BeforeElementType} ";
            var line = $"~ {type}{o.AbsolutePath}";
            if (o.Properties is { } properties)
                line += "\n" + string.Join("\n", properties.Select(PropertyLine));
            return line;
        });
        ReportSection("references", diff.References.AsEnumerable(),
            r => $"{(r.ChangeType == ObjectChangeType.Added ? "+" : "-")} {r.Kind} {r.SourcePath} -> {r.TargetPath}");
        ReportSection("files", diff.Files.AsEnumerable(),
            f => $"{(f.ChangeType == ObjectChangeType.Added ? '+' : f.ChangeType == ObjectChangeType.Removed ? '-' : '~')} {f.RelativePath}" +
                 (f.ChangeType == ObjectChangeType.Modified ? $" ({f.BeforeSizeBytes} → {f.AfterSizeBytes} bytes)" : ""));

        return 1;
    }

    private static string PropertyLine(PropertyChange p) =>
        $"    {p.Path}: {p.Before ?? "(absent)"} → {p.After ?? "(absent)"}";

    private static void ReportSection<T>(string title, IEnumerable<T> items, Func<T, string> render)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return;
        Console.WriteLine($"\n{title} ({list.Count}):");
        foreach (var item in list)
            Console.WriteLine("  " + render(item));
    }

    internal static ObjectChangeJson ToObjectChangeDto(ObjectChange o) => new(
        o.ChangeType,
        o.ShortName,
        o.SemanticKind,
        o.AbsolutePath,
        o.BeforeElementType,
        o.AfterElementType,
        o.ElementTypeChanged,
        o.Properties?.Select(p => new PropertyChangeJson(p.Path, p.ChangeType, p.Before, p.After)).ToList());

    internal static DiffJson ToDto(WorkspaceDiff diff) => new(
        new WorkspaceDiffSideJson(diff.Before.Path, diff.Before.DocumentCount, diff.Before.ObjectCount, diff.Before.ReferenceCount),
        new WorkspaceDiffSideJson(diff.After.Path, diff.After.DocumentCount, diff.After.ObjectCount, diff.After.ReferenceCount),
        diff.Objects.Select(ToObjectChangeDto).ToList(),
        diff.References.Select(r => new ReferenceChangeJson(r.ChangeType, r.SourcePath, r.Kind, r.TargetPath, FileName(r.SourceFile))).ToList(),
        diff.Files.Select(f => new FileChangeJson(f.ChangeType, f.RelativePath, f.BeforeSizeBytes, f.AfterSizeBytes)).ToList());

    private static string? FileName(string? path) => path is null ? null : Path.GetFileName(path);

    internal sealed record DiffJson(
        WorkspaceDiffSideJson Before,
        WorkspaceDiffSideJson After,
        IReadOnlyList<ObjectChangeJson> Objects,
        IReadOnlyList<ReferenceChangeJson> References,
        IReadOnlyList<FileChangeJson> Files);

    internal sealed record WorkspaceDiffSideJson(
        string Path,
        int Documents,
        int Objects,
        int References);

    internal sealed record ObjectChangeJson(
        ObjectChangeType ChangeType,
        string ShortName,
        SemanticKind SemanticKind,
        string AbsolutePath,
        string? BeforeElementType,
        string? AfterElementType,
        bool ElementTypeChanged,
        IReadOnlyList<PropertyChangeJson>? Properties);

    internal sealed record PropertyChangeJson(
        string Path,
        PropertyChangeType ChangeType,
        string? Before,
        string? After);

    internal sealed record ReferenceChangeJson(
        ObjectChangeType ChangeType,
        string SourcePath,
        string Kind,
        string TargetPath,
        string? SourceFile);

    internal sealed record FileChangeJson(
        ObjectChangeType ChangeType,
        string RelativePath,
        long? BeforeSizeBytes,
        long? AfterSizeBytes);

    private static bool TryParseArgs(
        string[] args,
        out string before,
        out string after,
        out bool detail,
        out bool json,
        out string? error)
    {
        before = "";
        after = "";
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
                error = "autarx: expected <before> <after>";
                return false;
            }
        }

        if (before.Length == 0 || after.Length == 0)
            error = "autarx: expected <before> <after> — try 'autarx --help'";

        return error is null;
    }
}
