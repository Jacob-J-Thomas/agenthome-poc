using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityLifecycleSelectionServiceTests
{
    [Fact]
    public async Task Upgrade_selection_derives_full_preview_request_from_server_resolver()
    {
        var kind = CapabilityLifecycleOperationKind.Upgrade;
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

    [Fact]
    public async Task Enable_selection_reproves_only_the_exact_current_lifecycle_target()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var resolver = new StubCapabilityLifecycleTargetResolver { Resolution = new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Ambiguous, null, null, "retained upgrade candidates are ambiguous") };
        var artifactEvidence = new StubCapabilityLifecycleArtifactEvidenceSource();
        var preview = Preview(manifest, CapabilityLifecycleOperationKind.Enable, CapabilityLifecyclePreviewStatus.Ready);
        var store = new StubCapabilityLifecycleMutationStore
        {
            ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Available, new CapabilityLifecycleState(manifest.Descriptor, manifest.Checksum, false, false, 7, "disable-current", DateTimeOffset.Parse("2026-08-01T12:00:00Z")), [], [], 7, "available"),
            PreviewResult = preview
        };
        var service = Service(resolver, store, artifactEvidence);

        var result = await service.PreviewAsync(new CapabilityLifecycleSelectionRequest("enable-current", CapabilityLifecycleOperationKind.Enable, manifest.Descriptor.Id, manifest.Descriptor.Version));

        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, result.Status);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(manifest.Descriptor, artifactEvidence.Descriptor);
        Assert.Equal(manifest.Checksum, artifactEvidence.ArtifactDigest);
        Assert.Equal(manifest.Descriptor, store.PreviewRequest!.TargetDescriptor);
        Assert.Equal(manifest.Checksum, store.PreviewRequest.TargetArtifactDigest);
    }

    [Theory]
    [InlineData(CapabilityLifecycleReadStatus.NotFound, CapabilityLifecycleSelectionStatus.NotFound)]
    [InlineData(CapabilityLifecycleReadStatus.RecoveredLastProved, CapabilityLifecycleSelectionStatus.Unavailable)]
    [InlineData(CapabilityLifecycleReadStatus.Unavailable, CapabilityLifecycleSelectionStatus.Unavailable)]
    public async Task Enable_requires_one_current_authenticated_lifecycle_entry(CapabilityLifecycleReadStatus readStatus, CapabilityLifecycleSelectionStatus expected)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var state = readStatus == CapabilityLifecycleReadStatus.NotFound ? null : new CapabilityLifecycleState(manifest.Descriptor, manifest.Checksum, false, false, 7, "disable-current", DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        var store = new StubCapabilityLifecycleMutationStore { ReadResult = new CapabilityLifecycleReadResult(readStatus, state, [], [], state is null ? null : 7, "read") };
        var resolver = new StubCapabilityLifecycleTargetResolver();

        var result = await Service(resolver, store).PreviewAsync(new CapabilityLifecycleSelectionRequest("enable-current-read", CapabilityLifecycleOperationKind.Enable, manifest.Descriptor.Id));

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Preview);
        Assert.Equal(0, resolver.Calls);
        Assert.Null(store.PreviewRequest);
    }

    [Fact]
    public async Task Enable_rejects_a_version_other_than_the_current_lifecycle_descriptor()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var store = new StubCapabilityLifecycleMutationStore
        {
            ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Available, new CapabilityLifecycleState(manifest.Descriptor, manifest.Checksum, false, false, 7, "disable-current", DateTimeOffset.Parse("2026-08-01T12:00:00Z")), [], [], 7, "available")
        };

        var result = await Service(new StubCapabilityLifecycleTargetResolver(), store).PreviewAsync(new CapabilityLifecycleSelectionRequest("enable-wrong-version", CapabilityLifecycleOperationKind.Enable, manifest.Descriptor.Id, CapabilityArtifactTestData.Version("2.0.0")));

        Assert.Equal(CapabilityLifecycleSelectionStatus.NotFound, result.Status);
        Assert.Null(store.PreviewRequest);
    }

    [Theory]
    [InlineData(CapabilityLifecycleArtifactEvidenceStatus.NotFound, CapabilityLifecycleSelectionStatus.NotFound)]
    [InlineData(CapabilityLifecycleArtifactEvidenceStatus.Unavailable, CapabilityLifecycleSelectionStatus.Unavailable)]
    public async Task Enable_fails_closed_when_the_exact_current_artifact_cannot_be_reproved(CapabilityLifecycleArtifactEvidenceStatus evidenceStatus, CapabilityLifecycleSelectionStatus expected)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var store = new StubCapabilityLifecycleMutationStore
        {
            ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Available, new CapabilityLifecycleState(manifest.Descriptor, manifest.Checksum, false, false, 7, "disable-current", DateTimeOffset.Parse("2026-08-01T12:00:00Z")), [], [], 7, "available")
        };
        var evidence = new StubCapabilityLifecycleArtifactEvidenceSource { Evidence = new CapabilityLifecycleArtifactEvidence(evidenceStatus, "evidence") };

        var result = await Service(new StubCapabilityLifecycleTargetResolver(), store, evidence).PreviewAsync(new CapabilityLifecycleSelectionRequest("enable-evidence", CapabilityLifecycleOperationKind.Enable, manifest.Descriptor.Id));

        Assert.Equal(expected, result.Status);
        Assert.Null(store.PreviewRequest);
        Assert.Equal(manifest.Descriptor, evidence.Descriptor);
        Assert.Equal(manifest.Checksum, evidence.ArtifactDigest);
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

    [Fact]
    public async Task Discard_requires_exact_persisted_evidence_and_is_idempotent_after_retirement()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var selection = new CapabilityLifecycleSelectionRequest("discard-selection", CapabilityLifecycleOperationKind.Disable, manifest.Descriptor.Id);
        var preview = Preview(manifest, CapabilityLifecycleOperationKind.Disable, CapabilityLifecyclePreviewStatus.Replayed) with { OperationId = selection.OperationId };
        var discarded = new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Discarded, null, 2, false, "discarded");
        var store = new StubCapabilityLifecycleMutationStore { SelectionReplayResult = preview, MutationResult = discarded };
        var service = Service(new StubCapabilityLifecycleTargetResolver(), store);
        var request = new CapabilityLifecycleDispositionRequest(selection, preview.BaselineCatalogRevision, preview.BaselineActivationRevision, preview.LifecycleRevision, preview.DependentSetRevision, preview.DependentSetHash, preview.PreviewHash);

        var stale = await service.DiscardAsync(request with { PreviewHash = CapabilityArtifactTestData.Manifest(content: "stale"u8.ToArray()).Checksum.Value });
        var result = await service.DiscardAsync(request);
        store.SelectionReplayResult = preview with { Status = CapabilityLifecyclePreviewStatus.NotFound };
        var replayedAfterRetirement = await service.DiscardAsync(request);
        var invalid = await service.DiscardAsync(request with { LifecycleRevision = 0 });

        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, stale.Status);
        Assert.Same(discarded, result);
        Assert.Same(preview, store.DiscardedPreview);
        Assert.Equal(CapabilityLifecycleMutationStatus.Discarded, replayedAfterRetirement.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Invalid, invalid.Status);
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.DiscardAsync(null!));
    }

    private static CapabilityLifecycleSelectionService Service(StubCapabilityLifecycleTargetResolver resolver, StubCapabilityLifecycleMutationStore store, StubCapabilityLifecycleArtifactEvidenceSource? artifactEvidence = null)
    {
        var lifecycle = new CapabilityLifecycleService(new StubCapabilityDependentIndex(), new StubCapabilityLifecycleBaselineSource(), artifactEvidence ?? new StubCapabilityLifecycleArtifactEvidenceSource(), store, new RecordingCapabilityAuditLog(), new StubCapabilityAuthorityTransaction());
        return new CapabilityLifecycleSelectionService(resolver, lifecycle);
    }

    private static CapabilityLifecyclePreview Preview(CapabilityArtifactManifest manifest, CapabilityLifecycleOperationKind kind, CapabilityLifecyclePreviewStatus status) => new(status, "sha256:workspace", "select-target", kind, manifest.Descriptor.Id, 1, 1, manifest.Checksum.Value, manifest.Checksum.Value, [], "preview", 1, 1, manifest.Descriptor, manifest.Checksum);
}
