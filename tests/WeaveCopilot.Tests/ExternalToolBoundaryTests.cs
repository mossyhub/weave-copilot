using GitHub.Copilot;
using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public class ExternalToolBoundaryTests
{
    [Fact]
    public void Hermes_mode_disables_native_tools_and_ambient_configuration()
    {
        var config = new SessionConfig { EnableConfigDiscovery = true, AvailableTools = ["bash"] };
        SdkHost.ApplyExternalToolBoundary(config, true);
        Assert.NotNull(config.AvailableTools);
        Assert.Empty(config.AvailableTools);
        Assert.False(config.EnableConfigDiscovery);
        Assert.True(config.SkipCustomInstructions);
    }

    [Fact]
    public void Normal_mode_keeps_existing_tool_configuration()
    {
        var config = new SessionConfig { EnableConfigDiscovery = true, AvailableTools = ["bash"] };
        SdkHost.ApplyExternalToolBoundary(config, false);
        Assert.Equal(["bash"], config.AvailableTools);
        Assert.True(config.EnableConfigDiscovery);
    }
}
