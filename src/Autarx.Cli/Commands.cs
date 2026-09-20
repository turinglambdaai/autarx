using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autarx.Core.Models;
using Autarx.Core.Parsing;
using Autarx.Core.Validation;
using Autarx.Core.Workspace;

namespace Autarx.Cli;

internal static class Commands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Info(string[] args)
    {
        if (!TryParseArgs(args, out var file, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        return WithLoadedFile(file, json, (root, modules, elapsed) =>
        {
            var size = new FileInfo(file).Length;
            var elementCount = 1 + root.Descendants().Count();

            var result = new InfoJson(
                file,
                size,
                elementCount,
                modules.Count,
                [.. modules.Select(m => m.ShortName)],
                elapsed);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }

            Console.WriteLine($"file            {file}");
            Console.WriteLine($"size            {size:N0} B");
            Console.WriteLine($"elements        {elementCount:N0}");
            Console.WriteLine($"ecuc modules    {modules.Count}{ModulesSuffix(modules)}");
            Console.WriteLine($"parsed in       {elapsed} ms");
            return 0;
        });
    }

    public static int Modules(string[] args)
    {
        if (!TryParseArgs(args, out var file, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        return WithLoadedFile(file, json, (_, modules, _) =>
        {
            var rows = modules
                .Select(m => new ModuleJson(
                    m.ShortName,
                    m.DefinitionRef,
                    m.TotalContainers,
                    m.TotalParameters))
                .ToList();

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(rows, JsonOptions));
                return 0;
            }

            foreach (var row in rows)
                Console.WriteLine(
                    $"{row.ShortName,-20} {row.DefinitionRef ?? "(no definition-ref)",-40} " +
                    $"containers={row.Containers} parameters={row.Parameters}");
            return 0;
        });
    }

    public static int Validate(string[] args)
    {
        if (!TryParseArgs(args, out var file, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        return WithLoadedFile(file, json, (_, modules, _) =>
        {
            var diagnostics = ValidationEngine.Validate(modules);
            var result = new ValidateJson(
                diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
                diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
                [.. diagnostics.Select(d => new DiagnosticJson(
                    d.Code,
                    d.Severity.ToString().ToLowerInvariant(),
                    d.Message,
                    d.Path))]);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                foreach (var d in diagnostics)
                    Console.WriteLine(
                        $"{d.Severity.ToString().ToUpperInvariant(),-7} {d.Code} {d.Path}: {d.Message}");
                Console.WriteLine(
                    $"{result.Errors} error(s), {result.Warnings} warning(s)");
            }

            return result.Errors > 0 ? 1 : 0;
        });
    }

    public static int Find(string[] args)
    {
        if (!TryParseQueryArgs(args, out var query, out var input, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        return WithWorkspace(input, workspace =>
        {
            var matches = workspace.Find(query)
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(item => new FindJson(
                    item.Path,
                    item.ShortName,
                    item.ElementName,
                    item.FilePath))
                .ToArray();

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(matches, JsonOptions));
                return 0;
            }

            foreach (var match in matches)
                Console.WriteLine($"{match.Path}  [{match.ElementName}]  {match.File}");

            if (matches.Length == 0)
                Console.WriteLine("no matches");

            return 0;
        });
    }

    public static int Refs(string[] args)
    {
        if (!TryParseQueryArgs(args, out var query, out var input, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        return WithWorkspace(input, workspace =>
        {
            var objects = workspace.Find(query);
            if (query.StartsWith('/'))
                objects = objects.Where(item => string.Equals(item.Path, query, StringComparison.Ordinal));

            var rows = new List<ReferenceJson>();
            foreach (var item in objects.OrderBy(item => item.Path, StringComparer.Ordinal))
            {
                rows.AddRange(workspace.Incoming(item.Path).Select(reference =>
                    new ReferenceJson(
                        "in",
                        item.Path,
                        reference.SourcePath,
                        reference.TargetPath,
                        reference.ReferenceKind,
                        reference.Destination,
                        reference.FilePath)));

                rows.AddRange(workspace.Outgoing(item.Path).Select(reference =>
                    new ReferenceJson(
                        "out",
                        item.Path,
                        reference.SourcePath,
                        reference.TargetPath,
                        reference.ReferenceKind,
                        reference.Destination,
                        reference.FilePath)));
            }

            var result = new RefsJson(query, rows.ToArray());

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }

            if (rows.Count == 0)
            {
                Console.WriteLine("no references");
                return 0;
            }

            foreach (var row in rows)
            {
                var arrow = row.Direction == "in" ? "<-" : "->";
                var other = row.Direction == "in" ? row.SourcePath : row.TargetPath;
                Console.WriteLine($"{row.ObjectPath} {arrow} {other}  [{row.ReferenceKind}]");
            }

            return 0;
        });
    }

    private static int WithLoadedFile(
        string file,
        bool json,
        Func<ArxmlElement, List<EcucModule>, long, int> action)
    {
        try
        {
            var start = Environment.TickCount64;
            var root = ArxmlParser.ParseFile(file);
            var modules = EcucReader.ReadModules(root);
            var elapsed = Environment.TickCount64 - start;
            return action(root, modules, elapsed);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"autarx: file not found: {file}");
            return 2;
        }
        catch (DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"autarx: directory not found: {Path.GetDirectoryName(Path.GetFullPath(file))}");
            return 2;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"autarx: cannot read {file}: {ex.Message}");
            return 2;
        }
        catch (ArxmlParseException ex)
        {
            var location = ex.LineNumber > 0 ? $" (line {ex.LineNumber})" : "";
            Console.Error.WriteLine($"autarx: invalid ARXML{location}: {ex.Message}");
            return 2;
        }
    }

    private static int WithWorkspace(string input, Func<ArxmlWorkspace, int> action)
    {
        try
        {
            return action(ArxmlWorkspace.Open(input));
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"autarx: input not found: {input}");
            return 2;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"autarx: cannot access {input}: {ex.Message}");
            return 2;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"autarx: cannot read {input}: {ex.Message}");
            return 2;
        }
        catch (ArxmlParseException ex)
        {
            var location = ex.LineNumber > 0 ? $" (line {ex.LineNumber})" : "";
            Console.Error.WriteLine($"autarx: invalid ARXML{location}: {ex.Message}");
            return 2;
        }
    }

    private static bool TryParseArgs(string[] args, out string file, out bool json, out string? error)
    {
        file = "";
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
            else if (file.Length == 0)
            {
                file = arg;
            }
            else
            {
                error = "autarx: exactly one input file expected";
                return false;
            }
        }

        if (file.Length == 0)
            error = "autarx: input file required — try 'autarx --help'";

        return error is null;
    }

    private static bool TryParseQueryArgs(
        string[] args,
        out string query,
        out string input,
        out bool json,
        out string? error)
    {
        query = "";
        input = "";
        json = false;
        error = null;
        var positional = new List<string>();

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
            else
            {
                positional.Add(arg);
            }
        }

        if (positional.Count != 2)
        {
            error = "autarx: expected <query> <file-or-directory> — try 'autarx --help'";
            return false;
        }

        query = positional[0];
        input = positional[1];
        return true;
    }

    private static string ModulesSuffix(List<EcucModule> modules) =>
        modules.Count > 0 ? $" ({string.Join(", ", modules.Select(m => m.ShortName))})" : "";

    internal sealed record InfoJson(
        string File,
        long SizeBytes,
        int ElementCount,
        int ModuleCount,
        string[] Modules,
        long ElapsedMs);

    internal sealed record ModuleJson(
        string ShortName,
        string? DefinitionRef,
        int Containers,
        int Parameters);

    internal sealed record ValidateJson(
        int Errors,
        int Warnings,
        DiagnosticJson[] Diagnostics);

    internal sealed record DiagnosticJson(
        string Code,
        string Severity,
        string Message,
        string Path);

    internal sealed record FindJson(
        string Path,
        string ShortName,
        string ElementName,
        string File);

    internal sealed record RefsJson(
        string Query,
        ReferenceJson[] References);

    internal sealed record ReferenceJson(
        string Direction,
        string ObjectPath,
        string SourcePath,
        string TargetPath,
        string ReferenceKind,
        string? Destination,
        string File);
}
