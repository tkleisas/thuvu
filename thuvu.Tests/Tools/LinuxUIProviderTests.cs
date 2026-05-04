using System.Runtime.InteropServices;
using thuvu.Tools.UIAutomation;

namespace thuvu.Tests.Tools;

public class LinuxUIProviderTests
{
    [Fact]
    public void LinuxUIProvider_PlatformNameIsCorrect()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return; // Skip on non-Linux

        var provider = new thuvu.Tools.UIAutomation.Linux.LinuxUIProvider();
        Assert.Equal("Linux", provider.PlatformName);
    }

    [Fact]
    public async Task ListWindowsAsync_ReturnsResult()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return; // Skip on non-Linux

        var provider = new thuvu.Tools.UIAutomation.Linux.LinuxUIProvider();
        var windows = await provider.ListWindowsAsync(false);

        Assert.NotNull(windows);
    }

    [Fact]
    public async Task GetMousePositionAsync_ReturnsValidTuple()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return; // Skip on non-Linux

        var provider = new thuvu.Tools.UIAutomation.Linux.LinuxUIProvider();
        var (x, y) = await provider.GetMousePositionAsync();

        Assert.True(x >= 0);
        Assert.True(y >= 0);
    }
}
