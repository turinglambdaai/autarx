using Autarx.Core.Parsing;
using Xunit;

namespace Autarx.Tests;

public class EcucReaderTests
{
    private static List<Core.Models.EcucModule> ReadFixture() =>
        EcucReader.ReadModules(ArxmlParser.ParseFile(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal.arxml")));

    [Fact]
    public void Reads_both_modules()
    {
        var modules = ReadFixture();

        Assert.Equal(["Mcu", "Can"], modules.Select(m => m.ShortName));
        Assert.Equal("/AURIX2G/Mcu", modules[0].DefinitionRef);
        Assert.Equal("/AURIX2G/Can", modules[1].DefinitionRef);
    }

    [Fact]
    public void Reads_containers_recursively()
    {
        var mcu = ReadFixture()[0];

        var general = Assert.Single(mcu.Containers);
        Assert.Equal("McuGeneralConfiguration", general.ShortName);
        var sub = Assert.Single(general.SubContainers);
        Assert.Equal("McuModuleConfiguration", sub.ShortName);
    }

    [Fact]
    public void Reads_parameter_values()
    {
        var mcu = ReadFixture()[0];
        var general = mcu.Containers[0];

        var clockFailure = general.Parameters.Single(p => p.LeafName == "McuClockSrcFailureNotification");
        Assert.Equal("1", clockFailure.Value);

        var safety = general.Parameters.Single(p => p.LeafName == "McuSafetyEnable");
        Assert.Equal("ON", safety.Value);

        var frequency = general.SubContainers[0].Parameters[0];
        Assert.Equal("80000000", frequency.Value);
    }

    [Fact]
    public void Reads_reference_values()
    {
        var can = ReadFixture()[1];
        var general = can.Containers[0];

        var reference = Assert.Single(general.References);
        Assert.Equal("CanFilterRef", reference.LeafName);
        Assert.Equal("/Mcu/Mcu/McuModuleConfiguration", reference.ValueRef);
    }

    [Fact]
    public void Computes_totals()
    {
        var modules = ReadFixture();

        Assert.Equal(2, modules[0].TotalContainers);
        Assert.Equal(3, modules[0].TotalParameters);
        Assert.Equal(1, modules[1].TotalContainers);
        Assert.Equal(1, modules[1].TotalParameters);
    }
}
