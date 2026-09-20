using Autarx.Core.Models;

namespace Autarx.Cli;

internal static class FindCommand
{
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var pattern, out var workspace, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var index = WorkspaceInput.Load(workspace);
        if (index is null)
            return 2;

        var matches = index.FindByName(pattern);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"autarx: no objects match '{pattern}'");
            return 1;
        }

        if (json)
        {
            JsonOutput.Write(matches.Select(ToDto).ToList());
            return 0;
        }

        foreach (var match in matches)
            Console.WriteLine(
                $"{InspectCommand.KindText(match.SemanticKind),-22} {match.AbsolutePath,-52} {Path.GetFileName(match.SourceFile)}");
        return 0;
    }

    internal static ObjectJson ToDto(SemanticObject o) => new(
        o.ShortName,
        o.SemanticKind,
        o.ElementType,
        o.AbsolutePath,
        o.SourceFile);

    internal sealed record ObjectJson(
        string ShortName,
        SemanticKind SemanticKind,
        string ElementType,
        string AbsolutePath,
        string SourceFile);

    private static bool TryParseArgs(
        string[] args,
        out string pattern,
        out string workspace,
        out bool json,
        out string? error)
    {
        pattern = "";
        workspace = "";
        json = false;
        error = null;

        foreach (var arg in args)
        {
            if (arg == "--json")
            {
                json = true;
            }
            else if (arg.StartsWith('-'))
            {
                error = $"autarx: unknown option '{arg}' — try 'autarx --help'";
                return false;
            }
            else if (pattern.Length == 0)
            {
                pattern = arg;
            }
            else if (workspace.Length == 0)
            {
                workspace = arg;
            }
            else
            {
                error = "autarx: expected <pattern> <workspace>";
                return false;
            }
        }

        if (pattern.Length == 0 || workspace.Length == 0)
            error = "autarx: expected <pattern> <workspace> — try 'autarx --help'";

        return error is null;
    }
}
