using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Inference.Profiles;

internal static class ModelProfilePersistenceTestData
{
    internal static GovernedModelProfileMetadata Metadata(string profileId = "org.example/model-a", long configurationRevision = 1, char configurationHash = 'a')
    {
        var descriptor = CapabilityCatalogTestData.Descriptor(profileId) with
        {
            Kind = CapabilityKind.ModelProfile,
            Purpose = "A safe server-owned test model profile."
        };
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        var privacy = GovernedModelPrivacyPosture.Create(
            1,
            GovernedModelLocality.LocalProcess,
            CapabilityEgressMode.None,
            [],
            [],
            ["us"],
            GovernedModelRetentionPosture.None,
            GovernedModelTrainingPosture.Prohibited);
        var support = GovernedModelUsageSupportPolicy.Create(
            GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
            GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
            GovernedModelUsageSupport.Unavailable,
            GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
            GovernedModelUsageSupport.AuthoritativeAfterDispatch);
        return GovernedModelProfileMetadata.Create(
            1,
            identity!,
            "org.example",
            "codex-app-server",
            "gpt-5",
            "v1",
            configurationRevision,
            new string(configurationHash, 64),
            "A safe server-owned test model profile.",
            [GovernedModelModality.Text],
            [GovernedModelCapability.ToolCalling],
            128_000,
            8_192,
            privacy,
            support,
            [],
            ["inference"]);
    }

    internal static CapabilityId ProfileId(string value = "org.example/model-a")
    {
        Assert.True(CapabilityId.TryParse(value, out var profileId, out var error), error?.Message);
        return profileId!;
    }
}
