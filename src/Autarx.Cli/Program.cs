using System.Reflection;

namespace Autarx.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();

        if (args[0] is "-h" or "--help" or "help")
            return Usage(0);

        if (args[0] is "--version" or "-v")
        {
            Console.WriteLine($"autarx {Version()}");
            return 0;
        }

        return args[0] switch
        {
            // workspace commands
            "inspect" => InspectCommand.Run(args[1..]),
            "find" => FindCommand.Run(args[1..]),
            "refs" => RefsCommand.Run(args[1..]),
            "trace" => TraceCommand.Run(args[1..]),
            "ecus" => EcusCommand.RunList(args[1..]),
            "ecu" => EcusCommand.RunDetail(args[1..]),
            "unresolved" => UnresolvedCommand.Run(args[1..]),
            "diff" => DiffCommand.Run(args[1..]),
            "impact" => ImpactCommand.Run(args[1..]),
            "comm" => CommCommand.Run(args[1..]),

            // single-file commands
            "info" => Commands.Info(args[1..]),
            "modules" => Commands.Modules(args[1..]),
            "validate" => Commands.Validate(args[1..]),

            _ => UnknownCommand(args[0]),
        };
    }

    internal static string Version()
    {
        var v = typeof(Program).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static int UnknownCommand(string name)
    {
        Console.Error.WriteLine($"autarx: unknown command '{name}' — try 'autarx --help'");
        return 2;
    }

    private static int Usage(int exitCode = 2)
    {
        Console.WriteLine($"""
            autarx {Version()} — AUTOSAR Integration Workbench

            Inspect, understand, compare, trace and automate OEM-to-supplier
            integration data. Autarx sits above the vendor generators — it does
            not replace DaVinci, tresos or ISOLAR, and it does not generate
            production BSW/RTE/MCAL code.

            Usage:
              autarx <command> [options] <file-or-directory>

            Workspace commands (file or directory):
              inspect     Summarize a workspace (files, semantic inventory, references)
              find        Find identifiables by SHORT-NAME substring
              refs        Show outgoing/incoming references of an object
              trace       Follow the reference graph from an object (BFS, depth-limited)
              ecus        List ECU instances in a workspace
              ecu         Show one ECU instance and its direct relations
              unresolved  List references whose target is not in the workspace
              diff        Semantic diff between two deliveries
                          diff <before> <after> [--detail]
              impact      ECU-scoped impact analysis across two deliveries
                          impact <before> <after> --ecu <name> [--detail]
              comm        Communication projection: clusters, frames, PDUs,
                          signals — and what is not attached anywhere
                          comm <workspace> [--cluster <name>]

            Single-file commands:
              info        Summarize a single ARXML file
              modules     List ECUC module configurations in a file
              validate    Structural checks only — XML correctness, workspace
                          integrity, broken references, duplicate paths.
                          NOT vendor validation: use DaVinci / tresos / ISOLAR
                          for production-grade validation.

            Options:
              --json      Machine-readable JSON output (camelCase, deterministic)
              -h, --help  Show this help
              --version   Show version

            Exit codes:
              0  success
              1  nothing matched (find/refs/trace/ecu) or findings reported
                 (validate/unresolved/diff/impact)
              2  usage, file, or parse error, or ambiguous object name
            """);
        return exitCode;
    }
}
