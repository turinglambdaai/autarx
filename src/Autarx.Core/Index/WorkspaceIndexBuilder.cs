using Autarx.Core.Models;
using Autarx.Core.Parsing;

namespace Autarx.Core.Index;

public static class WorkspaceIndexBuilder
{
    public static WorkspaceIndex Build(string path)
    {
        var fullPath = Path.GetFullPath(path);

        string[] files;
        var singleFile = File.Exists(fullPath);
        if (singleFile)
        {
            files = [fullPath];
        }
        else if (Directory.Exists(fullPath))
        {
            files = Directory.EnumerateFiles(fullPath, "*.arxml", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        }
        else
        {
            throw new FileNotFoundException("workspace path not found", fullPath);
        }

        var documents = new List<WorkspaceDocument>();
        var objects = new List<SemanticObject>();
        var references = new List<AutosarReference>();
        var fileErrors = new List<WorkspaceFileError>();
        var packageCount = 0;

        foreach (var file in files)
        {
            if (singleFile)
            {
                // An explicitly named file is a hard input: parse errors abort.
                CollectFile(file, documents, objects, references, ref packageCount);
            }
            else
            {
                // In a directory scan one broken file must not sink the
                // workspace — record and continue.
                try
                {
                    CollectFile(file, documents, objects, references, ref packageCount);
                }
                catch (ArxmlParseException ex)
                {
                    fileErrors.Add(new WorkspaceFileError(file, ex.Message, ex.LineNumber));
                }
                catch (IOException ex)
                {
                    fileErrors.Add(new WorkspaceFileError(file, ex.Message, 0));
                }
            }
        }

        var sortedObjects = objects
            .OrderBy(o => o.AbsolutePath, StringComparer.Ordinal)
            .ThenBy(o => o.SourceFile, StringComparer.Ordinal)
            .ToList();

        var pathIndex = new HashSet<string>(
            sortedObjects.Select(o => o.AbsolutePath), StringComparer.Ordinal);

        foreach (var reference in references)
            reference.IsResolved = pathIndex.Contains(reference.TargetPath);

        var sortedReferences = references
            .OrderBy(r => r.SourcePath, StringComparer.Ordinal)
            .ThenBy(r => r.TargetPath, StringComparer.Ordinal)
            .ThenBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.SourceFile, StringComparer.Ordinal)
            .ToList();

        var duplicates = sortedObjects
            .GroupBy(o => o.AbsolutePath, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new WorkspaceDuplicatePath(
                g.Key,
                g.Select(o => o.SourceFile).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList()))
            .OrderBy(d => d.AbsolutePath, StringComparer.Ordinal)
            .ToList();

        return new WorkspaceIndex(
            documents.OrderBy(d => d.FilePath, StringComparer.Ordinal).ToList(),
            sortedObjects,
            sortedReferences,
            duplicates,
            fileErrors.OrderBy(e => e.FilePath, StringComparer.Ordinal).ToList(),
            packageCount);
    }

    private static void CollectFile(
        string file,
        List<WorkspaceDocument> documents,
        List<SemanticObject> objects,
        List<AutosarReference> references,
        ref int packageCount)
    {
        var root = ArxmlParser.ParseFile(file);
        var state = new WalkState(file);

        var namespaceUri = root.Attribute("xmlns");
        var schemaLocation = root.Attribute("schemaLocation");
        var elementCount = 1 + root.Descendants().Count();

        if (root.Element("AR-PACKAGES") is { } packages)
            foreach (var package in packages.Elements("AR-PACKAGE"))
                VisitPackage(package, "", state);

        documents.Add(new WorkspaceDocument
        {
            FilePath = file,
            FileSizeBytes = new FileInfo(file).Length,
            Namespace = namespaceUri,
            SchemaLocation = schemaLocation,
            AutosarRelease = AutosarReleaseParser.Derive(schemaLocation),
            ElementCount = elementCount,
            IdentifiableCount = state.Objects.Count,
            PackageCount = state.PackageCount,
            NamedElementCount = state.NamedElementCount,
            SemanticCounts = state.KindCounts,
        });

        objects.AddRange(state.Objects);
        references.AddRange(state.References);
        packageCount += state.PackageCount;
    }

    private static void VisitPackage(ArxmlElement package, string parentPath, WalkState state)
    {
        state.PackageCount++;
        var packagePath = AppendSegment(parentPath, package.ChildText("SHORT-NAME") ?? "(unnamed)");

        if (package.Element("ELEMENTS") is { } elements)
            foreach (var element in elements.Children)
                VisitIdentifiable(element, packagePath, state);

        if (package.Element("AR-PACKAGES") is { } subPackages)
            foreach (var sub in subPackages.Elements("AR-PACKAGE"))
                VisitPackage(sub, packagePath, state);
    }

    private static void VisitIdentifiable(ArxmlElement element, string packagePath, WalkState state)
    {
        var shortName = element.ChildText("SHORT-NAME");
        if (shortName is null)
            return; // schema-invalid identifiable: nothing to index under

        var path = AppendSegment(packagePath, shortName);
        var kind = SemanticClassifier.Classify(element.Name);

        state.Objects.Add(new SemanticObject
        {
            ShortName = shortName,
            ElementType = element.Name,
            SemanticKind = kind,
            AbsolutePath = path,
            SourceFile = state.File,
        });
        state.NamedElementCount++;
        if (kind != SemanticKind.Unknown)
            state.KindCounts[kind] = state.KindCounts.GetValueOrDefault(kind) + 1;

        VisitContent(element, path, state, nestedChain: "");
    }

    /// <summary>
    /// Walks everything below an identifiable: nested identifiables contribute
    /// semantic counts and longer attribution chains; *REF elements become
    /// references attributed to the owning object.
    /// </summary>
    private static void VisitContent(ArxmlElement element, string ownerPath, WalkState state, string nestedChain)
    {
        foreach (var child in element.Children)
        {
            if (IsReference(child))
            {
                state.References.Add(new AutosarReference
                {
                    Kind = child.Name,
                    SourcePath = ownerPath,
                    SourceElementPath = ownerPath + nestedChain,
                    TargetPath = child.Text!,
                    SourceFile = state.File,
                });
                continue;
            }

            var childShortName = child.ChildText("SHORT-NAME");
            if (childShortName is not null)
            {
                var kind = SemanticClassifier.Classify(child.Name);
                state.NamedElementCount++;
                if (kind != SemanticKind.Unknown)
                    state.KindCounts[kind] = state.KindCounts.GetValueOrDefault(kind) + 1;
            }

            VisitContent(child, ownerPath, state, childShortName is null ? nestedChain : nestedChain + "/" + childShortName);
        }
    }

    private static bool IsReference(ArxmlElement element) =>
        element.Name.EndsWith("REF", StringComparison.Ordinal)
        && element.Name != "DEFINITION-REF"
        && element.Text is { } target
        && target.StartsWith('/');

    private static string AppendSegment(string path, string segment) => path + "/" + segment;

    private sealed class WalkState(string file)
    {
        public string File { get; } = file;

        public List<SemanticObject> Objects { get; } = [];

        public List<AutosarReference> References { get; } = [];

        public int PackageCount { get; set; }

        public int NamedElementCount { get; set; }

        public Dictionary<SemanticKind, int> KindCounts { get; } = [];
    }
}
