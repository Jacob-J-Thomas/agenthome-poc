using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Tests.Inference.Profiles;

public sealed class GovernedModelRoutingAdmissionServiceTests
{
    [Fact]
    public async Task No_inference_nodes_produce_exact_empty_routing_without_reading_profile_dependencies()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact();
        var workspaceId = "workspace-sha256:" + Hash('1');
        var binding = GovernedLoopExecutionBinding.Create(1, "run-no-inference", artifact.RevisionArtifact.Revision, 1);
        var receipt = GovernedLoopSequentialApplicationTestFixture.AdmissionReceipt(
            artifact,
            binding,
            workspaceId,
            "admit-no-inference",
            Hash('2'),
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var evidence = receipt.Evidence;
        var seed = new GovernedModelRoutingAdmissionSeed(
            receipt.Intent,
            binding,
            evidence.GrantProfile,
            evidence.GrantBoundary,
            evidence.GrantDependencyEvidenceHash,
            evidence.EffectiveAuthority,
            evidence.CapabilityAdmission,
            evidence.EvaluatedAtUtc);
        var dependencies = new ThrowingProfileDependencies();
        var service = new GovernedModelRoutingAdmissionService(dependencies, dependencies, dependencies, dependencies);

        var result = await service.AdmitAsync(new GovernedModelRoutingAdmissionRequest(seed, []));

        Assert.Equal(GovernedModelRoutingAdmissionStatus.Admitted, result.Status);
        var snapshot = Assert.IsType<GovernedModelRoutingAdmissionSnapshot>(result.Snapshot);
        Assert.Empty(snapshot.Entries);
        Assert.Null(snapshot.CapabilityCatalogRevision);
        Assert.Null(snapshot.ResolvedDefaultProfileId);
        Assert.Null(snapshot.DefaultSourceRevisionHash);
        Assert.Null(snapshot.AdapterRegistryRevisionHash);
        Assert.Equal(0, dependencies.ReadCalls);
        var expected = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(
            receipt.Intent,
            binding,
            evidence.GrantProfile,
            evidence.GrantBoundary,
            evidence.GrantDependencyEvidenceHash,
            evidence.EffectiveAuthority,
            evidence.CapabilityAdmission,
            evidence.EvaluatedAtUtc);
        Assert.Equal(expected.ContentHash, snapshot.ContentHash);
    }

    private sealed class ThrowingProfileDependencies :
        ICapabilityCatalogStore,
        IModelProfileMetadataSource,
        IModelProfileDefaultSource,
        IModelProfileAdapterRegistry
    {
        internal int ReadCalls { get; private set; }

        public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
            => Throw<CapabilityCatalogReadResult>();

        public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId profileId, CancellationToken cancellationToken = default)
            => Throw<ModelProfileSourceReadResult>();

        public Task<ModelProfileDefaultReadResult> ReadAsync(CancellationToken cancellationToken = default)
            => Throw<ModelProfileDefaultReadResult>();

        public Task<ModelProfileAdapterPosture> ReadPostureAsync(GovernedModelProfileMetadata metadata, CancellationToken cancellationToken = default)
            => Throw<ModelProfileAdapterPosture>();

        private Task<T> Throw<T>()
        {
            ReadCalls++;
            throw new InvalidOperationException("No profile dependency may be read for an explicit empty routing admission.");
        }
    }

    private static string Hash(char value) => new(value, 64);
}
