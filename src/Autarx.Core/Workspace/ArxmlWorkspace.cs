using Autarx.Core.Models;
using Autarx.Core.Parsing;

namespace Autarx.Core.Workspace;

/// <summary>
/// One parsed ARXML document inside a workspace.
/// </summary>
public sealed record ArxmlDocument(string FilePath, ArxmlElement Root);

/// <summary>
/// An AUTOSAR identifiable discovered by SHORT-NAME. The path is built from
/// enclosing identifiable elements and is suitable for human-facing search,
/// reference tracing, diffing, and impact analysis.
/// </summary>
public sealed record ArxmlObject(
    string Path,
    string ShortName,
    string ElementName,
    string FilePath,
    ArxmlElement Element);

/// <summary>
/// A simple AUTOSAR reference edge discovered from *-REF / *-TREF elements.
/// Structured IREFs are represented by their nested reference elements.
/// </summary>
public sealed record ArxmlReference(
    string SourcePath,
    string TargetPath,
    string ReferenceKind,
    string? Destination,
    string FilePath);

/// <summary>
/// Multi-file ARXML workspace and the first layer of Autarx's semantic index.
///
/// This intentionally sits below ECUC-specific projections. System Description,
/// ECU Extract, ECUC and future communication projections should all share the
/// same workspace, object index and reference graph.
/// </summary>
public sealed class ArxmlWorkspace
{
    private readonly List<ArxmlDocument> _documents;
    private readonly List<ArxmlObject> _objects = [];
    private readonly List<ArxmlReference> _references = [];

    private ArxmlWorkspace(List<ArxmlDocument> documents)
    {
        _documents = documents;
        foreach (var document in documents)
            IndexElement(document.Root, document.FilePath, "");
    }

    public IReadOnlyList<ArxmlDocument> Documents => _documents;
    public IReadOnlyList<ArxmlObject> Objects => _objects;
    public IReadOnlyList<ArxmlReference> References => _references;

    /// <summary>
    /// Opens one .arxml file or recursively loads every .arxml file in a directory.
    /// </summary>
    public static ArxmlWorkspace Open(string inputPath)
    {
        var fullPath = Path.GetFullPath(inputPath);
        string[] files;

        if (File.Exists(fullPath))
        {
            files = [fullPath];
        }
        else if (Directory.Exists(fullPath))
        {
            files = Directory
                .EnumerateFiles(fullPath, "*.arxml", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            throw new FileNotFoundException($"ARXML input not found: {inputPath}", inputPath);
        }

        var documents = files
            .Select(path => new ArxmlDocument(path, ArxmlParser.ParseFile(path)))
            .ToList();

        return new ArxmlWorkspace(documents);
    }

    /// <summary>
    /// Finds identifiable objects by SHORT-NAME, semantic path, or AUTOSAR element type.
    /// Matching is case-insensitive and intentionally simple; richer query syntax belongs
    /// in a later query layer built on the same index.
    /// </summary>
    public IEnumerable<ArxmlObject> Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return _objects.Where(item =>
            item.ShortName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.ElementName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<ArxmlReference> Incoming(string targetPath) =>
        _references.Where(reference =>
            string.Equals(reference.TargetPath, targetPath, StringComparison.Ordinal));

    public IEnumerable<ArxmlReference> Outgoing(string sourcePath) =>
        _references.Where(reference =>
            string.Equals(reference.SourcePath, sourcePath, StringComparison.Ordinal));

    private void IndexElement(ArxmlElement element, string filePath, string currentPath)
    {
        var path = currentPath;
        var shortName = element.ChildText("SHORT-NAME");

        if (!string.IsNullOrWhiteSpace(shortName))
        {
            path = AppendPath(currentPath, shortName);
            _objects.Add(new ArxmlObject(path, shortName, element.Name, filePath, element));
        }

        foreach (var child in element.Children)
        {
            if (path.Length > 0 && IsReferenceElement(child) && !string.IsNullOrWhiteSpace(child.Text))
            {
                _references.Add(new ArxmlReference(
                    path,
                    child.Text!,
                    child.Name,
                    child.Attribute("DEST"),
                    filePath));
            }

            IndexElement(child, filePath, path);
        }
    }

    private static bool IsReferenceElement(ArxmlElement element) =>
        element.Name.EndsWith("-REF", StringComparison.Ordinal) ||
        element.Name.EndsWith("-TREF", StringComparison.Ordinal);

    private static string AppendPath(string parent, string shortName) =>
        parent.Length == 0 ? $"/{shortName}" : $"{parent}/{shortName}";
}
