using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Autarx.Core.Vendor;

/// <summary>
/// Executes a vendor hand-off and normalizes the tool's own output into a
/// structured report. Autarx never judges the engineering result — severity
/// tags come from a loose line heuristic (a line mentioning "error" / 
/// "warning"), everything else stays in the raw output file.
/// </summary>
public sealed class VendorHandoff(IVendorToolRunner runner)
{
    private static readonly Regex ErrorPattern = new(@"\berror\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WarningPattern = new(@"\bwarning\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public VendorReport Execute(
        VendorToolInfo tool,
        VendorInvocation invocation,
        int timeoutSeconds,
        string rawOutputFile)
    {
        var clock = Stopwatch.StartNew();
        var result = runner.Run(invocation, timeoutSeconds);
        clock.Stop();

        var combined = result.StandardOutput + result.StandardError;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(rawOutputFile))!);
        File.WriteAllText(rawOutputFile, combined);

        var diagnostics = ParseDiagnostics(result);
        return new VendorReport(
            tool.Name,
            invocation.Executable,
            invocation.Arguments,
            invocation.WorkingDirectory,
            result.ExitCode,
            result.TimedOut,
            clock.ElapsedMilliseconds,
            diagnostics,
            diagnostics.Count(d => d.Severity == "error"),
            diagnostics.Count(d => d.Severity == "warning"),
            rawOutputFile);
    }

    internal static IReadOnlyList<VendorDiagnostic> ParseDiagnostics(VendorRunResult result)
    {
        var lines = (result.StandardOutput + result.StandardError)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var diagnostics = new List<VendorDiagnostic>();
        foreach (var line in lines)
        {
            var severity = ErrorPattern.IsMatch(line) ? "error"
                : WarningPattern.IsMatch(line) ? "warning"
                : null;

            if (severity is not null)
                diagnostics.Add(new VendorDiagnostic(severity, line, line));
        }

        return diagnostics;
    }
}
