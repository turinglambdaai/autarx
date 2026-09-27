using Autarx.Core.Vendor;
using Xunit;

namespace Autarx.Tests;

public class VendorAdapterTests
{
    private sealed class FakeRunner(VendorRunResult result) : IVendorToolRunner
    {
        public VendorInvocation? LastInvocation { get; private set; }

        public int LastTimeoutSeconds { get; private set; }

        public VendorRunResult Run(VendorInvocation invocation, int timeoutSeconds)
        {
            LastInvocation = invocation;
            LastTimeoutSeconds = timeoutSeconds;
            return result;
        }
    }

    [Fact]
    public void Finds_tool_via_home_environment_variable()
    {
        var adapter = VendorAdapters.Find("tresos")!;
        var home = Path.Combine("/opt", "tresos");
        var expected = Path.Combine(home, "bin", "tresos_cmd.bat");

        var detected = adapter.Detect(
            name => name == "TRESOS_HOME" ? home : null,
            candidate => candidate == expected ? candidate : null);

        Assert.NotNull(detected);
        Assert.Equal(expected, detected.ExecutablePath);
    }

    [Fact]
    public void Falls_back_to_path_when_home_is_unset()
    {
        var adapter = VendorAdapters.Find("davinci")!;

        var detected = adapter.Detect(
            _ => null,
            candidate => candidate == "DaVinciConfigurator.exe" ? @"C:\Vector\dv\DaVinciConfigurator.exe" : null);

        Assert.NotNull(detected);
        Assert.Equal(@"C:\Vector\dv\DaVinciConfigurator.exe", detected.ExecutablePath);
    }

    [Fact]
    public void Detection_failure_is_a_first_class_result()
    {
        var adapter = VendorAdapters.Find("isolar")!;

        var detected = adapter.Detect(_ => null, _ => null);

        Assert.Null(detected);
    }

    [Fact]
    public void Adapter_lookup_is_case_insensitive_and_total()
    {
        Assert.NotNull(VendorAdapters.Find("DaVinci"));
        Assert.Null(VendorAdapters.Find("nope"));
    }

    [Fact]
    public void Default_invocation_carries_project_and_working_directory()
    {
        var adapter = VendorAdapters.Find("davinci")!;
        var tool = new VendorToolInfo("davinci", @"C:\Vector\dv\DaVinciConfigurator.exe", null);
        var project = Path.Combine(".", "cfg", "my.dvcfg");

        var invocation = adapter.BuildInvocation(tool, project, null, []);

        Assert.Contains("my.dvcfg", invocation.Arguments);
        Assert.EndsWith("cfg", invocation.WorkingDirectory);
        Assert.Equal(tool.ExecutablePath, invocation.Executable);
    }

    [Fact]
    public void Template_override_replaces_project_placeholder()
    {
        var adapter = VendorAdapters.Find("tresos")!;
        var tool = new VendorToolInfo("tresos", "tresos_cmd.bat", null);
        var project = Path.Combine(".", "proj", "ecuc.arxml");

        var invocation = adapter.BuildInvocation(tool, project, "generate {project} --all", ["-v"]);

        Assert.Contains(Path.GetFullPath(project), invocation.Arguments);
        Assert.Contains("--all", invocation.Arguments);
        Assert.EndsWith(" -v", invocation.Arguments);
    }

    [Fact]
    public void Handoff_normalizes_diagnostics_and_writes_raw_output()
    {
        var runner = new FakeRunner(new VendorRunResult(
            1,
            "parsing done\r\nERROR: PduR config invalid\r\n[Warning] missing timeout\r\n",
            "line to stderr\r\n",
            TimedOut: false));
        var rawFile = Path.Combine(Path.GetTempPath(), $"autarx-test-{Guid.NewGuid():N}.log");

        try
        {
            var handoff = new VendorHandoff(runner);
            var tool = new VendorToolInfo("davinci", "dv.exe", null);
            var invocation = new VendorInvocation("dv.exe", "proj.dvcfg", ".");

            var report = handoff.Execute(tool, invocation, 60, rawFile);

            Assert.Equal(1, report.ExitCode);
            Assert.False(report.TimedOut);
            Assert.Equal(1, report.ErrorCount);
            Assert.Equal(1, report.WarningCount);
            Assert.Equal("error", report.Diagnostics[0].Severity);
            Assert.Equal("ERROR: PduR config invalid", report.Diagnostics[0].Message);
            Assert.True(File.Exists(rawFile));
            Assert.Contains("parsing done", File.ReadAllText(rawFile));
            Assert.Contains("line to stderr", File.ReadAllText(rawFile));
            Assert.Equal(60, runner.LastTimeoutSeconds);
        }
        finally
        {
            File.Delete(rawFile);
        }
    }

    [Fact]
    public void Timeout_reports_null_exit_code_and_no_crash()
    {
        var runner = new FakeRunner(new VendorRunResult(null, "", "", TimedOut: true));
        var rawFile = Path.Combine(Path.GetTempPath(), $"autarx-test-{Guid.NewGuid():N}.log");

        try
        {
            var handoff = new VendorHandoff(runner);
            var report = handoff.Execute(
                new VendorToolInfo("tresos", "tresos_cmd.bat", null),
                new VendorInvocation("tresos_cmd.bat", "proj", "."),
                30,
                rawFile);

            Assert.True(report.TimedOut);
            Assert.Null(report.ExitCode);
            Assert.Empty(report.Diagnostics);
        }
        finally
        {
            File.Delete(rawFile);
        }
    }
}
