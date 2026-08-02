using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityLifecycleSelectionServiceTests
{
    [Theory]
    [InlineData(CapabilityLifecycleOperationKind.Enable)]
    [InlineData(CapabilityLifecycleOperationKind.Upgrade)]
    public async Task Artifact_bearing_selection_derives_full_preview_request_from_server_resolver(CapabilityLifecycleOperationKind kind)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var resolver = new StubCapabilityLifecycleTargetResolver { Resolution = new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Available, manifest.Descriptor, manifest.Checksum, "available") };
        var preview = Preview(manifest, kind, CapabilityLifecyclePreviewStatus.Ready);
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = preview };
        var service = Service(resolver, store);

        var result = await service.PreviewAsync(new CapabilityLifecycleSelectionRequest("select-target", kind, manifest.Descriptor.Id, manifest.Descriptor.Version));

        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, result.Status);
        Assert.Same(preview, result.Preview);
        Assert.Equal(kind, resolver.Request!.Kind);
        Assert.Equal(manifest.Descriptor.Version, resolver.Request.TargetVersion);
        Assert.Equal(manifest.Descriptor, store.PreviewRequest!.TargetDescriptor);
        Assert.Equal(manifest.Checksum, store.PreviewRequest.TargetArtifactDigest);
    }

    [Theory]
    [InlineData(CapabilityLifecycleTargetResolutionStatus.NotFound, CapabilityLifecycleSelectionStatus.NotFound)]
    [InlineData(CapabilityLifecycleTargetResolutionStatus.Ambiguous, CapabilityLifecycleSelectionStatus.Ambiguous)]
    [InlineData(CapabilityLifecycleTargetResolutionStatus.Unavailable, CapabilityLifecycleSelectionStatus.Unavailable)]
    public async Task Fresh_resolver_fail_closed_outcomes_do_not_create_lifecycle_previews(CapabilityLifecycleTargetResolutionStatus resolutionStatus, CapabilityLifecycleSelectionStatus expected)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var resolver = new StubCapabilityLifecycleTargetResolver { Resolution = new CapabilityLifecycleTargetResolution(resolutionStatus, null, null, "resolution") };
        var store = new StubCapabilityLifecycleMutationStore();

        var result = await Service(resolver, store).PreviewAsync(new CapabilityLifecycleSelectionRequest("resolve-failure", CapabilityLifecycleOperationKind.Upgrade, manifest.Descriptor.Id));

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Preview);
        Assert.Null(store.PreviewRequest);
        Assert.NotNull(store.SelectionReplayRequest);
    }

    [Theory]
    [InlineData(CapabilityLifecycleTargetResolutionStatus.NotFound)]
    [InlineData(CapabilityLifecycleTargetResolutionStatus.Unavailable)]
    [InlineData(CapabilityLifecycleTargetResolutionStatus.Ambiguous)]
    public async Task Persisted_selection_replays_before_staged_target_evidence_changes(CapabilityLifecycleTargetResolutionStatus changedStatus)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var resolver = new StubCapabilityLifecycleTargetResolver { Resolution = new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Available, manifest.Descriptor, manifest.Checksum, "available") };
        var preview = Preview(manifest, CapabilityLifecycleOperationKind.Upgrade, CapabilityLifecyclePreviewStatus.Ready);
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = preview };
        var service = Service(resolver, store);
        var request = new CapabilityLifecycleSelectionRequest("select-target", CapabilityLifecycleOperationKind.Upgrade, manifest.Descriptor.Id, manifest.Descriptor.Version);

        var initial = await service.PreviewAsync(request);
        store.SelectionReplayResult = preview with { Status = CapabilityLifecyclePreviewStatus.Replayed, Detail = "replayed" };
        resolver.Resolution = new CapabilityLifecycleTargetResolution(changedStatus, null, null, "changed staged evidence");
        var replayed = await service.PreviewAsync(request);

        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, initial.Status);
        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, replayed.Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Replayed, replayed.Preview!.Status);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(1, store.PreviewCount);
    }

    [Fact]
    public async Task Conflicting_selection_reuse_is_rejected_before_current_staged_evidence()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var resolver = new StubCapabilityLifecycleTargetResolver { Resolution = new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Available, manifest.Descriptor, manifest.Checksum, "available") };
        var preview = Preview(manifest, CapabilityLifecycleOperationKind.Upgrade, CapabilityLifecyclePreviewStatus.Ready);
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = preview };
        var service = Service(resolver, store);
        var request = new CapabilityLifecycleSelectionRequest("select-target", CapabilityLifecycleOperationKind.Upgrade, manifest.Descriptor.Id, manifest.Descriptor.Version);
        _ = await service.PreviewAsync(request);
        store.SelectionReplayResult = preview with { Status = CapabilityLifecyclePreviewStatus.Conflict, Detail = "conflict" };
        resolver.Resolution = new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "unavailable");

        var conflict = await service.PreviewAsync(request with { TargetVersion = CapabilityArtifactTestData.Version("2.0.0") });

        Assert.Equal(CapabilityLifecycleSelectionStatus.Conflict, conflict.Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Conflict, conflict.Preview!.Status);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(1, store.PreviewCount);
    }

    [Fact]
    public async Task Non_target_transition_bypasses_resolver_and_rejects_client_version()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var resolver = new StubCapabilityLifecycleTargetResolver();
        var preview = Preview(manifest, CapabilityLifecycleOperationKind.Disable, CapabilityLifecyclePreviewStatus.Ready);
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = preview };
        var service = Service(resolver, store);

        var valid = await service.PreviewAsync(new CapabilityLifecycleSelectionRequest("disable-selection", CapabilityLifecycleOperationKind.Disable, manifest.Descriptor.Id));
        var invalid = await service.PreviewAsync(new CapabilityLifecycleSelectionRequest("disable-version", CapabilityLifecycleOperationKind.Disable, manifest.Descriptor.Id, manifest.Descriptor.Version));

        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, valid.Status);
        Assert.Equal(CapabilityLifecycleSelectionStatus.Invalid, invalid.Status);
        Assert.Equal(0, resolver.Calls);
        Assert.Null(store.PreviewRequest!.TargetDescriptor);
        Assert.Null(store.PreviewRequest.TargetArtifactDigest);
    }

    [Theory]
    [InlineData(CapabilityLifecyclePreviewStatus.NotFound, CapabilityLifecycleSelectionStatus.NotFound)]
    [InlineData(CapabilityLifecyclePreviewStatus.Invalid, CapabilityLifecycleSelectionStatus.Invalid)]
    [InlineData(CapabilityLifecyclePreviewStatus.Conflict, CapabilityLifecycleSelectionStatus.Conflict)]
    [InlineData(CapabilityLifecyclePreviewStatus.Unavailable, CapabilityLifecycleSelectionStatus.Unavailable)]
    public async Task Lifecycle_preview_outcomes_are_mapped_without_losing_the_opaque_preview(CapabilityLifecyclePreviewStatus previewStatus, CapabilityLifecycleSelectionStatus expected)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var preview = Preview(manifest, CapabilityLifecycleOperationKind.Disable, previewStatus);
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = preview };

        var result = await Service(new StubCapabilityLifecycleTargetResolver(), store).PreviewAsync(new CapabilityLifecycleSelectionRequest("map-preview", CapabilityLifecycleOperationKind.Disable, manifest.Descriptor.Id));

        Assert.Equal(expected, result.Status);
        Assert.Same(preview, result.Preview);
    }

    [Fact]
    public async Task Invalid_browser_selection_and_cancellation_fail_before_mutation_authority()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var resolver = new StubCapabilityLifecycleTargetResolver { Resolution = new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Available, manifest.Descriptor, manifest.Checksum, "available") };
        var store = new StubCapabilityLifecycleMutationStore();
        var service = Service(resolver, store);
        var invalid = await service.PreviewAsync(new CapabilityLifecycleSelectionRequest(string.Empty, CapabilityLifecycleOperationKind.Enable, manifest.Descriptor.Id));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(CapabilityLifecycleSelectionStatus.Invalid, invalid.Status);
        Assert.Equal(0, resolver.Calls);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PreviewAsync(new CapabilityLifecycleSelectionRequest("cancel-selection", CapabilityLifecycleOperationKind.Enable, manifest.Descriptor.Id), cancellation.Token));
    }

    [Fact]
    public async Task Opaque_confirmation_is_forwarded_to_existing_lifecycle_service()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var preview = Preview(manifest, CapabilityLifecycleOperationKind.Enable, CapabilityLifecyclePreviewStatus.Ready);
        var mutation = new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Applied, null, 2, false, "applied");
        var store = new StubCapabilityLifecycleMutationStore { MutationResult = mutation };
        var service = Service(new StubCapabilityLifecycleTargetResolver(), store);

        var result = await service.MutateAsync(preview);

        Assert.Same(mutation, result);
        Assert.Same(preview, store.MutatedPreview);
    }

    private static CapabilityLifecycleSelectionService Service(StubCapabilityLifecycleTargetResolver resolver, StubCapabilityLifecycleMutationStore store)
    {
        var lifecycle = new CapabilityLifecycleService(new StubCapabilityDependentIndex(), new StubCapabilityLifecycleBaselineSource(), new StubCapabilityLifecycleArtifactEvidenceSource(), store, new RecordingCapabilityAuditLog(), new StubCapabilityAuthorityTransaction());
        return new CapabilityLifecycleSelectionService(resolver, lifecycle);
    }

    private static CapabilityLifecyclePreview Preview(CapabilityArtifactManifest manifest, CapabilityLifecycleOperationKind kind, CapabilityLifecyclePreviewStatus status) => new(status, "sha256:workspace", "select-target", kind, manifest.Descriptor.Id, 1, 1, manifest.Checksum.Value, manifest.Checksum.Value, [], "preview", 1, 1, manifest.Descriptor, manifest.Checksum);
}
