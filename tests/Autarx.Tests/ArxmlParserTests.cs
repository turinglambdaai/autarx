using Autarx.Core.Models;
using Autarx.Core.Parsing;
using Xunit;

namespace Autarx.Tests;

public class ArxmlParserTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal.arxml");

    [Fact]
    public void Parses_fixture_root_element()
    {
        var root = ArxmlParser.ParseFile(FixturePath);
        Assert.Equal("AUTOSAR", root.Name);
    }

    [Fact]
    public void Strips_namespace_prefixes()
    {
        var root = ArxmlParser.Parse(
            """
            <?xml version="1.0"?>
            <a:AUTOSAR xmlns:a="http://autosar.org/schema/r4.0">
              <a:SHORT-NAME>x</a:SHORT-NAME>
            </a:AUTOSAR>
            """);

        Assert.Equal("AUTOSAR", root.Name);
        Assert.Equal("x", root.ChildText("SHORT-NAME"));
    }

    [Fact]
    public void Reads_attributes_by_local_name()
    {
        var root = ArxmlParser.Parse(
            """
            <?xml version="1.0"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xsi:schemaLocation="http://autosar.org/schema/r4.0 AUTOSAR_4-3-0.xsd" />
            """);

        Assert.Equal("http://autosar.org/schema/r4.0 AUTOSAR_4-3-0.xsd", root.Attribute("schemaLocation"));
    }

    [Fact]
    public void Counts_fixture_elements()
    {
        var root = ArxmlParser.ParseFile(FixturePath);

        Assert.Equal(2, root.Descendants().Count(e => e.Name == "ECUC-MODULE-CONFIGURATION-VALUES"));
        Assert.Equal(3, root.Descendants().Count(e => e.Name == "ECUC-CONTAINER-VALUE"));
    }

    [Fact]
    public void Invalid_xml_reports_line_number()
    {
        var ex = Assert.Throws<ArxmlParseException>(() =>
            ArxmlParser.Parse("<AUTOSAR>\n<BROKEN>\n</AUTOSAR>"));

        Assert.Equal(3, ex.LineNumber);
    }

    [Fact]
    public void Empty_input_throws()
    {
        Assert.Throws<ArxmlParseException>(() => ArxmlParser.Parse(""));
    }
}
