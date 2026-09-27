using Autarx.Core.Index;
using Autarx.Core.Models;
using Autarx.Core.Parsing;

namespace Autarx.Cli;

/// <summary>Shared workspace loading and object resolution for CLI commands.
/// Error messages go to stderr; callers translate null results into exit
/// code 2.</summary>
internal static class WorkspaceInput
{
    public static WorkspaceIndex? Load(string path)
    {
        try
        {
            return WorkspaceIndex.Build(path);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"autarx: workspace not found: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"autarx: directory not found: {path}");
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"autarx: cannot read workspace: {ex.Message}");
        }
        catch (ArxmlParseException ex)
        {
            var location = ex.LineNumber > 0 ? $" (line {ex.LineNumber})" : "";
            Console.Error.WriteLine($"autarx: invalid ARXML{location}: {ex.Message}");
        }
        return null;
    }

    /// <summary>Resolves a name-or-path argument. NotFound exits 1 (nothing
    /// matched), Ambiguous exits 2 listing the candidates.</summary>
    public static SemanticObject? ResolveOrReport(WorkspaceIndex index, string nameOrPath)
    {
        var resolution = index.Resolve(nameOrPath);
        switch (resolution.Status)
        {
            case ResolveStatus.Found:
                return resolution.Object;

            case ResolveStatus.NotFound:
                Console.Error.WriteLine(
                    $"autarx: no workspace object matches '{nameOrPath}' " +
                    "(paths start with '/', names are exact SHORT-NAMEs)");
                return null;

            case ResolveStatus.Ambiguous:
                Console.Error.WriteLine(
                    $"autarx: '{nameOrPath}' is ambiguous, {resolution.Candidates.Count} objects match:");
                foreach (var candidate in resolution.Candidates)
                    Console.Error.WriteLine($"  {candidate.AbsolutePath}");
                return null;

            default:
                return null;
        }
    }
}
