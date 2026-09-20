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
            "info" => Commands.Info(args[1..]),
            "modules" => Commands.Modules(args[1..]),
            "validate" => Commands.Validate(args[1..]),
            "find" => Commands.Find(args[1..]),
            "refs" => Commands.Refs(args[1..]),
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
            autarx {Version()} — AUTOSAR integration workbench

            Usage:
              autarx <command> [options] <input>

            Commands:
              info       Summarize one ARXML file (legacy ECUC-oriented view)
              modules    List ECUC module configurations in one ARXML file
              validate   Run structural ECUC validation in one ARXML file
              find       Find AUTOSAR objects by SHORT-NAME/path/type in a file or directory
              refs       Show incoming/outgoing references for matching objects

            Examples:
              autarx find VehicleSpeed ./oem-delivery --json
              autarx refs /Can/Can/CanGeneralConfiguration ./project

            Options:
              --json     Machine-readable JSON output
              -h, --help Show this help
              --version  Show version

            Exit codes:
              0  success
              1  validation reported errors (validate only)
              2  usage, file, or parse error
            """);
        return exitCode;
    }
}
