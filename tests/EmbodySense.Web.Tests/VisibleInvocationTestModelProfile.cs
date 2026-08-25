using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference.Profiles;

namespace EmbodySense.Web.Tests;

internal sealed class VisibleInvocationTestModelProfile
{
    private VisibleInvocationTestModelProfile(ModelProfileRuntimeProvider provider)
    {
        Provider = provider;
    }

    internal ModelProfileRuntimeProvider Provider { get; }

    internal static VisibleInvocationTestModelProfile Create()
    {
        var descriptor = BuiltInCapabilityCatalog.Descriptors.Single(item => item.Id.Value == BuiltInCapabilityCatalog.CodexModelProfileCapabilityId);
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        Assert.True(CapabilityDataClass.TryParse("sensitive", out var sensitiveData, out _));
        var metadata = GovernedModelProfileMetadata.Create(
            1,
            identity!,
            "org.embodysense",
            "visible-invocation-test-adapter",
            "gpt-test",
            "v1",
            1,
            CustomLoopTraceContentHash.Compute("visible-invocation-test-model.v1\n" + descriptor.Id.Value),
            "Test-only exact ready model adapter for visible invocation host behavior.",
            [GovernedModelModality.Text],
            [GovernedModelCapability.ToolCalling, GovernedModelCapability.Streaming],
            1,
            1,
            GovernedModelPrivacyPosture.Create(
                1,
                GovernedModelLocality.LocalProcess,
                CapabilityEgressMode.None,
                [],
                [sensitiveData!],
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
        var sourceRevisionHash = CustomLoopTraceContentHash.Compute("visible-invocation-test-model-source.v1\n" + metadata.ContentHash);
        var registryRevisionHash = CustomLoopTraceContentHash.Compute("visible-invocation-test-model-registry.v1\n" + metadata.ContentHash);
        return new VisibleInvocationTestModelProfile(new ModelProfileRuntimeProvider(
            new VisibleInvocationTestModelMetadataSource(descriptor.Id, metadata, sourceRevisionHash),
            new VisibleInvocationTestModelAdapterRegistry(metadata.ContentHash, registryRevisionHash),
            _ => new VisibleInvocationTestModelClientResolver()));
    }
}
