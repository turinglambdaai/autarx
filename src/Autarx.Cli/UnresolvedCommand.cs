using Autarx.Core.Index;

namespace Autarx.Cli;

internal static class UnresolvedCommand
{
    /// <summary>`autarx unresolved <workspace>` — references whose target is
    /// not in the workspace. This is the seed of the future OEM delivery
    /// quality check; findings make the exit code 1 so CI can gate on it.</summary>
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var workspace, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var index = WorkspaceInput.Load(workspace);
        if (index is null)
            return 2;

        var unresolved = index.UnresolvedReferences;

        if (json)
        {
            JsonOutput.Write(unresolved.Select(RefsCommand.ToDto).ToList());
            return unresolved.Count > 0 ? 1 : 0;
        }

        foreach (var reference in unresolved)
            Console.WriteLine(
                $"{reference.SourceElementPath,-60} -[{reference.Kind}]-> {reference.TargetPath} " +
                $"({Path.GetFileName(reference.SourceFile)})");

        Console.WriteLine($"{unresolved.Count} unresolved reference(s)");
        return unresolved.Count > 0 ? 1 : 0;
    }

    private static bool TryParseArgs(string[] args, out string workspace, out bool json, out string? error)
    {
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
            else if (workspace.Length == 0)
            {
                workspace = arg;
            }
            else
            {
                error = "autarx: exactly one workspace path expected";
                return false;
            }
        }

        if (workspace.Length == 0)
            error = "autarx: workspace path required — try 'autarx --help'";

        return error is null;
    }
}
