using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference.Profiles;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class InvocationPreparationReadyModelProfile
{
    private InvocationPreparationReadyModelProfile(CapabilityDescriptor descriptor, ModelProfileRuntimeProvider provider)
    {
        Descriptor = descriptor;
        Provider = provider;
    }

    internal CapabilityDescriptor Descriptor { get; }

    internal ModelProfileRuntimeProvider Provider { get; }

    internal static InvocationPreparationReadyModelProfile Create()
    {
        var descriptor = CreateDescriptor();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        Assert.True(CapabilityDataClass.TryParse("public", out var publicData, out _));
        var metadata = GovernedModelProfileMetadata.Create(
            1,
            identity!,
            "org.example",
            "invocation-preparation-ready-adapter",
            "invocation-preparation-model",
            "v1",
            1,
            CustomLoopTraceContentHash.Compute("invocation-preparation-ready-model.v1\n" + descriptor.Id.Value),
            "Test-only exact ready model adapter for invocation-preparation behavior.",
            [GovernedModelModality.Text],
            [GovernedModelCapability.ToolCalling, GovernedModelCapability.Streaming],
            1,
            1,
            GovernedModelPrivacyPosture.Create(
                1,
                GovernedModelLocality.LocalProcess,
                CapabilityEgressMode.None,
                [],
                [publicData!],
                ["local"],
                GovernedModelRetentionPosture.None,
                GovernedModelTrainingPosture.Prohibited),
            GovernedModelUsageSupportPolicy.Create(
                GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
                GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
                GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
                GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
                GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch),
            [],
            ["provider-inference"]);
        var sourceRevisionHash = CustomLoopTraceContentHash.Compute("invocation-preparation-ready-model-source.v1\n" + metadata.ContentHash);
        var registryRevisionHash = CustomLoopTraceContentHash.Compute("invocation-preparation-ready-model-registry.v1\n" + metadata.ContentHash);
        return new InvocationPreparationReadyModelProfile(
            descriptor,
            new ModelProfileRuntimeProvider(
                new InvocationPreparationReadyModelMetadataSource(descriptor.Id, metadata, sourceRevisionHash),
                new InvocationPreparationReadyModelAdapterRegistry(metadata.ContentHash, registryRevisionHash),
                _ => new InvocationPreparationUnavailableModelClientResolver()));
    }

    private static CapabilityDescriptor CreateDescriptor()
    {
        var template = BuiltInCapabilityCatalog.Descriptors.Single(item => item.Id.Value == BuiltInCapabilityCatalog.CodexModelProfileCapabilityId);
        Assert.True(CapabilityId.TryParse("org.example/model-profile/invocation-preparation-ready", out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var providerId, out _));
        return template with
        {
            Id = id!,
            Implementation = new CapabilityImplementationIdentity(providerId!, "model-profile/invocation-preparation-ready"),
            Provenance = new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://example.invalid/invocation-preparation-ready", "test-v1", null),
            Purpose = "Test-only ready model profile for invocation preparation.",
        };
    }
}
