using System.Runtime.InteropServices;
using thuvu.Tools.UIAutomation;

namespace thuvu.Tests.Tools;

public class UIAutomationFactoryTests
{
    [Fact]
    public void Create_ReturnsNonNullProvider()
    {
        var provider = UIAutomationFactory.Create();
        Assert.NotNull(provider);
    }

    [Fact]
    public void Create_ReturnsPlatformSpecificProvider()
    {
        var provider = UIAutomationFactory.Create();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Equal("Windows", provider.PlatformName);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Assert.Equal("Linux", provider.PlatformName);
    }

    [Fact]
    public void IsSupported_ReturnsTrueOnWindowsAndLinux()
    {
        Assert.True(UIAutomationFactory.IsSupported());
    }

    [Fact]
    public void Create_DisposeDoesNotThrow()
    {
        var provider = UIAutomationFactory.Create();
        var exception = Record.Exception(() => provider.Dispose());
        Assert.Null(exception);
    }
}
