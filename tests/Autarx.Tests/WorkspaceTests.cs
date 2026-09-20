using Autarx.Core.Workspace;
using Xunit;

namespace Autarx.Tests;

public class WorkspaceTests
{
    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal.arxml");

    [Fact]
    public void Builds_identifiable_paths_across_arxml()
    {
        var workspace = ArxmlWorkspace.Open(FixturePath());

        var item = Assert.Single(workspace.Find("McuModuleConfiguration"));
        Assert.Equal("/Mcu/Mcu/McuGeneralConfiguration/McuModuleConfiguration", item.Path);
        Assert.Equal("ECUC-CONTAINER-VALUE", item.ElementName);
    }

    [Fact]
    public void Traces_incoming_and_outgoing_references()
    {
        var workspace = ArxmlWorkspace.Open(FixturePath());

        var outgoing = workspace.Outgoing("/Can/Can/CanGeneralConfiguration").ToList();
        Assert.Contains(outgoing, reference =>
            reference.ReferenceKind == "VALUE-REF" &&
            reference.TargetPath == "/Mcu/Mcu/McuModuleConfiguration");

        var incoming = workspace.Incoming("/Mcu/Mcu/McuModuleConfiguration").ToList();
        Assert.Contains(incoming, reference =>
            reference.SourcePath == "/Can/Can/CanGeneralConfiguration" &&
            reference.ReferenceKind == "VALUE-REF");
    }
}
