using System.Reflection;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Tests;

public class AppInfoTests
{
    [Theory]
    [InlineData(1, 2, 0, "v1.2.0")]
    [InlineData(1, 2, 3, "v1.2.3")]
    [InlineData(10, 0, 5, "v10.0.5")]
    public void Format_UsesMajorMinorBuild(int major, int minor, int build, string expected)
        => Assert.Equal(expected, AppInfo.Format(new Version(major, minor, build)));

    [Fact]
    public void Format_Null_ReturnsEmpty()
        => Assert.Equal("", AppInfo.Format(null));

    [Fact]
    public void DisplayVersion_ReadsAssemblyVersion_InExpectedShape()
    {
        // Robust against version bumps: assert the shape, not a hardcoded number.
        var result = AppInfo.DisplayVersion(typeof(AppInfo).Assembly);
        Assert.Matches(@"^v\d+\.\d+\.\d+$", result);
    }
}
