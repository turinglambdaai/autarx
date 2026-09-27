using System.Diagnostics;

namespace Autarx.Core.Vendor;

/// <summary>Abstraction over process execution so vendor hand-offs can be
/// tested with canned results.</summary>
public interface IVendorToolRunner
{
    VendorRunResult Run(VendorInvocation invocation, int timeoutSeconds);
}

/// <summary>Runs the vendor tool as a child process, capturing both streams.
/// Batch files go through cmd.exe (they are not directly executable).
/// On timeout the child is killed and TimedOut is reported — exit code stays
/// null because the tool never finished.</summary>
public sealed class ProcessToolRunner : IVendorToolRunner
{
    public VendorRunResult Run(VendorInvocation invocation, int timeoutSeconds)
    {
        var isBatch = executableIsBatch(invocation.Executable);
        var start = new ProcessStartInfo
        {
            FileName = isBatch ? "cmd.exe" : invocation.Executable,
            Arguments = isBatch ? $"/c \"{invocation.Executable}\" {invocation.Arguments}" : invocation.Arguments,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = Process.Start(start);
        if (process is null)
            return new VendorRunResult(null, "", $"failed to start {invocation.Executable}", false);

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        var timedOut = !process.WaitForExit(timeoutSeconds * 1000);
        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited between wait and kill */ }
        }

        return new VendorRunResult(
            timedOut ? null : process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult(),
            timedOut);
    }

    private static bool executableIsBatch(string executable) =>
        executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        || executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
}
