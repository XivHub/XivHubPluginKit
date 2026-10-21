using System.Globalization;

namespace XivHubPluginKit.Tests;

public sealed class LogTemplateTests
{
    [Fact]
    public void FillsHolesPositionally()
        => Assert.Equal("Ham-a: 3 of 20", LogTemplate.Render("{Retainer}: {N} of {Of}", ["Ham-a", 3, 20]));

    [Fact]
    public void AppliesTheHoleFormatUnderTheInvariantCulture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            Assert.Equal("14d up, 14.03 raw",
                         LogTemplate.Render("{Days:0}d up, {Raw:0.00} raw", [14.0299, 14.0299]));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void MissingArgumentsRenderAsQuestionMarks()
        => Assert.Equal("a ?", LogTemplate.Render("{A} {B}", ["a"]));

    [Fact]
    public void NoArgumentsLeavesTheTemplateAlone()
        => Assert.Equal("{A}", LogTemplate.Render("{A}", []));
}
