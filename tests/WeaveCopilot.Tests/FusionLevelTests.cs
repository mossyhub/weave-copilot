using GitHub.Copilot.Rpc;
using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public class FusionLevelTests
{
    [Fact]
    public void Pending_tier_acknowledgement_is_exact_and_requires_auto()
    {
        Assert.True(SdkHost.TierAcknowledged("auto", null, "provider-deep", "provider-deep"));
        Assert.True(SdkHost.TierAcknowledged("auto", "provider-deep", null, "provider-deep"));
        Assert.False(SdkHost.TierAcknowledged("auto", null, "other", "provider-deep"));
        Assert.False(SdkHost.TierAcknowledged("leaf-model", "provider-deep", null, "provider-deep"));
    }

    [Fact]
    public void Levels_use_enabled_provider_descriptors_not_guessed_ids()
    {
        var metadata = new AutoTierMetadata { Tiers = [
            new AutoTierDescriptor { Id="provider-balanced", FusionTier="standard", DisplayName="Standard", Status=new AutoTierStatus {Enabled=true, PickerVisible=true} },
            new AutoTierDescriptor { Id="provider-deep", FusionTier="high", DisplayName="High", Status=new AutoTierStatus {Enabled=true, PickerVisible=true} },
            new AutoTierDescriptor { Id="provider-hidden", FusionTier="max", DisplayName="Max", Status=new AutoTierStatus {Enabled=false, PickerVisible=true} }
        ] };
        var levels = SdkHost.FusionLevels(metadata);
        Assert.Equal("provider-balanced", levels["fusion-standard"]);
        Assert.Equal("provider-deep", levels["fusion-high"]);
        Assert.False(levels.ContainsKey("fusion-max"));
        Assert.Empty(SdkHost.FusionLevels(null));
    }
}
