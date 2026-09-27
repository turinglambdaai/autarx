using Autarx.Core.Vendor;

namespace Autarx.Cli;

internal static class VendorCommand
{
    /// <summary>`autarx vendor list|validate` — vendor tool discovery and
    /// validation hand-off. The report relays the vendor tool's own result;
    /// it is never an Autarx verdict.</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0)
            return Usage();

        return args[0] switch
        {
            "list" => RunList(args[1..]),
            "validate" => RunValidate(args[1..]),
            "-h" or "--help" or "help" => Usage(0),
            _ => UnknownSubcommand(args[0]),
        };
    }

    private static int RunList(string[] args)
    {
        var json = args.Any(a => a == "--json");
        if (args.Any(a => a.StartsWith('-') && a != "--json"))
        {
            Console.Error.WriteLine("autarx: unknown option — try 'autarx vendor list --help'");
            return 2;
        }

        var found = DetectAll();
        if (json)
        {
            JsonOutput.Write(found.Select(t => new ToolJson(t.Tool.Name, t.DisplayName, t.Tool.ExecutablePath)).ToList());
            return found.Count == 0 ? 1 : 0;
        }

        if (found.Count == 0)
        {
            Console.Error.WriteLine("autarx: no vendor tools detected — set DAVINCI_HOME / TRESOS_HOME / ISOLAR_HOME or put the tool on PATH");
            return 1;
        }

        Console.WriteLine("detected vendor tools:");
        foreach (var (tool, displayName) in found)
            Console.WriteLine($"  {tool.Name,-10} {displayName,-24} {tool.ExecutablePath}");
        return 0;
    }

    private static int RunValidate(string[] args)
    {
        if (!TryParseValidateArgs(args, out var toolName, out var project, out var exeOverride, out var template, out var timeoutSeconds, out var json, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var adapter = VendorAdapters.Find(toolName);
        if (adapter is null)
        {
            Console.Error.WriteLine($"autarx: unknown vendor tool '{toolName}' — try 'autarx vendor list'");
            return 2;
        }

        if (!File.Exists(project))
        {
            Console.Error.WriteLine($"autarx: project not found: {project}");
            return 2;
        }

        VendorToolInfo? tool;
        if (exeOverride is not null)
        {
            if (!File.Exists(exeOverride))
            {
                Console.Error.WriteLine($"autarx: --exe not found: {exeOverride}");
                return 2;
            }
            tool = new VendorToolInfo(adapter.Name, Path.GetFullPath(exeOverride), null);
        }
        else
        {
            tool = adapter.Detect(Environment.GetEnvironmentVariable, ResolveOnPath);
            if (tool is null)
            {
                Console.Error.WriteLine($"autarx: {adapter.DisplayName} not detected — point --exe at the tool executable");
                return 2;
            }
        }

        var invocation = adapter.BuildInvocation(tool, project, template, []);
        var rawOutputFile = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(project))!,
            ".autarx",
            $"vendor-{adapter.Name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

        var handoff = new VendorHandoff(new ProcessToolRunner());
        VendorReport report;
        try
        {
            report = handoff.Execute(tool, invocation, timeoutSeconds, rawOutputFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"autarx: vendor hand-off failed: {ex.Message}");
            return 2;
        }

        if (json)
        {
            JsonOutput.Write(new ReportJson(
                report.Tool,
                report.Executable,
                report.Arguments,
                report.ExitCode,
                report.TimedOut,
                report.DurationMilliseconds,
                report.ErrorCount,
                report.WarningCount,
                report.Diagnostics.Select(d => new DiagnosticJson(d.Severity, d.Message)).ToList(),
                report.RawOutputFile));
        }
        else
        {
            Console.WriteLine($"{adapter.DisplayName} hand-off (relayed vendor result — not an autarx verdict)");
            Console.WriteLine($"  command: {report.Executable} {report.Arguments}");
            Console.WriteLine($"  exit: {(report.TimedOut ? "timeout" : report.ExitCode?.ToString() ?? "n/a")} in {report.DurationMilliseconds} ms");
            Console.WriteLine($"  diagnostics: {report.ErrorCount} error(s), {report.WarningCount} warning(s)");
            foreach (var diagnostic in report.Diagnostics.Take(20))
                Console.WriteLine($"    [{diagnostic.Severity}] {diagnostic.Message}");
            if (report.Diagnostics.Count > 20)
                Console.WriteLine($"    … {report.Diagnostics.Count - 20} more (see raw output)");
            Console.WriteLine($"  raw output: {report.RawOutputFile}");
        }

        return report.TimedOut ? 2 : report.ExitCode == 0 && report.ErrorCount == 0 ? 0 : 1;
    }

    private static List<(VendorToolInfo Tool, string DisplayName)> DetectAll()
    {
        var found = new List<(VendorToolInfo, string)>();
        foreach (var adapter in VendorAdapters.All)
        {
            var tool = adapter.Detect(Environment.GetEnvironmentVariable, ResolveOnPath);
            if (tool is not null)
                found.Add((tool, adapter.DisplayName));
        }
        return found;
    }

    /// <summary>Resolves a bare executable name against PATH, or checks an
    /// absolute candidate for existence.</summary>
    private static string? ResolveOnPath(string candidate)
    {
        if (Path.IsPathRooted(candidate))
            return File.Exists(candidate) ? candidate : null;

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
            return null;

        return pathVariable
            .Split(Path.PathSeparator)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(dir => Path.Combine(dir.Trim('"'), candidate))
            .FirstOrDefault(File.Exists);
    }

    private static int Usage(int exitCode = 2)
    {
        Console.WriteLine("""
            autarx vendor — vendor tool discovery and validation hand-off

            Autarx relays the vendor tool's own result; it is never an autarx
            verdict, and no vendor generation is reimplemented or simulated.

            Usage:
              autarx vendor list [--json]
                  Detect DaVinci / tresos / ISOLAR installations (HOME env vars, PATH).

              autarx vendor validate <tool> <project> [options]
                  Hand the project to the vendor tool and normalize its output.
                  <tool>    davinci | tresos | isolar
                  <project> vendor project file (e.g. .dvcfg)
                  --exe <path>      explicit tool executable (else autodetected)
                  --args "<tmpl>"   argument template override, {project} placeholder
                  --timeout <sec>   kill the tool after this long (default 600)
                  --json            machine-readable report

            Exit codes (validate):
              0  tool ran and reported no errors
              1  tool ran and reported failure (nonzero exit or errors)
              2  autarx-side problem: unknown tool, not detected, timeout
            """);
        return exitCode;
    }

    private static int UnknownSubcommand(string name)
    {
        Console.Error.WriteLine($"autarx: unknown vendor subcommand '{name}' — try 'autarx vendor --help'");
        return 2;
    }

    internal sealed record ToolJson(string Name, string DisplayName, string ExecutablePath);

    internal sealed record ReportJson(
        string Tool,
        string Executable,
        string Arguments,
        int? ExitCode,
        bool TimedOut,
        long DurationMilliseconds,
        int ErrorCount,
        int WarningCount,
        IReadOnlyList<DiagnosticJson> Diagnostics,
        string RawOutputFile);

    internal sealed record DiagnosticJson(string Severity, string Message);

    private static bool TryParseValidateArgs(
        string[] args,
        out string tool,
        out string project,
        out string? exe,
        out string? template,
        out int timeoutSeconds,
        out bool json,
        out string? error)
    {
        tool = "";
        project = "";
        exe = null;
        template = null;
        timeoutSeconds = 600;
        json = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                json = true;
            }
            else if (arg == "--exe")
            {
                if (i + 1 >= args.Length) { error = "autarx: --exe requires a value"; return false; }
                exe = args[++i];
            }
            else if (arg == "--args")
            {
                if (i + 1 >= args.Length) { error = "autarx: --args requires a value"; return false; }
                template = args[++i];
            }
            else if (arg == "--timeout")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out timeoutSeconds) || timeoutSeconds <= 0)
                {
                    error = "autarx: --timeout requires a positive number of seconds";
                    return false;
                }
            }
            else if (arg.StartsWith('-'))
            {
                error = $"autarx: unknown option '{arg}' — try 'autarx vendor --help'";
                return false;
            }
            else if (tool.Length == 0)
            {
                tool = arg;
            }
            else if (project.Length == 0)
            {
                project = arg;
            }
            else
            {
                error = "autarx: expected <tool> <project>";
                return false;
            }
        }

        if (tool.Length == 0 || project.Length == 0)
            error = "autarx: expected <tool> <project> — try 'autarx vendor --help'";

        return error is null;
    }
}
